using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Torrents;

/// <summary>
/// Drives the server-side download queue: starts queued items up to the concurrency limit,
/// records progress, moves completed files into the media folder and evicts idle stream caches.
/// </summary>
public class ServerDownloadService(IServiceScopeFactory scopes, TorrentEngine engine, ILogger<ServerDownloadService> log) : BackgroundService
{
    private DateTime _lastEviction = DateTime.MinValue;
    private DateTime _lastIdleStop = DateTime.MinValue;

    /// <summary>A stream nobody has read from for this long stops downloading (a paused film included).</summary>
    public static readonly TimeSpan StopIdleStreamsAfter = TimeSpan.FromMinutes(10);
    private DateTime _lastExpiry = DateTime.MinValue;
    private (int Down, int Up) _appliedLimits = (-1, -1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Download queue tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);

        if (_appliedLimits != (settings.MaxDownloadKbps, settings.MaxUploadKbps))
        {
            await engine.UpdateRateLimitsAsync(settings.MaxDownloadKbps, settings.MaxUploadKbps);
            _appliedLimits = (settings.MaxDownloadKbps, settings.MaxUploadKbps);
        }

        var active = await db.ServerDownloads.Include(d => d.Release).Include(d => d.Episode)
            .Where(d => d.Status == DownloadStatus.Queued || d.Status == DownloadStatus.FetchingMetadata || d.Status == DownloadStatus.Downloading)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

        // Each episode saved from a pack is a download of its own: the pack's torrent fetches just those episodes' files.
        // Ones already saved stay wanted, so they keep seeding (and aren't skipped) while others come in.
        var packs = active.Where(d => d.EpisodeId is not null).Select(d => d.ReleaseId).Distinct().ToList();
        if (packs.Count > 0)
        {
            var wanted = await db.ServerDownloads.AsNoTracking().Include(d => d.Episode)
                .Where(d => d.EpisodeId != null && packs.Contains(d.ReleaseId)
                            && d.Status != DownloadStatus.Cancelled && d.Status != DownloadStatus.Failed && d.Status != DownloadStatus.Paused)
                .ToListAsync(ct);
            foreach (var group in wanted.GroupBy(d => d.ReleaseId))
                await engine.KeepEpisodesAsync(active.First(d => d.ReleaseId == group.Key).Release!, group.Select(d => Choice(d)));
        }

        var running = active.Where(d => d.Status != DownloadStatus.Queued).Select(d => d.ReleaseId).Distinct().Count();
        foreach (var d in active)
        {
            var release = d.Release!;
            if (d.Status == DownloadStatus.Queued)
            {
                // Another episode from a torrent that's already running doesn't take another slot.
                var sameTorrent = active.Any(o => o != d && o.ReleaseId == d.ReleaseId && o.Status != DownloadStatus.Queued);
                if (!sameTorrent)
                {
                    if (running >= settings.MaxActiveDownloads) continue;
                    running++;
                }
                d.Status = DownloadStatus.FetchingMetadata;
                _ = StartAsync(d.Id, release, Choice(d), ct);
                continue;
            }

            var stats = engine.Stats(release.InfoHash, Choice(d), release.SeasonNumber);
            if (stats is null)
            {
                // Engine restarted (or add still in flight after a restart): kick it again.
                if (d.Status == DownloadStatus.Downloading) _ = StartAsync(d.Id, release, Choice(d), ct);
                continue;
            }

            engine.Touch(release.InfoHash);
            if (stats.Size > 0) d.Status = DownloadStatus.Downloading;
            d.Progress = Math.Round(stats.Progress, 2);
            if (stats.Complete)
            {
                var moreToCome = active.Any(o => o != d && o.ReleaseId == d.ReleaseId && o.Status != DownloadStatus.Completed);
                release.LocalFilePath = await engine.FinalizeDownloadAsync(release, settings.SeedAfterDownload, Choice(d), moreToCome)
                                        ?? release.LocalFilePath;
                d.Status = DownloadStatus.Completed;
                d.Progress = 100;
                d.CompletedAt = DateTime.UtcNow;
                log.LogInformation("Server download complete: {Name}", release.Name);
            }
        }
        await db.SaveChangesAsync(ct);

