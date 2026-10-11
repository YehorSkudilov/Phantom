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
            metadata.IsConfigured);
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("settings")]
    public async Task<ServerSettingsDto> GetSettings(CancellationToken ct)
    {
        var s = await db.Settings.AsNoTracking().FirstAsync(ct);
        return new ServerSettingsDto(s.ServerName, s.MaxActiveDownloads, s.MaxDownloadKbps, s.MaxUploadKbps,
            s.SeedAfterDownload, s.StreamCacheHours, s.AutoDownloadNewReleases, s.PreferredQuality, s.MinSeeders);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(ServerSettingsDto dto, CancellationToken ct)
    {
        if (dto.MaxActiveDownloads < 1 || dto.StreamCacheHours < 1 || dto.MinSeeders < 0)
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
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <summary>The built-in sources (see Program.SeedSourcesAsync): listed, switched on or off, synced. Nothing else.</summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/sources")]
public class SourcesController(PhantomDbContext db, SourceSyncService sync) : ControllerBase
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



    private static SourceDto ToDto(Source s) =>
        new(s.Id, s.Name, s.Type, s.BaseUrl, !string.IsNullOrEmpty(s.ApiKey), s.Categories, s.Query, s.Enabled,
            s.SyncIntervalMinutes, s.LastSyncAt, s.LastSyncResult);
}
