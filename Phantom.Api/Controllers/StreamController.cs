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
    HlsSessions hls,
    TrickplayService trickplay,
    ILogger<StreamController> log) : StreamControllerBase(db, engine, transcoder, hls, trickplay, log)
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
    HlsSessions hls,
    TrickplayService trickplay,
    ILogger<EpisodeStreamController> log) : StreamControllerBase(db, engine, transcoder, hls, trickplay, log)
{
    protected override string RouteBase(int id) => $"/api/stream/episode/{id}";

    protected override async Task<StreamTarget?> ResolveAsync(int id, int? releaseId, CancellationToken ct)
    {
        var episode = await Db.Episodes.AsNoTracking().Include(e => e.Show).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null || !await shows.Visible(User).AnyAsync(s => s.Id == episode.ShowId, ct)) return null;
        var releases = await Db.Releases.Where(r => r.ShowId == episode.ShowId).ToListAsync(ct);
        var settings = await Db.Settings.AsNoTracking().FirstAsync(ct);
        var candidates = ShowLibraryService.OrderForPlayback(releases, episode, settings.PreferredQuality).ToList();
        // A source picked for the season that hasn't got this episode: the best that has.
        var release = (releaseId is { } rid ? candidates.FirstOrDefault(r => r.Id == rid) : null) ?? candidates.FirstOrDefault();
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
    HlsSessions hls,
    TrickplayService trickplay,
    ILogger log) : ControllerBase
{
    protected PhantomDbContext Db => db;

    /// <summary>How much video has to be downloaded ahead before playback starts (for the loading screen's estimate).</summary>
    private static readonly TimeSpan StartBuffer = TimeSpan.FromSeconds(30);

    /// <summary>Seek previews are cached per this many seconds of the video.</summary>
    private const int ThumbnailStepSeconds = TrickplayService.StepSeconds;

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

    /// <summary>The video's whole file, when the server has it: saved, or a stream that has downloaded all of it.</summary>
    private string? CompleteFile(StreamTarget target)
    {
        if (Local(target) is { } local) return local;
        if (engine.Find(target.Release.InfoHash) is not { HasMetadata: true } manager) return null;
        return TorrentEngine.Video(manager, target.Choice, target.Release.SeasonNumber) is { } video
               && TorrentEngine.IsFileComplete(video) && System.IO.File.Exists(video.FullPath) ? video.FullPath : null;
    }

    /// <summary>Our own direct endpoint, for ffmpeg to read a video that's still a torrent.</summary>
    /// <param name="preview">For a seek preview: only what's downloaded, never waiting on the swarm (see <see cref="Direct"/>).</param>
    private string Loopback(int id, StreamTarget target, bool preview = false) =>
        $"http://127.0.0.1:{HttpContext.Connection.LocalPort}{RouteBase(id)}/direct?releaseId={target.Release.Id}&access_token={Uri.EscapeDataString(Token())}"
        + (preview ? "&preview=true" : "");

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

        // A finished file gets all its seek previews made now, so they're there to drag through.
        if (CompleteFile(target) is { } complete) trickplay.Request(target.CacheKey, complete);

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
            target.EpisodeId,
            transcoder.IsAvailable ? $"{baseUrl}/thumbnails?{auth}" : null,
            transcoder.IsAvailable ? $"{baseUrl}/hls/{{session}}/{HlsSessions.Playlist}?{auth}" : null);

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

    /// <summary>
    /// Direct play with HTTP range support; pieces are fetched on demand while seeking. <paramref name="preview"/> (our own
    /// seek previews, through <see cref="Loopback"/>) reads only what's downloaded already: a preview of a part that isn't
    /// fails rather than stalling the playback beside it.
    /// </summary>
    [HttpGet("direct")]
    [HttpHead("direct")]
    public async Task<IActionResult> Direct(int id, [FromQuery] int? releaseId, [FromQuery] bool download, [FromQuery] bool preview, CancellationToken ct)
    {
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null) return NotFound();

        if (Local(target) is { } local)
            return PhysicalFile(Path.GetFullPath(local), VideoFiles.ContentType(local), download ? Path.GetFileName(local) : null, enableRangeProcessing: true);

        var media = await engine.OpenAsync(target.Release, ct, target.Choice, wait: !preview);
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
        var input = CompleteFile(target) is { } complete ? Path.GetFullPath(complete) : Loopback(id, target);

        Response.ContentType = "video/mp4";
        try
        {
            await transcoder.TranscodeAsync(input, start, maxKbps, maxHeight, Response.Body, ct);
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("Transcode of {Route} cancelled by client", RouteBase(id));
        }
        catch (TranscodeFailedException ex) when (!Response.HasStarted)
        {
            // Said as an error, with ffmpeg's reason: the player (and the app's error message) can show why.
            Response.StatusCode = StatusCodes.Status502BadGateway;
            Response.ContentType = "text/plain; charset=utf-8";
            await Response.WriteAsync($"The server couldn't convert this video: {ex.Message}", CancellationToken.None);
        }
    }

    /// <summary>
    /// The same conversion as <see cref="Transcode"/> as HLS, for Apple's player (see <see cref="HlsSessions"/>). The
    /// client names the session: the player rereading the playlist as it grows asks with the same one and gets the
    /// conversion that's already running. Answers once the first segment is ready.
    /// <para>
    /// <paramref name="remux"/>: the client could play the picture and sound, only not the file they're in (an MKV on
    /// iOS). Those that Apple decodes are then copied rather than re-encoded (see <see cref="Transcoder.HlsArguments"/>).
    /// </para>
    /// </summary>
    [HttpGet("hls/{session}/" + HlsSessions.Playlist)]
    public async Task<IActionResult> HlsPlaylist(int id, string session, [FromQuery] int? releaseId, [FromQuery] double start,
        [FromQuery] int? maxKbps, [FromQuery] int? maxHeight, [FromQuery] bool remux, CancellationToken ct)
    {
        if (!transcoder.IsAvailable) return StatusCode(StatusCodes.Status501NotImplemented);
        if (!HlsSessions.IsSessionId(session)) return BadRequest();
        var owner = HlsOwner(id);
        var running = hls.Find(session, owner);
        if (running is null)
        {
            var target = await ResolveAsync(id, releaseId, ct);
            if (target is null) return NotFound();
            // A whole file on the server's disk is read straight from there, not through the torrent.
            var input = CompleteFile(target) is { } complete ? Path.GetFullPath(complete) : Loopback(id, target);
            var codecs = remux ? await transcoder.ProbeAsync(input, ct) : null;
            running = hls.Start(session, owner, input, start, maxKbps, maxHeight, codecs);
        }

        string playlist;
        try
        {
            playlist = await hls.PlaylistAsync(running, ct);
        }
        catch (TranscodeFailedException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, $"The server couldn't convert this video: {ex.Message}");
        }
        // The player resolves each segment next to the playlist but doesn't carry its query along: the token goes on each.
        var auth = $"?access_token={Uri.EscapeDataString(Token())}";
        var lines = playlist.Split('\n').Select(l => l.Length > 0 && !l.StartsWith('#') ? l.TrimEnd('\r') + auth
            : l.Replace($"URI=\"{HlsSessions.InitSegment}\"", $"URI=\"{HlsSessions.InitSegment}{auth}\""));
        Response.Headers.CacheControl = "no-cache";
        return Content(string.Join('\n', lines), "application/vnd.apple.mpegurl");
    }

    /// <summary>One segment of an HLS conversion, from its session's folder.</summary>
    [HttpGet("hls/{session}/{segment}")]
    public IActionResult HlsSegment(int id, string session, string segment)
    {
        if (!HlsSessions.IsSegment(segment) || hls.Find(session, HlsOwner(id)) is not { } running) return NotFound();
        var path = Path.Combine(running.Folder, segment);
        var type = segment == HlsSessions.InitSegment ? "video/mp4" : "video/iso.segment";
        return System.IO.File.Exists(path) ? PhysicalFile(Path.GetFullPath(path), type, enableRangeProcessing: true) : NotFound();
    }

    /// <summary>Whose HLS sessions are whose: this viewer, this video.</summary>
    private string HlsOwner(int id) => $"{User.UserId()}:{RouteBase(id)}";

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
        if (Local(target) is not null) return new StreamStatusDto("Ready", 0, 100, 0, true, AvailableTo: 1);

        var manager = engine.Find(release.InfoHash);
        // The loading screen polls this: still wanted, even before the player reads anything.
        if (manager is not null) engine.Touch(release.InfoHash);
        if (manager is null) return new StreamStatusDto("Starting the download on the server…", 0, 0, 0, false);

        var peers = manager.Peers.Available + manager.OpenConnections;
        var what = target.EpisodeId is null ? "movie" : "episode";
        if (!manager.HasMetadata)
            return new StreamStatusDto(peers > 0 ? $"Getting the {what}'s details from {peers} peers…" : $"Looking for people sharing this {what}…", peers, 0, 0, false);

        var stats = engine.Stats(release.InfoHash)!;
        if (stats.Complete) return new StreamStatusDto("Ready", stats.Peers, 100, 0, true, AvailableTo: 1);

        // Playback starts once about StartBuffer of video is downloaded from where it starts (the bitrate comes from
        // the file's size over the video's length).
        var runtime = target.Runtime;
        var buffer = engine.BufferedFrom(release.InfoHash, position / runtime.TotalSeconds);
        double? bufferProgress = null;
        int? secondsToStart = null;
        var tooSlow = false;
        long neededRate = 0;
        double? availableTo = null;
        if (buffer is var (buffered, length) && length > 0)
        {
            availableTo = Math.Clamp(position / runtime.TotalSeconds + (double)buffered / length, 0, 1);
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
            bufferProgress, secondsToStart, tooSlow, neededRate, availableTo);

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
        var cache = Path.Combine(trickplay.Folder(target.CacheKey), $"{step}.jpg");
        if (System.IO.File.Exists(cache)) return PhysicalFile(Path.GetFullPath(cache), "image/jpeg");

        var input = CompleteFile(target) is { } complete ? Path.GetFullPath(complete) : Loopback(id, target, preview: true);

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

    /// <summary>
    /// Every seek preview made so far, in one response, for the player to keep and show the moment the bar is dragged:
    /// <c>[count]</c> then per frame <c>[seconds][length][JPEG bytes]</c>, all integers 32-bit little-endian. The
    /// <c>X-Thumbnails-Complete</c> header says whether that's all of them (otherwise ask again later). Asking also
    /// queues them to be made if the file is complete on the server.
    /// </summary>
    [HttpGet("thumbnails")]
    public async Task<IActionResult> Thumbnails(int id, [FromQuery] int? releaseId, CancellationToken ct)
    {
        if (!transcoder.IsAvailable) return StatusCode(StatusCodes.Status501NotImplemented);
        var target = await ResolveAsync(id, releaseId, ct);
        if (target is null) return NotFound();
        if (CompleteFile(target) is { } complete) trickplay.Request(target.CacheKey, complete);

        var buffer = new MemoryStream();
        await using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            var frames = trickplay.Frames(target.CacheKey);
            writer.Write(frames.Count);
            foreach (var (seconds, path) in frames)
            {
                byte[] jpeg;
                try
                {
                    jpeg = await System.IO.File.ReadAllBytesAsync(path, ct);
                }
                catch (IOException)
                {
                    jpeg = []; // being written, or cleared for disk space; the player asks for it on its own
                }
                writer.Write(seconds);
                writer.Write(jpeg.Length);
                writer.Write(jpeg);
            }
        }
        Response.Headers["X-Thumbnails-Complete"] = trickplay.IsComplete(target.CacheKey) ? "1" : "0";
        return File(buffer.ToArray(), "application/octet-stream");
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
