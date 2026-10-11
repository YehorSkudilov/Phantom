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

        var active = await db.ServerDownloads.Include(d => d.Release)
            .Where(d => d.Status == DownloadStatus.Queued || d.Status == DownloadStatus.FetchingMetadata || d.Status == DownloadStatus.Downloading)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

        var running = active.Count(d => d.Status != DownloadStatus.Queued);
        foreach (var d in active)
        {
            var release = d.Release!;
            if (d.Status == DownloadStatus.Queued)
            {
                if (running >= settings.MaxActiveDownloads) continue;
                running++;
                d.Status = DownloadStatus.FetchingMetadata;
                _ = StartAsync(d.Id, release, ct);
                continue;
            }

            var stats = engine.Stats(release.InfoHash);
            if (stats is null)
            {
                // Engine restarted (or add still in flight after a restart): kick it again.
                if (d.Status == DownloadStatus.Downloading) _ = StartAsync(d.Id, release, ct);
                continue;
            }

            engine.Touch(release.InfoHash);
            if (stats.Size > 0) d.Status = DownloadStatus.Downloading;
            d.Progress = Math.Round(stats.Progress, 2);
            if (stats.Complete)
            {
                release.LocalFilePath = await engine.FinalizeDownloadAsync(release.InfoHash, settings.SeedAfterDownload);
                d.Status = DownloadStatus.Completed;
                d.Progress = 100;
                d.CompletedAt = DateTime.UtcNow;
                log.LogInformation("Server download complete: {Name}", release.Name);
            }
        }
        await db.SaveChangesAsync(ct);

        if (DateTime.UtcNow - _lastEviction > TimeSpan.FromMinutes(10))
        {
            _lastEviction = DateTime.UtcNow;
            await engine.EvictIdleAsync(TimeSpan.FromHours(Math.Max(1, settings.StreamCacheHours)));
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
        var expired = await db.ServerDownloads.Include(d => d.Release)
            .Where(d => d.Status == DownloadStatus.Completed && d.CompletedAt != null && d.CompletedAt < cutoff)
            .ToListAsync(ct);
        foreach (var d in expired)
        {
            var release = d.Release!;
            log.LogInformation("Deleting {Name}: saved on the server more than {Days} days ago", release.Name, days);
            await engine.RemoveAsync(release.InfoHash, deleteData: true);
            if (release.LocalFilePath is { } path && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            release.LocalFilePath = null;
            db.ServerDownloads.Remove(d);
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
    }

    private async Task StartAsync(int downloadId, Release release, CancellationToken ct)
    {
        string? error = null;
        try
        {
            await engine.EnsureStartedAsync(release, keep: true, ct);
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