        if (DateTime.UtcNow - _lastIdleStop > TimeSpan.FromSeconds(30))
        {
            _lastIdleStop = DateTime.UtcNow;
            await engine.StopIdleStreamsAsync(StopIdleStreamsAfter);
        }

        if (DateTime.UtcNow - _lastEviction > TimeSpan.FromMinutes(10))
        {
            _lastEviction = DateTime.UtcNow;
            await engine.EvictIdleAsync(TimeSpan.FromHours(Math.Max(1, settings.StreamCacheHours)));
            await engine.EnforceCacheLimitAsync((long)settings.MaxStreamCacheGb << 30);
        }
        await engine.EnsureFreeSpaceAsync();

        if (settings.KeepServerDownloadsDays > 0 && DateTime.UtcNow - _lastExpiry > TimeSpan.FromMinutes(15))
        {
            _lastExpiry = DateTime.UtcNow;
            await DeleteExpiredAsync(db, settings.KeepServerDownloadsDays, ct);
        }
    }

    /// <summary>Deletes movies saved on the server more than <paramref name="days"/> days after they finished.</summary>
    private async Task DeleteExpiredAsync(PhantomDbContext db, int days, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var expired = await db.ServerDownloads.Include(d => d.Release).Include(d => d.Episode)
            .Where(d => d.Status == DownloadStatus.Completed && d.CompletedAt != null && d.CompletedAt < cutoff)
            .ToListAsync(ct);
        foreach (var d in expired)
        {
            log.LogInformation("Deleting {Name}: saved on the server more than {Days} days ago", d.Release!.Name, days);
            await RemoveAsync(db, engine, d, deleteFiles: true, ct);
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes a server download, stopping it if it's still going, and with <paramref name="deleteFiles"/> its files.
    /// An episode saved from a pack that other episodes were saved from too only takes its own file with it: the
    /// torrent carries on for the others.
    /// </summary>
    public static async Task RemoveAsync(PhantomDbContext db, TorrentEngine engine, ServerDownload d, bool deleteFiles, CancellationToken ct)
    {
        var release = d.Release!;
        var others = await db.ServerDownloads.Include(o => o.Episode)
            .Where(o => o.ReleaseId == d.ReleaseId && o.Id != d.Id && o.Status != DownloadStatus.Cancelled && o.Status != DownloadStatus.Failed)
            .ToListAsync(ct);
        db.ServerDownloads.Remove(d);

        if (others.Count > 0 && d.EpisodeId is not null)
        {
            await engine.KeepEpisodesAsync(release, others.Select(Choice));
            if (deleteFiles) engine.DeleteSavedEpisode(release, Choice(d));
        }
        else if (d.Status != DownloadStatus.Completed || deleteFiles)
        {
            await engine.RemoveAsync(release.InfoHash, deleteData: deleteFiles || d.Status != DownloadStatus.Completed);
            if (deleteFiles)
            {
                if (release.LocalFilePath is { } path && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                // A pack's episodes can sit in subfolders of the release's folder.
                if (Directory.Exists(engine.MediaFolder(release.InfoHash))) Directory.Delete(engine.MediaFolder(release.InfoHash), recursive: true);
                release.LocalFilePath = null;
            }
        }
        else
        {
            engine.Unpin(release.InfoHash);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The episode a download is of (in a pack, the file to fetch); the main video for a movie.</summary>
    public static VideoChoice Choice(ServerDownload d) =>
        d.Episode is { } e ? new VideoChoice(e.SeasonNumber, e.EpisodeNumber) : VideoChoice.Main;

    private async Task StartAsync(int downloadId, Release release, VideoChoice choice, CancellationToken ct)
    {
        string? error = null;
        try
        {
            var manager = await engine.EnsureStartedAsync(release, keep: true, ct, choice);
            if (TorrentEngine.Video(manager, choice, release.SeasonNumber) is null)
                error = "This release hasn't got that episode.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not start download {Name}", release.Name);
            error = ex.Message;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var d = await db.ServerDownloads.FindAsync([downloadId], ct);
        if (d is null || d.Status is DownloadStatus.Cancelled or DownloadStatus.Paused) return;
        d.Status = error is null ? DownloadStatus.Downloading : DownloadStatus.Failed;
        d.Error = error;
        await db.SaveChangesAsync(ct);
    }
}
