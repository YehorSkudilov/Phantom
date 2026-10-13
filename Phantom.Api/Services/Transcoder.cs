using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Phantom.Api.Services;

/// <summary>
/// Optional on-the-fly transcoding through ffmpeg into fragmented MP4, used when a client
/// can't direct-play a file (e.g. MKV/HEVC on some devices) or asks for a lower bitrate.
/// </summary>
public class Transcoder(IOptions<PhantomOptions> options, ILogger<Transcoder> log)
{
    private bool? _available;

    public bool IsAvailable => _available ??= Probe();

    private bool Probe()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(options.Value.FfmpegPath, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogInformation("ffmpeg not found; transcoding disabled");
            return false;
        }
    }

    public static IReadOnlyList<string> BuildArguments(string input, double startSeconds, int? maxVideoKbps, int? maxHeight)
    {
        var args = EncodeArguments(input, startSeconds, maxVideoKbps, maxHeight);
        args.AddRange(["-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1"]);
        return args;
    }

    /// <summary>Seconds of video in each HLS segment.</summary>
    public const int HlsSegmentSeconds = 4;

    /// <summary>
    /// The video written as HLS into <paramref name="folder"/>: a playlist (<see cref="HlsSessions.Playlist"/>) that grows a
    /// segment at a time, for Apple's player (see <see cref="HlsSessions"/>). Segments are fragmented MP4, the only kind
    /// Apple's HLS takes HEVC in.
    /// <para>
    /// With <paramref name="codecs"/> saying the file's picture and sound are ones Apple plays (and no lower quality
    /// asked for), they're copied as they are, only repackaged: that goes at the disk's speed, so playing and seeking
    /// start almost at once, where re-encoding a big file can fall behind playback and keep it buffering. Otherwise
    /// it's the full conversion of <see cref="BuildArguments"/>, with a keyframe at each segment's start.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> HlsArguments(string input, double startSeconds, int? maxVideoKbps, int? maxHeight, string folder,
        MediaCodecs? codecs = null)
    {
        List<string> args;
        if (codecs?.CanCopyVideoForApple == true && maxVideoKbps is null && maxHeight is null)
        {
            args = ["-hide_banner", "-loglevel", "error"];
            if (startSeconds > 0) args.AddRange(["-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
            args.AddRange(["-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-c:v", "copy"]);
            // Apple only recognises HEVC in MP4 by this tag (MKVs carry the other one, hev1).
            if (codecs.Video == "hevc") args.AddRange(["-tag:v", "hvc1"]);
            if (codecs.CanCopyAudioForApple) args.AddRange(["-c:a", "copy"]);
            else args.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "160k"]);
        }
        else
        {
            args = EncodeArguments(input, startSeconds, maxVideoKbps, maxHeight);
            args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{HlsSegmentSeconds})"]);
        }
        args.Insert(0, "-nostdin");
        args.AddRange(["-f", "hls", "-hls_time", HlsSegmentSeconds.ToString(CultureInfo.InvariantCulture), "-hls_list_size", "0",
            "-hls_playlist_type", "event", "-hls_flags", "independent_segments+temp_file", "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", HlsSessions.InitSegment, "-start_number", "0",
            "-hls_segment_filename", Path.Combine(folder, "seg%d.m4s"), Path.Combine(folder, HlsSessions.Playlist)]);
        return args;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MediaCodecs> _probed = new();

    /// <summary>
    /// The codecs of a video's first picture and sound streams, by ffprobe (which comes with ffmpeg). Null when it can't
    /// tell, in which case the video is converted in full. Remembered per file on the server's disk.
    /// </summary>
    public async Task<MediaCodecs?> ProbeAsync(string input, CancellationToken ct)
    {
        var isFile = !input.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (isFile && _probed.TryGetValue(input, out var known)) return known;

        var ffmpeg = options.Value.FfmpegPath;
        var name = Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(ffmpeg);
        var psi = new ProcessStartInfo(string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", input })
            psi.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                // One line per stream, "codec_name,codec_type" (an attachment can have no codec name).
                var streams = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split(','))
                    .Where(p => p.Length >= 2)
                    .ToList();
                var codecs = new MediaCodecs(
                    streams.FirstOrDefault(p => p[1] == "video")?[0],
                    streams.FirstOrDefault(p => p[1] == "audio")?[0]);
                if (codecs.Video is null) return null;
                if (isFile) _probed[input] = codecs;
                return codecs;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            log.LogDebug(ex, "ffprobe couldn't read the codecs");
            return null;
        }
    }

    /// <summary>
    /// Said when a saved file can't be read as a video: a fake release (an archive or junk named as a film) or a
    /// damaged one. Nothing on the server can help it; another version can.
    /// </summary>
    public const string UnreadableMessage =
        "The saved file isn't a video the server can read: this release is probably fake or damaged. Delete it in Downloads and save another version.";

    /// <summary>
    /// Why ffprobe can't read <paramref name="path"/> as a video, or null when it can (or when there's no ffprobe to
    /// ask, or it takes too long: only a clear answer counts).
    /// </summary>
    public async Task<string?> UnreadableReasonAsync(string path, CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var name = Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(ffmpeg);
        var psi = new ProcessStartInfo(string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", path })
            psi.ArgumentList.Add(a);
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: 0 }) return "the file is empty";
            using var process = Process.Start(psi);
            if (process is null) return null;
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0)
                    return (await error).Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"ffprobe exited with {process.ExitCode}";
                return output.Split('\n').Any(l => l.Trim() == "video") ? null : "there's no video in it";
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            log.LogDebug(ex, "ffprobe couldn't check {Path}", path);
            return null;
        }
    }

    /// <summary>Input, H.264 video and stereo AAC: what every player can decode.</summary>
    private static List<string> EncodeArguments(string input, double startSeconds, int? maxVideoKbps, int? maxHeight)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (startSeconds > 0) args.AddRange(["-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-sn"]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-pix_fmt", "yuv420p"]);
        if (maxVideoKbps is > 0)
            args.AddRange(["-b:v", $"{maxVideoKbps}k", "-maxrate", $"{maxVideoKbps}k", "-bufsize", $"{maxVideoKbps * 2}k"]);
        else
            args.AddRange(["-crf", "21"]);
        if (maxHeight is > 0) args.AddRange(["-vf", $"scale=-2:'min({maxHeight},ih)'"]);
        args.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "160k"]);
        return args;
    }

    /// <summary>A small JPEG of the frame at <paramref name="seconds"/>, or null if ffmpeg couldn't get one.</summary>
    public async Task<byte[]?> ThumbnailAsync(string input, double seconds, int width, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -ss before -i seeks by keyframes without decoding everything up to that point. -noaccurate_seek and decoding
        // keyframes only take the keyframe at the seek point as it is, rather than decoding on to the exact second: a
        // preview doesn't need to be exact, and from a torrent every extra frame is more data to wait for.
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-skip_frame", "nokey", "-noaccurate_seek",
                     "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
                     "-frames:v", "1", "-vf", $"scale={width}:-2", "-q:v", "6", "-f", "image2", "-c:v", "mjpeg", "pipe:1",
                 })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            using var buffer = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 && buffer.Length > 0 ? buffer.ToArray() : null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public static IReadOnlyList<string> TrickplayArguments(string input, int stepSeconds, int width, string outputPattern) =>
    [
        "-hide_banner", "-loglevel", "error", "-skip_frame", "nokey", "-i", input, "-an", "-sn",
        "-vf", $"fps=1/{stepSeconds},scale={width}:-2", "-q:v", "6", "-f", "image2", outputPattern,
    ];

    /// <summary>
    /// Writes a preview frame for every <paramref name="stepSeconds"/> of a finished file into <paramref name="folder"/>,
    /// named by its time in seconds ("0.jpg", "10.jpg", ...), in one pass that only decodes keyframes. Frames already
    /// there (made one at a time while it streamed) are kept.
    /// </summary>
    public async Task TrickplayAsync(string input, string folder, int stepSeconds, int width, CancellationToken ct)
    {
        var work = Path.Combine(folder, ".work");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var psi = new ProcessStartInfo(options.Value.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in TrickplayArguments(input, stepSeconds, width, Path.Combine(work, "%d.jpg"))) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        try
        {
            // A whole film shouldn't hold up playback: it runs at a lower priority than the streams.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
        }
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed: {(await stderr).Trim()}");

        // ffmpeg numbers them from 1, the first being the frame at 0 s.
        foreach (var file in Directory.EnumerateFiles(work, "*.jpg"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var n)) continue;
            var target = Path.Combine(folder, $"{(n - 1) * stepSeconds}.jpg");
            if (File.Exists(target)) File.Delete(file);
            else File.Move(file, target);
        }
        Directory.Delete(work, recursive: true);
    }

    /// <summary>
    /// Runs ffmpeg and copies its output to <paramref name="output"/> until done or cancelled. Nothing is written until
    /// ffmpeg has produced its first bytes: if it fails before that (it can't read or decode the input), a
    /// <see cref="TranscodeFailedException"/> says why, while there's still time to answer with an error rather than an
    /// empty "video" (which a player can only report as an unreadable file).
    /// </summary>
    public async Task TranscodeAsync(string input, double startSeconds, int? maxVideoKbps, int? maxHeight, Stream output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in BuildArguments(input, startSeconds, maxVideoKbps, maxHeight)) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var failed = false;
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var first = new byte[64 * 1024];
            var read = await stdout.ReadAsync(first, ct);
            if (read == 0)
            {
                await process.WaitForExitAsync(ct);
                failed = true;
                var err = (await stderr).Trim();
                throw new TranscodeFailedException(
                    err.Length > 0 ? err.Split('\n').Last(l => l.Trim().Length > 0).Trim() : $"ffmpeg stopped (exit code {process.ExitCode}) without any output.");
            }
            await output.WriteAsync(first.AsMemory(0, read), ct);
            await stdout.CopyToAsync(output, ct);
            await process.WaitForExitAsync(ct);
            failed = process.ExitCode != 0;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            var err = await stderr;
            // A failure is worth seeing in the server's log (it used to be hidden at debug level); a cancelled one isn't.
            if (failed && !ct.IsCancellationRequested) log.LogWarning("ffmpeg couldn't convert {Input}: {Err}", Redact(input), err.Trim());
            else if (!string.IsNullOrWhiteSpace(err)) log.LogDebug("ffmpeg: {Err}", err);
        }
    }

    /// <summary>The input as it can be logged: a loopback URL's access token left out.</summary>
    private static string Redact(string input)
    {
        var i = input.IndexOf("access_token=", StringComparison.Ordinal);
        return i < 0 ? input : input[..i] + "access_token=…";
    }
}

/// <summary>A video's picture and sound codecs, as ffmpeg names them ("h264", "hevc", "aac", "dts"...).</summary>
public record MediaCodecs(string? Video, string? Audio)
{
    /// <summary>Picture Apple's player decodes, so it can be repackaged without re-encoding.</summary>
    public bool CanCopyVideoForApple => Video is "h264" or "hevc";

    /// <summary>Sound Apple's player decodes (no sound at all counts: there's nothing to convert).</summary>
    public bool CanCopyAudioForApple => Audio is null or "aac" or "ac3" or "eac3";
}

/// <summary>ffmpeg couldn't convert a video: its own last word on why.</summary>
public class TranscodeFailedException(string message) : Exception(message)
{
}
