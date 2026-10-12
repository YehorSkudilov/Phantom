using Phantom.Shared;

namespace Phantom.Services;

/// <summary>
/// What the server is saving and has saved, for the Save on server buttons to show it: on the server, how far along, or
/// not there. Fetched while some page shows such a button (<see cref="Watch"/>): every few seconds while something is
/// downloading, now and then otherwise.
/// </summary>
public class ServerDownloadTracker(PhantomApi api)
{
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    private IReadOnlyList<ServerDownloadDto> _items = [];
    private int _watchers;
    private CancellationTokenSource? _loop;

    /// <summary>The list changed (fetched again, or a download added or removed here).</summary>
    public event EventHandler? Changed;

    public ServerDownloadDto? ForMovie(int movieId) =>
        _items.FirstOrDefault(d => d.EpisodeId is null && d.MovieId == movieId && Counts(d));

    public ServerDownloadDto? ForEpisode(int episodeId) => _items.FirstOrDefault(d => d.EpisodeId == episodeId && Counts(d));

    private static bool Counts(ServerDownloadDto d) => d.Status is not (DownloadStatus.Cancelled or DownloadStatus.Failed);

    public static bool IsRunning(ServerDownloadDto? d) =>
        d?.Status is DownloadStatus.Queued or DownloadStatus.FetchingMetadata or DownloadStatus.Downloading;

    /// <summary>"Save on server", "Saving 42%", "On server" or "Paused": a button's label for a title's server copy.</summary>
    public static string Label(ServerDownloadDto? d) => d?.Status switch
    {
        null => "Save on server",
        DownloadStatus.Completed => "On server",
        DownloadStatus.Paused => "Paused on server",
        DownloadStatus.Queued or DownloadStatus.FetchingMetadata => "Saving…",
        _ => $"Saving {d.Progress / 100:0%}",
    };

    /// <summary>Keeps the list fresh until the returned handle is disposed (a page going away).</summary>
    public IDisposable Watch()
    {
        if (Interlocked.Increment(ref _watchers) == 1)
        {
            _loop = new CancellationTokenSource();
            _ = PollAsync(_loop.Token);
        }
        return new Watcher(this);
    }

    private void Unwatch()
    {
        if (Interlocked.Decrement(ref _watchers) == 0) _loop?.Cancel();
    }

    /// <summary>Fetches the list now (after saving or removing something, say).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            _items = await api.ServerDownloadsAsync();
            MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException || NetworkState.IsConnectionError(ex))
        {
            // Kept as it was; tried again on the next round.
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!NetworkState.InBackground) await RefreshAsync();
            try
            {
                await Task.Delay(_items.Any(IsRunning) ? Busy : Idle, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private sealed class Watcher(ServerDownloadTracker tracker) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) tracker.Unwatch();
        }
    }
}
