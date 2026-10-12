using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Api.Services.Metadata;
using Phantom.Api.Services.Sources;
using Phantom.Api.Services.Torrents;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

[ApiController]
[Route("api/server")]
public class ServerController(PhantomDbContext db, TorrentEngine engine, Transcoder transcoder, IMetadataProvider metadata, IOptions<PhantomOptions> options) : ControllerBase
{
    /// <summary>Unauthenticated so clients can identify a server before signing in.</summary>
    [HttpGet("info")]
    public async Task<ServerInfoDto> Info(CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        long free = 0;
        try
        {
            free = new DriveInfo(Path.GetFullPath(options.Value.DataDirectory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Unknown drive layout (e.g. some containers); report 0.
        }
        return new ServerInfoDto(
            settings.ServerName,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            await db.Movies.CountAsync(m => m.Releases.Any(), ct),
            engine.ActiveTorrents,
            engine.TotalDownloadRate,
            engine.TotalUploadRate,
            free,
            transcoder.IsAvailable,
            metadata.IsConfigured,
            await db.Shows.CountAsync(s => s.Releases.Any(), ct));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("metadata")]
    public async Task<MetadataStatusDto> MetadataStatus([FromServices] Phantom.Api.Services.Metadata.IMetadataProvider metadata, CancellationToken ct) =>
        new(metadata.IsConfigured,
            await Phantom.Api.Services.Metadata.MetadataRefreshService.Due(db).CountAsync(ct) + await Phantom.Api.Services.Metadata.MetadataRefreshService.DueShows(db).CountAsync(ct),
            await db.Movies.CountAsync(ct) + await db.Shows.CountAsync(ct));

    /// <summary>Queues every movie and show for a metadata refresh; MetadataRefreshService works through them in the background.</summary>
    [Authorize(Roles = "Admin")]
    [HttpPost("metadata/refresh")]
    public async Task<MetadataStatusDto> RefreshAllMetadata([FromServices] Phantom.Api.Services.Metadata.IMetadataProvider metadata, CancellationToken ct)
    {
        await db.Movies.ExecuteUpdateAsync(m => m.SetProperty(x => x.MetadataRefreshedAt, (DateTime?)null), ct);
        await db.Shows.ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataRefreshedAt, (DateTime?)null), ct);
        return await MetadataStatus(metadata, ct);
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("settings")]
    public async Task<ServerSettingsDto> GetSettings(CancellationToken ct)
    {
        var s = await db.Settings.AsNoTracking().FirstAsync(ct);
        return new ServerSettingsDto(s.ServerName, s.MaxActiveDownloads, s.MaxDownloadKbps, s.MaxUploadKbps,
            s.SeedAfterDownload, s.StreamCacheHours, s.AutoDownloadNewReleases, s.PreferredQuality, s.MinSeeders, s.KeepServerDownloadsDays,
            s.MaxStreamCacheGb);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(ServerSettingsDto dto, CancellationToken ct)
    {
        if (dto.MaxActiveDownloads < 1 || dto.StreamCacheHours < 1 || dto.MinSeeders < 0 || dto.KeepServerDownloadsDays < 0 || dto.MaxStreamCacheGb < 0)
            return BadRequest("Invalid settings.");
        var s = await db.Settings.FirstAsync(ct);
        s.ServerName = dto.ServerName;
        s.MaxActiveDownloads = dto.MaxActiveDownloads;
        s.MaxDownloadKbps = Math.Max(0, dto.MaxDownloadKbps);
        s.MaxUploadKbps = Math.Max(0, dto.MaxUploadKbps);
        s.SeedAfterDownload = dto.SeedAfterDownload;
        s.StreamCacheHours = dto.StreamCacheHours;
        s.AutoDownloadNewReleases = dto.AutoDownloadNewReleases;
        s.PreferredQuality = dto.PreferredQuality;
        s.MinSeeders = dto.MinSeeders;
        s.KeepServerDownloadsDays = dto.KeepServerDownloadsDays;
        s.MaxStreamCacheGb = dto.MaxStreamCacheGb;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Storage ----------

    /// <summary>What's taking space: the disk, each part of the data folder, and every torrent's files, biggest first.</summary>
    [Authorize(Roles = "Admin")]
    [HttpGet("storage")]
    public async Task<StorageDto> Storage(CancellationToken ct)
    {
        var o = options.Value;
        long free = 0, total = 0;
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(o.DataDirectory));
            (free, total) = (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Unknown drive layout (e.g. some containers); report 0.
        }

        var stored = engine.StoredTorrents();
        var hashes = stored.Select(t => t.InfoHash).ToList();
        var releases = await db.Releases.AsNoTracking().Where(r => hashes.Contains(r.InfoHash))
            .Select(r => new { r.Id, r.InfoHash, r.Name, Movie = r.Movie!.Title, Show = r.Show!.Title, r.SeasonNumber, r.EpisodeNumber })
            .ToListAsync(ct);
        var downloads = await db.ServerDownloads.AsNoTracking().Where(d => hashes.Contains(d.Release!.InfoHash))
            .Select(d => new { d.Id, d.Release!.InfoHash }).ToListAsync(ct);

        var items = stored.Select(t =>
        {
            var r = releases.FirstOrDefault(x => x.InfoHash == t.InfoHash);
            var title = r is null ? t.Name ?? t.InfoHash
                : r.Movie ?? (r.Show is null ? r.Name
                    : r.EpisodeNumber is { } e ? $"{r.Show} · S{r.SeasonNumber:00}E{e:00}"
                    : r.SeasonNumber is { } s ? $"{r.Show} · Season {s}" : $"{r.Show} · Several seasons");
            return new StoredTorrentDto(t.InfoHash, title, t.Saved, t.SizeBytes, t.LastUsed, t.InUse,
                downloads.FirstOrDefault(d => d.InfoHash == t.InfoHash)?.Id);
        }).OrderByDescending(i => i.SizeBytes).ToList();

        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        return new StorageDto(free, total,
            items.Where(i => !i.SavedOnServer).Sum(i => i.SizeBytes),
            items.Where(i => i.SavedOnServer).Sum(i => i.SizeBytes),
            TorrentEngine.DirectorySize(Path.Combine(o.CacheDirectory, "thumbnails")),
            new[] { "", "-wal", "-shm" }.Sum(suffix => TorrentEngine.DirectorySize(o.DatabasePath + suffix)),
            settings.MaxStreamCacheGb, o.MinFreeDiskGb, items);
    }

    /// <summary>Clears one torrent's stream cache (not while it's being watched, and not a copy saved on the server).</summary>
    [Authorize(Roles = "Admin")]
    [HttpDelete("storage/cache/{infoHash}")]
    public async Task<IActionResult> ClearCache(string infoHash) =>
        await engine.ClearStreamCacheAsync(infoHash) ? NoContent() : Conflict("It's being watched, or it's saved on the server (remove it from Downloads).");

    /// <summary>Clears every stream cache nobody is watching from.</summary>
    [Authorize(Roles = "Admin")]
    [HttpPost("storage/cache/clear")]
    public async Task<StorageDto> ClearIdleCaches(CancellationToken ct)
    {
        await engine.ClearIdleStreamCachesAsync();
        return await Storage(ct);
    }

    /// <summary>Deletes every seek preview; they're made again as they're needed.</summary>
    [Authorize(Roles = "Admin")]
    [HttpPost("storage/previews/clear")]
    public async Task<StorageDto> ClearPreviews(CancellationToken ct)
    {
        var folder = Path.Combine(options.Value.CacheDirectory, "thumbnails");
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // One being written right now; the rest are gone.
        }
        return await Storage(ct);
    }
}

