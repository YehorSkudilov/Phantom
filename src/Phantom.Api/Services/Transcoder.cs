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
        args.AddRange(["-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1"]);
        return args;
    }

    /// <summary>Runs ffmpeg and copies its output to <paramref name="output"/> until done or cancelled.</summary>
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
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            var err = await stderr;
            if (!string.IsNullOrWhiteSpace(err)) log.LogDebug("ffmpeg: {Err}", err);
        }
    }
}
