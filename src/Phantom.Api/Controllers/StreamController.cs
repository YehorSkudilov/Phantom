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
    Microsoft.Extensions.Options.IOptions<PhantomOptions> options,
    ILogger<StreamController> log) : ControllerBase
{
    /// <summary>Seek previews are cached per this many seconds of the movie.</summary>
    private const int ThumbnailStepSeconds = 10;

    // A few ffmpeg processes at a time: scrubbing asks for many previews in a row.
    private static readonly SemaphoreSlim ThumbnailSlots = new(3, 3);
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
            state is { Watched: false } ? state.PositionMs : 0,
            transcoder.IsAvailable ? $"{baseUrl}/thumbnail?{auth}" : null);

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

    /// <summary>
    /// What's happening to get this movie playing, for the player's loading screen. Answers straight away, even while
    /// <see cref="Info"/> is still waiting for the torrent's metadata.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<StreamStatusDto>> Status(int movieId, [FromQuery] int? releaseId, CancellationToken ct)
    {
        var release = await ResolveReleaseAsync(movieId, releaseId, ct);
        if (release is null) return NotFound();
        if (release.LocalFilePath is { } local && System.IO.File.Exists(local))
            return new StreamStatusDto("Ready", 0, 100, 0, true);

        var manager = engine.Find(release.InfoHash);
        if (manager is null) return new StreamStatusDto("Starting the download on the server…", 0, 0, 0, false);

        var peers = manager.Peers.Available + manager.OpenConnections;
        if (!manager.HasMetadata)
            return new StreamStatusDto(peers > 0 ? $"Getting the movie's details from {peers} peers…" : "Looking for people sharing this movie…", peers, 0, 0, false);

        var stats = engine.Stats(release.InfoHash)!;
        if (stats.Complete) return new StreamStatusDto("Ready", stats.Peers, 100, 0, true);
        var message = stats.Peers == 0
            ? "Connecting to peers…"
            : $"Downloading from {stats.Peers} peers · {FormatRate(stats.DownloadRate)}";
        return new StreamStatusDto(message, stats.Peers, Math.Round(stats.Progress, 1), stats.DownloadRate, false);

        static string FormatRate(long bytesPerSecond) => bytesPerSecond >= 1 << 20
            ? $"{bytesPerSecond / (double)(1 << 20):0.#} MB/s"
            : $"{bytesPerSecond / 1024} KB/s";
    }

    /// <summary>
    /// A small frame from <paramref name="t"/> seconds in, for the seek bar's preview. Taken by ffmpeg from the file (or
    /// the torrent, which fetches the pieces around that point) and cached in steps of <see cref="ThumbnailStepSeconds"/>.
    /// </summary>
    [HttpGet("thumbnail")]
    public async Task<IActionResult> Thumbnail(int movieId, [FromQuery] int? releaseId, [FromQuery] double t, CancellationToken ct)
    {
        if (!transcoder.IsAvailable) return StatusCode(StatusCodes.Status501NotImplemented);
        var release = await ResolveReleaseAsync(movieId, releaseId, ct);
        if (release is null) return NotFound();

        var step = (int)Math.Max(0, t) / ThumbnailStepSeconds * ThumbnailStepSeconds;
        var cache = Path.Combine(options.Value.CacheDirectory, "thumbnails", release.Id.ToString(), $"{step}.jpg");
        if (System.IO.File.Exists(cache)) return PhysicalFile(Path.GetFullPath(cache), "image/jpeg");

        var input = release.LocalFilePath is { } local && System.IO.File.Exists(local)
            ? Path.GetFullPath(local)
            : $"http://127.0.0.1:{HttpContext.Connection.LocalPort}/api/stream/{movieId}/direct?releaseId={release.Id}&access_token={Uri.EscapeDataString(Token())}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await ThumbnailSlots.WaitAsync(timeout.Token);
        try
        {
            var jpeg = await transcoder.ThumbnailAsync(input, step, 320, timeout.Token);
            if (jpeg is null) return NotFound();
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await System.IO.File.WriteAllBytesAsync(cache, jpeg, CancellationToken.None);
            return File(jpeg, "image/jpeg");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        finally
        {
            ThumbnailSlots.Release();
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
