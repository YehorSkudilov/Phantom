using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Phantom.Api.Services;

/// <summary>
/// Conversions served as HLS (a playlist of short segments), for Apple's player. AVPlayer only plays HTTP video it can
/// fetch in byte ranges, and the piped MP4 of <see cref="Transcoder.TranscodeAsync"/> has no length to range over, so
/// on iOS and macOS a converted video wouldn't play at all. Here ffmpeg writes the segments into a folder per session
/// instead, and they're served as files.
/// <para>
/// The client names each session (a new one per start position, as a seek restarts the conversion). A viewer's newer
/// session for the same video replaces the older one, and one nobody has asked anything of for a minute is stopped;
/// either way its ffmpeg is ended and its folder deleted.
/// </para>
/// </summary>
public partial class HlsSessions(IOptions<PhantomOptions> options, ILogger<HlsSessions> log) : BackgroundService
{
    public const string Playlist = "index.m3u8";

    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);

    /// <summary>How long a playlist request waits for ffmpeg's first segment (from a slow torrent, it can be a while).</summary>
    private static readonly TimeSpan FirstSegmentTimeout = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new();
    private readonly Lock _starting = new();

    private string Root => Path.Combine(options.Value.CacheDirectory, "hls");

    [GeneratedRegex(@"^seg\d+\.ts$")]
    private static partial Regex SegmentName();

    /// <summary>A file a session's playlist names (and nothing else, so a request can't reach outside its folder).</summary>
    public static bool IsSegment(string name) => SegmentName().IsMatch(name);

    /// <summary>A session id as clients make them: a GUID, which also keeps it a safe folder name.</summary>
    public static bool IsSessionId(string id) => Guid.TryParse(id, out _);

    /// <summary>The running session <paramref name="id"/>, if it's <paramref name="owner"/>'s; marks it as still wanted.</summary>
    public HlsSession? Find(string id, string owner)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Owner != owner) return null;
        session.Touch();
        return session;
    }

    /// <summary>
    /// Session <paramref name="id"/>, started now if it isn't running. <paramref name="owner"/> is the viewer and the
    /// video: an older session of theirs for it is stopped.
    /// </summary>
    public HlsSession Start(string id, string owner, string input, double startSeconds, int? maxVideoKbps, int? maxHeight)
    {
        lock (_starting)
        {
            if (Find(id, owner) is { } running) return running;
            foreach (var old in _sessions.Values.Where(s => s.Owner == owner).ToList()) Stop(old);

            var folder = Path.Combine(Root, id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            var psi = new ProcessStartInfo(options.Value.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in Transcoder.HlsArguments(input, startSeconds, maxVideoKbps, maxHeight, folder)) psi.ArgumentList.Add(a);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
            var session = new HlsSession(id, owner, folder, process);
            _sessions[id] = session;
            return session;
        }
    }

    /// <summary>
    /// The session's playlist once it has its first segment. Throws <see cref="TranscodeFailedException"/> with ffmpeg's
    /// reason when it stopped before making one.
    /// </summary>
    public async Task<string> PlaylistAsync(HlsSession session, CancellationToken ct)
    {
        var path = Path.Combine(session.Folder, Playlist);
        var deadline = DateTime.UtcNow + FirstSegmentTimeout;
        while (true)
        {
            session.Touch();
            // ffmpeg writes the playlist to a temporary file and renames it (temp_file), so it's never read half-written.
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path, ct);
                if (text.Contains("#EXTINF", StringComparison.Ordinal)) return text;
            }
            if (session.Process.HasExited)
            {
                var err = (await session.Errors).Trim();
                log.LogWarning("ffmpeg couldn't convert to HLS: {Err}", err);
                throw new TranscodeFailedException(err.Length > 0
                    ? err.Split('\n').Last(l => l.Trim().Length > 0).Trim()
                    : $"ffmpeg stopped (exit code {session.Process.ExitCode}) without any output.");
            }
            if (DateTime.UtcNow > deadline) throw new TranscodeFailedException("The conversion didn't get started in time.");
            await Task.Delay(250, ct);
        }
    }

    private void Stop(HlsSession session)
    {
        _sessions.TryRemove(session.Id, out _);
        try
        {
            if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
            session.Process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        session.Process.Dispose();
        try
        {
            if (Directory.Exists(session.Folder)) Directory.Delete(session.Folder, recursive: true);
        }
        catch (IOException ex)
        {
            log.LogDebug(ex, "Couldn't delete {Folder}", session.Folder);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Anything left from before a restart belongs to no session.
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException ex)
        {
            log.LogDebug(ex, "Couldn't clear {Folder}", Root);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var session in _sessions.Values.Where(s => DateTime.UtcNow - s.LastUsed > Idle).ToList())
                    lock (_starting) Stop(session);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var session in _sessions.Values.ToList()) Stop(session);
        }
    }
}

/// <summary>One running HLS conversion: its ffmpeg and the folder it writes to.</summary>
public sealed class HlsSession
{
    public HlsSession(string id, string owner, string folder, Process process)
    {
        Id = id;
        Owner = owner;
        Folder = folder;
        Process = process;
        Errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Touch();
    }

    public string Id { get; }
    public string Owner { get; }
    public string Folder { get; }
    public Process Process { get; }

    /// <summary>What ffmpeg says on stderr, once it has ended (read throughout, so its output pipe never fills).</summary>
    public Task<string> Errors { get; }

    public DateTime LastUsed { get; private set; }

    public void Touch() => LastUsed = DateTime.UtcNow;
}