/// <summary>The built-in sources (see Program.SeedSourcesAsync): listed, switched on or off, synced. Nothing else.</summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/sources")]
public class SourcesController(PhantomDbContext db, SourceSyncService sync, IEnumerable<IMovieSource> providers) : ControllerBase
{
    [HttpGet]
    public async Task<List<SourceDto>> List(CancellationToken ct) =>
        (await db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto).ToList();

    [HttpPut("{id:int}/enabled")]
    public async Task<ActionResult<SourceDto>> SetEnabled(int id, [FromQuery] bool value, CancellationToken ct)
    {
        var s = await db.Sources.FindAsync([id], ct);
        if (s is null) return NotFound();
        s.Enabled = value;
        await db.SaveChangesAsync(ct);
        if (value) sync.RequestSync(id);
        return ToDto(s);
    }

    /// <summary>Triggers an immediate pull from one source (or all with id 0).</summary>
    [HttpPost("{id:int}/sync")]
    public IActionResult Sync(int id)
    {
        sync.RequestSync(id == 0 ? null : id);
        return Accepted();
    }



    private SourceDto ToDto(Source s) =>
        new(s.Id, s.Name, s.Type, s.BaseUrl, !string.IsNullOrEmpty(s.ApiKey), s.Categories, s.Query, s.Enabled,
            s.SyncIntervalMinutes, s.LastSyncAt, s.LastSyncResult,
            sync.IsSyncing(s.Id),
            providers.FirstOrDefault(p => p.Type == s.Type) is IBackfillingSource backfill ? backfill.CatalogueProgress(s) : null);
}
