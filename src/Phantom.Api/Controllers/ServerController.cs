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

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/sources")]
public class SourcesController(PhantomDbContext db, SourceSyncService sync) : ControllerBase
{
    [HttpGet]
    public async Task<List<SourceDto>> List(CancellationToken ct) =>
        (await db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto).ToList();

    [HttpPost]
    public async Task<ActionResult<SourceDto>> Create(UpsertSourceRequest req, CancellationToken ct)
    {
        if (Validate(req) is { } error) return BadRequest(error);
        var s = new Source { Name = req.Name };
        Apply(s, req);
        db.Sources.Add(s);
        await db.SaveChangesAsync(ct);
        sync.RequestSync(s.Id);
        return ToDto(s);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<SourceDto>> Update(int id, UpsertSourceRequest req, CancellationToken ct)
    {
        if (Validate(req) is { } error) return BadRequest(error);
        var s = await db.Sources.FindAsync([id], ct);
        if (s is null) return NotFound();
        Apply(s, req);
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var s = await db.Sources.FindAsync([id], ct);
        if (s is null) return NotFound();
        db.Sources.Remove(s);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Triggers an immediate pull from one source (or all with id 0).</summary>
    [HttpPost("{id:int}/sync")]
    public IActionResult Sync(int id)
    {
        sync.RequestSync(id == 0 ? null : id);
        return Accepted();
    }

    private static string? Validate(UpsertSourceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return "Name is required.";
        if (req.Type == SourceType.Torznab && !Uri.TryCreate(req.BaseUrl, UriKind.Absolute, out _)) return "Torznab sources need an absolute base URL.";
        if (req.SyncIntervalMinutes < 15) return "Sync interval must be at least 15 minutes.";
        return null;
    }

    private static void Apply(Source s, UpsertSourceRequest req)
    {
        s.Name = req.Name.Trim();
        s.Type = req.Type;
        s.BaseUrl = req.BaseUrl;
        if (req.ApiKey is not null) s.ApiKey = req.ApiKey.Length == 0 ? null : req.ApiKey; // null = keep existing
        s.Categories = req.Categories;
        s.Query = req.Query;
        s.Enabled = req.Enabled;
        s.SyncIntervalMinutes = req.SyncIntervalMinutes;
    }

    private static SourceDto ToDto(Source s) =>
        new(s.Id, s.Name, s.Type, s.BaseUrl, !string.IsNullOrEmpty(s.ApiKey), s.Categories, s.Query, s.Enabled,
            s.SyncIntervalMinutes, s.LastSyncAt, s.LastSyncResult);
}
