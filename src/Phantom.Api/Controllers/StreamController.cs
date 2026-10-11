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

/// <summary>What a stream request plays: a release, which video in it, and what the loading screen needs to know.</summary>
public record StreamTarget(Release Release, VideoChoice Choice, TimeSpan Runtime, long ResumePositionMs, int MovieId, int? EpisodeId)
{
    /// <summary>Names this video's seek previews in the cache (a pack's episodes share a release).</summary>
    public string CacheKey => EpisodeId is { } e ? $"{Release.Id}-e{e}" : Release.Id.ToString();
}

/// <summary>Movies: <c>api/stream/{movieId}/...</c>.</summary>
[Route("api/stream/{id:int}")]
public class StreamController(
    PhantomDbContext db,
    LibraryService library,
    TorrentEngine engine,
    Transcoder transcoder,
    Microsoft.Extensions.Options.IOptions<PhantomOptions> options,
    ILogger<StreamController> log) : StreamControllerBase(db, engine, transcoder, options, log)
{
    protected override string RouteBase(int id) => $"/api/stream/{id}";

    protected override async Task<StreamTarget?> ResolveAsync(int id, int? releaseId, CancellationToken ct)
    {
        var movie = await library.Visible(User).Where(m => m.Id == id).Select(m => new { m.RuntimeMinutes }).FirstOrDefaultAsync(ct);
        if (movie is null) return null;
        var releases = await Db.Releases.Where(r => r.MovieId == id).ToListAsync(ct);
        var settings = await Db.Settings.AsNoTracking().FirstAsync(ct);
        var release = releaseId is { } rid ? releases.FirstOrDefault(r => r.Id == rid) : LibraryService.PickRelease(releases, settings.PreferredQuality);
        if (release is null) return null;
        var state = await Db.WatchStates.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == User.UserId() && w.MovieId == id, ct);
        return new StreamTarget(release, VideoChoice.Main, Runtime(movie.RuntimeMinutes, 110), state is { Watched: false } ? state.PositionMs : 0, id, null);
    }
}

/// <summary>Episodes: <c>api/stream/episode/{episodeId}/...</c>, from the episode's own release or a pack that has it.</summary>
[Route("api/stream/episode/{id:int}")]
public class EpisodeStreamController(
    PhantomDbContext db,
    ShowLibraryService shows,
    TorrentEngine engine,
    Transcoder transcoder,
    Microsoft.Extensions.Options.IOptions<PhantomOptions> options,
    ILogger<EpisodeStreamController> log) : StreamControllerBase(db, engine, transcoder, options, log)
{
    protected override string RouteBase(int id) => $"/api/stream/episode/{id}";

    protected override async Task<StreamTarget?> ResolveAsync(int id, int? releaseId, CancellationToken ct)
    {
        var episode = await Db.Episodes.AsNoTracking().Include(e => e.Show).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null || !await shows.Visible(User).AnyAsync(s => s.Id == episode.ShowId, ct)) return null;
        var releases = await Db.Releases.Where(r => r.ShowId == episode.ShowId).ToListAsync(ct);
        var settings = await Db.Settings.AsNoTracking().FirstAsync(ct);
        var candidates = ShowLibraryService.OrderForPlayback(releases, episode, settings.PreferredQuality).ToList();
        var release = releaseId is { } rid ? candidates.FirstOrDefault(r => r.Id == rid) : candidates.FirstOrDefault();
        if (release is null) return null;
        var state = await Db.EpisodeWatchStates.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == User.UserId() && w.EpisodeId == id, ct);
        return new StreamTarget(release, new VideoChoice(episode.SeasonNumber, episode.EpisodeNumber),
            Runtime(episode.RuntimeMinutes ?? episode.Show!.EpisodeRuntimeMinutes, 45),
            state is { Watched: false } ? state.PositionMs : 0, 0, id);
    }
}

