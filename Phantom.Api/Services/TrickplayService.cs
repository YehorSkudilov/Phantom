using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Phantom.Api.Services;

/// <summary>
/// Seek-bar previews made ahead of time ("trickplay", as Plex calls it), so they show the moment the bar is dragged
/// instead of an ffmpeg run per frame. Once a video's file is complete on the server it's queued here, and a frame for
/// every <see cref="StepSeconds"/> is written to its thumbnail folder, the same one the one-at-a-time previews use.
/// One video at a time, at low priority.
/// </summary>
public sealed class TrickplayService(Transcoder transcoder, IOptions<PhantomOptions> options, ILogger<TrickplayService> log) : BackgroundService
{
    /// <summary>Seconds between previews; the one-at-a-time previews are cached on the same steps.</summary>
    public const int StepSeconds = 10;
    private const int Width = 320;
    private const string CompleteMarker = ".complete";

    private readonly Channel<(string Key, string Input)> _queue = Channel.CreateUnbounded<(string, string)>();
    private readonly ConcurrentDictionary<string, bool> _queued = new();

    /// <summary>Where a video's previews are (see StreamTarget.CacheKey).</summary>
    public string Folder(string key) => Path.Combine(options.Value.CacheDirectory, "thumbnails", key);

    /// <summary>Every preview of the video has been made.</summary>
    public bool IsComplete(string key) => File.Exists(Path.Combine(Folder(key), CompleteMarker));

    /// <summary>Queues a finished file's previews, unless they're made, queued or can't be (no ffmpeg).</summary>
    public void Request(string key, string input)
    {
        if (!transcoder.IsAvailable || IsComplete(key) || !_queued.TryAdd(key, true)) return;
        _queue.Writer.TryWrite((key, input));
    }

    /// <summary>The previews made so far, by their time in seconds.</summary>
    public IReadOnlyList<(int Seconds, string Path)> Frames(string key)
    {
        var folder = Folder(key);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.jpg")
            .Select(f => (Ok: int.TryParse(Path.GetFileNameWithoutExtension(f), out var s), Seconds: s, Path: f))
            .Where(f => f.Ok)
            .OrderBy(f => f.Seconds)
            .Select(f => (f.Seconds, f.Path))
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (key, input) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (!File.Exists(input)) continue;
                var started = DateTime.UtcNow;
                Directory.CreateDirectory(Folder(key));
                await transcoder.TrickplayAsync(input, Folder(key), StepSeconds, Width, stoppingToken);
                await File.WriteAllTextAsync(Path.Combine(Folder(key), CompleteMarker), DateTime.UtcNow.ToString("O"), stoppingToken);
                log.LogInformation("Made seek previews for {Key} in {Seconds:0} s", key, (DateTime.UtcNow - started).TotalSeconds);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Seek previews for {Key} failed", key);
            }
            finally
            {
                _queued.TryRemove(key, out _);
            }
        }
    }
}
