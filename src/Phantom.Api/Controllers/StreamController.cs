using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Api.Services.Sources;
using Phantom.Api.Services.Torrents;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

/// <summary>
/// Media delivery. Media players can't add headers, so these endpoints also accept the JWT
/// as an <c>access_token</c> query parameter (see Program.cs).
/// </summary>
[ApiController]
[Authorize]
[Route("api/stream/{movieId:int}")]
public class StreamController(
    PhantomDbContext db,
    LibraryService library,
    TorrentEngine engine,
    Transcoder transcoder,
    ILogger<StreamController> log) : ControllerBase
{
    private async Task<Release?> ResolveReleaseAsync(int movieId, int? releaseId, CancellationToken ct)
    {
        if (!await library.Visible(User).AnyAsync(m => m.Id == movieId, ct)) return null;
        var releases = await db.Releases.Where(r => r.MovieId == movieId).ToListAsync(ct);
        if (releaseId is { } rid) return releases.FirstOrDefault(r => r.Id == rid);
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        return LibraryService.PickRelease(releases, settings.PreferredQuality);
    }

    private string Token() =>
        Request.Query["access_token"].FirstOrDefault()
        ?? Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts the torrent (if needed) and tells the client how to play it.</summary>
    [HttpGet("info")]
    public async Task<ActionResult<StreamInfoDto>> Info(int movieId, [FromQuery] int? releaseId, CancellationToken ct)
    {
        var release = await ResolveReleaseAsync(movieId, releaseId, ct);
        if (release is null) return NotFound();

        string fileName;
        long size;
        var subtitles = new List<SubtitleTrackDto>();
        var baseUrl = $"{Request.Scheme}://{Request.Host}/api/stream/{movieId}";
        var auth = $"releaseId={release.Id}&access_token={Uri.EscapeDataString(Token())}";

        if (release.LocalFilePath is { } local && System.IO.File.Exists(local))
        {
            fileName = Path.GetFileName(local);
            size = new FileInfo(local).Length;
            var dir = Path.GetDirectoryName(local)!;
            var subs = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(VideoFiles.IsSubtitle).Order().ToList();
            for (var i = 0; i < subs.Count; i++) subtitles.Add(Subtitle(i, subs[i]));
        }
        else
        {
            try
            {
                var manager = await engine.EnsureStartedAsync(release, keep: false, ct);
                var video = TorrentEngine.MainVideo(manager)!;
                fileName = Path.GetFileName(video.Path);
                size = video.Length;
                var subs = TorrentEngine.Subtitles(manager);
                for (var i = 0; i < subs.Count; i++) subtitles.Add(Subtitle(i, subs[i].Path));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status504GatewayTimeout, "Could not fetch torrent metadata (no peers?).");
            }
        }

        var state = await db.WatchStates.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == User.UserId() && w.MovieId == movieId, ct);
        return new StreamInfoDto(
            movieId,
            release.Id,
            fileName,
            size,
            $"{baseUrl}/direct?{auth}",
            transcoder.IsAvailable ? $"{baseUrl}/transcode?{auth}" : null,
            subtitles,
            state is { Watched: false } ? state.PositionMs : 0);

        SubtitleTrackDto Subtitle(int index, string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var lang = name.Split('.', '_', '-', ' ').LastOrDefault(p => p.Length is 2 or 3) ?? "und";
            return new SubtitleTrackDto(index.ToString(), lang, name, $"{baseUrl}/subtitles/{index}?{auth}");
        }
    }

    /// <summary>Direct play with HTTP range support; pieces are fetched on demand while seeking.</summary>
    [HttpGet("direct")]
    [HttpHead("direct")]
    public async Task<IActionResult> Direct(int movieId, [FromQuery] int? releaseId, [FromQuery] bool download, CancellationToken ct)
    {
        var release = await ResolveReleaseAsync(movieId, releaseId, ct);
        if (release is null) return NotFound();

        if (release.LocalFilePath is { } local && System.IO.File.Exists(local))
        {
            return PhysicalFile(Path.GetFullPath(local), VideoFiles.ContentType(local),
                download ? Path.GetFileName(local) : null, enableRangeProcessing: true);
        }

        var media = await engine.OpenAsync(release, ct);
        engine.Touch(release.InfoHash);
        var name = Path.GetFileName(media.FileName);
        return media.LocalPath is { } path
            ? PhysicalFile(Path.GetFullPath(path), VideoFiles.ContentType(name), download ? name : null, enableRangeProcessing: true)
            : File(media.Stream!, VideoFiles.ContentType(name), download ? name : null, enableRangeProcessing: true);
    }

    /// <summary>Transcoded fragmented-MP4 stream; seeking is done by re-requesting with <c>start</c>.</summary>
    [HttpGet("transcode")]
    public async Task Transcode(int movieId, [FromQuery] int? releaseId, [FromQuery] double start, [FromQuery] int? maxKbps, [FromQuery] int? maxHeight, CancellationToken ct)
    {
        if (!transcoder.IsAvailable)
        {
            Response.StatusCode = StatusCodes.Status501NotImplemented;
            return;
        }
        var release = await ResolveReleaseAsync(movieId, releaseId, ct);
        if (release is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // ffmpeg reads either the finished file or our own range-capable direct endpoint.
        var input = release.LocalFilePath is { } local && System.IO.File.Exists(local)
            ? Path.GetFullPath(local)
            : $"http://127.0.0.1:{HttpContext.Connection.LocalPort}/api/stream/{movieId}/direct?releaseId={release.Id}&access_token={Uri.EscapeDataString(Token())}";

        Response.ContentType = "video/mp4";
        try
        {
            await transcoder.TranscodeAsync(input, start, maxKbps, maxHeight, Response.Body, ct);
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("Transcode for movie {Id} cancelled by client", movieId);
        }
    }

    /// <summary>Returns a subtitle file from the torrent, converted to WebVTT unless format=srt.</summary>
    [HttpGet("subtitles/{index:int}")]
    public async Task<IActionResult> Subtitles(int movieId, int index, [FromQuery] int? releaseId, [FromQuery] string? format, CancellationToken ct)
    {
        var release = await ResolveReleaseAsync(movieId, releaseId, ct);
        if (release is null) return NotFound();

        string? path;
        if (release.LocalFilePath is { } local && System.IO.File.Exists(local))
        {
            var dir = Path.GetDirectoryName(local)!;
            path = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(VideoFiles.IsSubtitle).Order().ElementAtOrDefault(index);
        }
        else
        {
            var manager = await engine.EnsureStartedAsync(release, keep: false, ct);
            var file = TorrentEngine.Subtitles(manager).ElementAtOrDefault(index);
            if (file is null) return NotFound();
            if (!TorrentEngine.IsFileComplete(file)) return StatusCode(StatusCodes.Status409Conflict, "Subtitle still downloading, retry shortly.");
            path = file.FullPath;
        }
        if (path is null || !System.IO.File.Exists(path)) return NotFound();

        var text = await System.IO.File.ReadAllTextAsync(path, ct);
        if (string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
            return Content(text, path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ? "text/vtt" : "application/x-subrip", Encoding.UTF8);
        return Content(SubtitleConverter.SrtToVtt(text), "text/vtt", Encoding.UTF8);
    }
}

public static class SubtitleConverter
{
    public static string SrtToVtt(string srt)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var line in srt.Replace("\r\n", "\n").Split('\n'))
        {
            // 00:00:01,000 --> 00:00:02,000 becomes 00:00:01.000 --> 00:00:02.000
            sb.Append(line.Contains("-->") ? line.Replace(',', '.') : line).Append('\n');
        }
        return sb.ToString();
    }
}