/// <summary>
/// Media delivery, the same for a movie and an episode (the subclasses say which release and video). Media players
/// can't add headers, so these endpoints also accept the JWT as an <c>access_token</c> query parameter (see Program.cs).
/// </summary>
[ApiController]
[Authorize]
public abstract class StreamControllerBase(
    PhantomDbContext db,
    TorrentEngine engine,
    Transcoder transcoder,
    Microsoft.Extensions.Options.IOptions<PhantomOptions> options,
    ILogger log) : ControllerBase
{
    protected PhantomDbContext Db => db;

    /// <summary>How much video has to be downloaded ahead before playback starts (for the loading screen's estimate).</summary>
    private static readonly TimeSpan StartBuffer = TimeSpan.FromSeconds(30);

    /// <summary>Seek previews are cached per this many seconds of the video.</summary>
    private const int ThumbnailStepSeconds = 10;

    // A few ffmpeg processes at a time: scrubbing asks for many previews in a row.
    private static readonly SemaphoreSlim ThumbnailSlots = new(3, 3);

    // The preview being made for each viewer of each video. A newer request replaces it: while scrubbing only the
    // latest frame matters, and a stale one would keep pulling its part of the torrent ahead of what's being watched
    // (reads from a torrent all share one stream, so playback would wait behind it).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> ThumbnailsInFlight = new();

    /// <summary>The path these endpoints sit under for <paramref name="id"/>, e.g. "/api/stream/12".</summary>
    protected abstract string RouteBase(int id);

    /// <summary>The release and video to play, or null when there's nothing this profile may play.</summary>
    protected abstract Task<StreamTarget?> ResolveAsync(int id, int? releaseId, CancellationToken ct);

    protected static TimeSpan Runtime(int? minutes, int fallbackMinutes) => TimeSpan.FromMinutes(minutes is > 0 ? minutes.Value : fallbackMinutes);

    private string Token() =>
        Request.Query["access_token"].FirstOrDefault()
        ?? Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The finished file on the server's disk for this video, if there is one.</summary>
    private string? Local(StreamTarget target) => engine.LocalVideo(target.Release, target.Choice);

    /// <summary>Our own direct endpoint, for ffmpeg to read a video that's still a torrent.</summary>
    private string Loopback(int id, StreamTarget target) =>
        $"http://127.0.0.1:{HttpContext.Connection.LocalPort}{RouteBase(id)}/direct?releaseId={target.Release.Id}&access_token={Uri.EscapeDataString(Token())}";

    /// <summary>Starts the torrent (if needed) and tells the client how to play it.</summary>
    [HttpGet("info")]
    public async Task<ActionResult<StreamInfoDto>> Info(int id, [FromQuery] int? releaseId, CancellationToken ct)
    {
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null) return NotFound();
        var release = target.Release;

        string fileName;
        long size;
        var subtitles = new List<SubtitleTrackDto>();
        var baseUrl = $"{Request.Scheme}://{Request.Host}{RouteBase(id)}";
        var auth = $"releaseId={release.Id}&access_token={Uri.EscapeDataString(Token())}";

        if (Local(target) is { } local)
        {
            fileName = Path.GetFileName(local);
            size = new FileInfo(local).Length;
            var subs = LocalSubtitles(target, local);
            for (var i = 0; i < subs.Count; i++) subtitles.Add(Subtitle(i, subs[i]));
        }
        else
        {
            try
            {
                var manager = await engine.EnsureStartedAsync(release, keep: false, ct, target.Choice);
                if (TorrentEngine.Video(manager, target.Choice, release.SeasonNumber) is not { } video)
                    return NotFound("This release hasn't got that episode. Try another version.");
                fileName = Path.GetFileName(video.Path);
                size = video.Length;
                var subs = TorrentEngine.Subtitles(manager, video);
                for (var i = 0; i < subs.Count; i++) subtitles.Add(Subtitle(i, subs[i].Path));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status504GatewayTimeout, "Could not fetch torrent metadata (no peers?).");
            }
        }

        return new StreamInfoDto(
            target.MovieId,
            release.Id,
            fileName,
            size,
            $"{baseUrl}/direct?{auth}",
            transcoder.IsAvailable ? $"{baseUrl}/transcode?{auth}" : null,
            subtitles,
            target.ResumePositionMs,
            transcoder.IsAvailable ? $"{baseUrl}/thumbnail?{auth}" : null,
            target.EpisodeId);

        SubtitleTrackDto Subtitle(int index, string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var lang = name.Split('.', '_', '-', ' ').LastOrDefault(p => p.Length is 2 or 3) ?? "und";
            return new SubtitleTrackDto(index.ToString(), lang, name, $"{baseUrl}/subtitles/{index}?{auth}");
        }
    }

    /// <summary>
    /// Subtitle files next to a finished video: everything in its folder for a movie, and for an episode of a pack the
    /// ones named after it or for its episode.
    /// </summary>
    private static List<string> LocalSubtitles(StreamTarget target, string video)
    {
        var all = Directory.EnumerateFiles(Path.GetDirectoryName(video)!, "*.*", SearchOption.AllDirectories)
            .Where(VideoFiles.IsSubtitle).Order().ToList();
        if (!target.Choice.IsEpisode) return all;
        var stem = Path.GetFileNameWithoutExtension(video);
        return all.Where(s => Path.GetFileName(s).StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                              || ReleaseNameParser.EpisodeOfFile(s, target.Choice.Season) == (target.Choice.Season, target.Choice.Episode)).ToList();
    }

    /// <summary>Direct play with HTTP range support; pieces are fetched on demand while seeking.</summary>
    [HttpGet("direct")]
    [HttpHead("direct")]
    public async Task<IActionResult> Direct(int id, [FromQuery] int? releaseId, [FromQuery] bool download, CancellationToken ct)
    {
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null) return NotFound();

        if (Local(target) is { } local)
            return PhysicalFile(Path.GetFullPath(local), VideoFiles.ContentType(local), download ? Path.GetFileName(local) : null, enableRangeProcessing: true);

        var media = await engine.OpenAsync(target.Release, ct, target.Choice);
        if (media is null) return NotFound();
        engine.Touch(target.Release.InfoHash);
        var name = Path.GetFileName(media.FileName);
        return media.LocalPath is { } path
            ? PhysicalFile(Path.GetFullPath(path), VideoFiles.ContentType(name), download ? name : null, enableRangeProcessing: true)
            : File(media.Stream!, VideoFiles.ContentType(name), download ? name : null, enableRangeProcessing: true);
    }

    /// <summary>Transcoded fragmented-MP4 stream; seeking is done by re-requesting with <c>start</c>.</summary>
    [HttpGet("transcode")]
    public async Task Transcode(int id, [FromQuery] int? releaseId, [FromQuery] double start, [FromQuery] int? maxKbps, [FromQuery] int? maxHeight, CancellationToken ct)
    {
        if (!transcoder.IsAvailable)
        {
            Response.StatusCode = StatusCodes.Status501NotImplemented;
            return;
        }
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // ffmpeg reads either the finished file or our own range-capable direct endpoint.
        var input = Local(target) is { } local ? Path.GetFullPath(local) : Loopback(id, target);

        Response.ContentType = "video/mp4";
        try
        {
            await transcoder.TranscodeAsync(input, start, maxKbps, maxHeight, Response.Body, ct);
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("Transcode of {Route} cancelled by client", RouteBase(id));
        }
    }

    /// <summary>
    /// What's happening to get this video playing, for the player's loading screen. Answers straight away, even while
    /// <see cref="Info"/> is still waiting for the torrent's metadata.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<StreamStatusDto>> Status(int id, [FromQuery] int? releaseId, [FromQuery] double position, CancellationToken ct)
    {
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null) return NotFound();
        var release = target.Release;
        if (Local(target) is not null) return new StreamStatusDto("Ready", 0, 100, 0, true);

        var manager = engine.Find(release.InfoHash);
        if (manager is null) return new StreamStatusDto("Starting the download on the server…", 0, 0, 0, false);

        var peers = manager.Peers.Available + manager.OpenConnections;
        var what = target.EpisodeId is null ? "movie" : "episode";
        if (!manager.HasMetadata)
            return new StreamStatusDto(peers > 0 ? $"Getting the {what}'s details from {peers} peers…" : $"Looking for people sharing this {what}…", peers, 0, 0, false);

        var stats = engine.Stats(release.InfoHash)!;
        if (stats.Complete) return new StreamStatusDto("Ready", stats.Peers, 100, 0, true);

        // Playback starts once about StartBuffer of video is downloaded from where it starts (the bitrate comes from
        // the file's size over the video's length).
        var runtime = target.Runtime;
        var buffer = engine.BufferedFrom(release.InfoHash, position / runtime.TotalSeconds);
        double? bufferProgress = null;
        int? secondsToStart = null;
        var tooSlow = false;
        long neededRate = 0;
        if (buffer is var (buffered, length) && length > 0)
        {
            neededRate = (long)(length / runtime.TotalSeconds);
            var bufferTarget = Math.Max(4L << 20, (long)(neededRate * StartBuffer.TotalSeconds));
            bufferProgress = Math.Clamp((double)buffered / bufferTarget, 0, 1);
            if (stats.DownloadRate > 0) secondsToStart = (int)Math.Ceiling(Math.Max(0, bufferTarget - buffered) / (double)stats.DownloadRate);
            // Judged once it's had a fair chance to get going.
            tooSlow = engine.Age(release.InfoHash) > TimeSpan.FromSeconds(20)
                      && (stats.DownloadRate < neededRate * 0.9 || secondsToStart is null or > 120);
        }

        var message = stats.Peers == 0
            ? "Connecting to peers…"
            : bufferProgress >= 1 ? "Almost ready…" : $"Downloading from {stats.Peers} peers · {FormatRate(stats.DownloadRate)}";
        return new StreamStatusDto(message, stats.Peers, Math.Round(stats.Progress, 1), stats.DownloadRate, false,
            bufferProgress, secondsToStart, tooSlow, neededRate);

        static string FormatRate(long bytesPerSecond) => bytesPerSecond >= 1 << 20
            ? $"{bytesPerSecond / (double)(1 << 20):0.#} MB/s"
            : $"{bytesPerSecond / 1024} KB/s";
    }

    /// <summary>
    /// A small frame from <paramref name="t"/> seconds in, for the seek bar's preview. Taken by ffmpeg from the file (or
    /// the torrent, which fetches the pieces around that point) and cached in steps of <see cref="ThumbnailStepSeconds"/>.
    /// </summary>
    [HttpGet("thumbnail")]
    public async Task<IActionResult> Thumbnail(int id, [FromQuery] int? releaseId, [FromQuery] double t, CancellationToken ct)
    {
        if (!transcoder.IsAvailable) return StatusCode(StatusCodes.Status501NotImplemented);
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null) return NotFound();

        var step = (int)Math.Max(0, t) / ThumbnailStepSeconds * ThumbnailStepSeconds;
        var cache = Path.Combine(options.Value.CacheDirectory, "thumbnails", target.CacheKey, $"{step}.jpg");
        if (System.IO.File.Exists(cache)) return PhysicalFile(Path.GetFullPath(cache), "image/jpeg");

        var input = Local(target) is { } local ? Path.GetFullPath(local) : Loopback(id, target);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var viewer = $"{target.CacheKey}:{User.UserId()}";
        ThumbnailsInFlight.AddOrUpdate(viewer, timeout, (_, older) =>
        {
            older.Cancel();
            return timeout;
        });
        try
        {
            await ThumbnailSlots.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ThumbnailsInFlight.TryRemove(new(viewer, timeout));
            return StatusCode(StatusCodes.Status204NoContent);
        }
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
            // Timed out, or a newer preview took over.
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        finally
        {
            ThumbnailSlots.Release();
            ThumbnailsInFlight.TryRemove(new(viewer, timeout));
        }
    }

    /// <summary>Returns a subtitle file from the torrent, converted to WebVTT unless format=srt.</summary>
    [HttpGet("subtitles/{index:int}")]
    public async Task<IActionResult> Subtitles(int id, int index, [FromQuery] int? releaseId, [FromQuery] string? format, CancellationToken ct)
    {
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null) return NotFound();

        string? path;
        if (Local(target) is { } local)
        {
            path = LocalSubtitles(target, local).ElementAtOrDefault(index);
        }
        else
        {
            var manager = await engine.EnsureStartedAsync(target.Release, keep: false, ct, target.Choice);
            var video = TorrentEngine.Video(manager, target.Choice, target.Release.SeasonNumber);
            var file = TorrentEngine.Subtitles(manager, video).ElementAtOrDefault(index);
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
