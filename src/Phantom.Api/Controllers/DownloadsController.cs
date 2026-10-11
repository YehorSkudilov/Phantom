using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Api.Services.Torrents;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

/// <summary>
/// Server-side downloads: torrents the server fetches completely and keeps on its own disk
/// (as opposed to client downloads, which copy a file to a device for offline playback).
/// </summary>
[ApiController]
[Authorize]
[Route("api/downloads")]
public class DownloadsController(PhantomDbContext db, TorrentEngine engine) : ControllerBase
{
    [HttpGet]
    public async Task<List<ServerDownloadDto>> List(CancellationToken ct)
    {
        var rows = await db.ServerDownloads.AsNoTracking()
            .Include(d => d.Movie).Include(d => d.Release).Include(d => d.Episode).ThenInclude(e => e!.Show)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        _keepDays = (await db.Settings.AsNoTracking().FirstAsync(ct)).KeepServerDownloadsDays;
        return rows.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ServerDownloadDto>> Create(CreateServerDownloadRequest req, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        Release? release;
        Episode? episode = null;
        if (req.EpisodeId is { } episodeId)
        {
            // The release that has the episode, which may be its whole season.
            episode = await db.Episodes.Include(e => e.Show).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
            if (episode is null) return NotFound();
            var candidates = ShowLibraryService.OrderForPlayback(await db.Releases.Where(r => r.ShowId == episode.ShowId).ToListAsync(ct), episode, settings.PreferredQuality).ToList();
            release = (req.ReleaseId is { } rid ? candidates.FirstOrDefault(r => r.Id == rid) : null) ?? candidates.FirstOrDefault();
        }
        else
        {
            var releases = await db.Releases.Include(r => r.Movie).Where(r => r.MovieId == req.MovieId).ToListAsync(ct);
            release = req.ReleaseId is { } rid ? releases.FirstOrDefault(r => r.Id == rid) : LibraryService.PickRelease(releases, settings.PreferredQuality);
        }
        if (release is null) return NotFound();

        // One download per movie release, and per episode: each episode saved from a season (or whole-show) pack is a
        // download of its own, and only its file is fetched.
        var existing = await db.ServerDownloads.Include(d => d.Movie).Include(d => d.Release).Include(d => d.Episode).ThenInclude(e => e!.Show)
            .FirstOrDefaultAsync(d => (episode == null ? d.ReleaseId == release.Id && d.EpisodeId == null : d.EpisodeId == episode.Id)
                                      && d.Status != DownloadStatus.Cancelled && d.Status != DownloadStatus.Failed, ct);
        if (existing is not null) return ToDto(existing);

        var download = new ServerDownload
        {
            MovieId = release.MovieId, EpisodeId = episode?.Id, ReleaseId = release.Id, RequestedByUserId = User.UserId(),
            Movie = release.Movie, Episode = episode, Release = release,
        };
        db.ServerDownloads.Add(download);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), ToDto(download));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{id:int}/pause")]
    public async Task<IActionResult> Pause(int id, CancellationToken ct)
    {
        var d = await db.ServerDownloads.Include(x => x.Release).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return NotFound();
        if (d.Status is DownloadStatus.Completed or DownloadStatus.Cancelled) return BadRequest("Download is not active.");
        // Other episodes being saved from the same pack carry on; only this one's file stops (see ServerDownloadService).
        var sharesTorrent = await db.ServerDownloads.AnyAsync(x => x.ReleaseId == d.ReleaseId && x.Id != d.Id
            && (x.Status == DownloadStatus.Queued || x.Status == DownloadStatus.FetchingMetadata || x.Status == DownloadStatus.Downloading), ct);
        if (!sharesTorrent) await engine.PauseAsync(d.Release!.InfoHash);
        d.Status = DownloadStatus.Paused;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{id:int}/resume")]
    public async Task<IActionResult> Resume(int id, CancellationToken ct)
    {
        var d = await db.ServerDownloads.FindAsync([id], ct);
        if (d is null) return NotFound();
        if (d.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return BadRequest("Download is not paused.");
        d.Status = DownloadStatus.Queued;
        d.Error = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Cancels an active download, or removes a finished one (optionally deleting files).</summary>
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFiles, CancellationToken ct)
    {
        var d = await db.ServerDownloads.Include(x => x.Release).Include(x => x.Episode).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return NotFound();
        await ServerDownloadService.RemoveAsync(db, engine, d, deleteFiles, ct);
        return NoContent();
    }

    private int _keepDays = 7;

    private ServerDownloadDto ToDto(ServerDownload d)
    {
        var stats = d.Status is DownloadStatus.Downloading or DownloadStatus.FetchingMetadata or DownloadStatus.Completed
            ? engine.Stats(d.Release!.InfoHash, ServerDownloadService.Choice(d), d.Release.SeasonNumber)
            : null;
        var title = d.Episode is { } e ? $"{e.Show?.Title} · S{e.SeasonNumber:00}E{e.EpisodeNumber:00}" : d.Movie?.Title ?? "";
        return new ServerDownloadDto(
            d.Id, d.MovieId ?? 0, title, d.ReleaseId, d.Release?.Name ?? "", d.Status, d.Progress,
            stats?.DownloadRate ?? 0, stats?.UploadRate ?? 0, stats?.Peers ?? 0,
            stats?.Size is > 0 ? stats.Size : d.Release?.SizeBytes ?? 0,
            d.Error, d.CreatedAt, d.CompletedAt,
            d.CompletedAt is { } done && _keepDays > 0 ? done.AddDays(_keepDays) : null,
            d.EpisodeId, d.Episode?.ShowId);
    }
}
