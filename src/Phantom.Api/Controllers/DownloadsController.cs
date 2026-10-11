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
            .Include(d => d.Movie).Include(d => d.Release)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ServerDownloadDto>> Create(CreateServerDownloadRequest req, CancellationToken ct)
    {
        var releases = await db.Releases.Include(r => r.Movie).Where(r => r.MovieId == req.MovieId).ToListAsync(ct);
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        var release = req.ReleaseId is { } rid ? releases.FirstOrDefault(r => r.Id == rid) : LibraryService.PickRelease(releases, settings.PreferredQuality);
        if (release is null) return NotFound();

        var existing = await db.ServerDownloads.Include(d => d.Movie).Include(d => d.Release)
            .FirstOrDefaultAsync(d => d.ReleaseId == release.Id && d.Status != DownloadStatus.Cancelled && d.Status != DownloadStatus.Failed, ct);
        if (existing is not null) return ToDto(existing);

        var download = new ServerDownload { MovieId = req.MovieId, ReleaseId = release.Id, RequestedByUserId = User.UserId(), Movie = release.Movie, Release = release };
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
        await engine.PauseAsync(d.Release!.InfoHash);
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
        var d = await db.ServerDownloads.Include(x => x.Release).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return NotFound();
        var release = d.Release!;

        if (d.Status != DownloadStatus.Completed || deleteFiles)
        {
            await engine.RemoveAsync(release.InfoHash, deleteData: deleteFiles || d.Status != DownloadStatus.Completed);
            if (deleteFiles && release.LocalFilePath is { } path)
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                release.LocalFilePath = null;
            }
        }
        else
        {
            engine.Unpin(release.InfoHash);
        }
        db.ServerDownloads.Remove(d);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private ServerDownloadDto ToDto(ServerDownload d)
    {
        var stats = d.Status is DownloadStatus.Downloading or DownloadStatus.FetchingMetadata or DownloadStatus.Completed
            ? engine.Stats(d.Release!.InfoHash)
            : null;
        return new ServerDownloadDto(
            d.Id, d.MovieId, d.Movie?.Title ?? "", d.ReleaseId, d.Release?.Name ?? "", d.Status, d.Progress,
            stats?.DownloadRate ?? 0, stats?.UploadRate ?? 0, stats?.Peers ?? 0,
            stats?.Size is > 0 ? stats.Size : d.Release?.SizeBytes ?? 0,
            d.Error, d.CreatedAt, d.CompletedAt);
    }
}
