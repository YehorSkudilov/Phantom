using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Phantom.Shared;

namespace Phantom.Services;

public enum OfflineStatus { Queued, Downloading, Paused, Completed, Failed, WaitingForWifi }

/// <summary>A movie or a show's episode copied to this device for offline playback ("Downloads" in Plex).</summary>
public partial class OfflineDownload : ObservableObject
{
    /// <summary>The movie's id; 0 for an episode.</summary>
    public int MovieId { get; set; }

    /// <summary>Set for a show's episode (then Title is the show's, and Subtitle names the episode).</summary>
    public int? EpisodeId { get; set; }
    public int? ShowId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    /// <summary>"S1 · E2 · The Title" for an episode.</summary>
    public string? Subtitle { get; set; }

    public bool IsEpisode => EpisodeId is not null;

    /// <summary>Names its files: the movie's id, or "ep" and the episode's.</summary>
    public string FileKey => EpisodeId is { } e ? $"ep{e}" : MovieId.ToString();
    public int? ReleaseId { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string? PosterUrl { get; set; }
    public string? LocalPosterPath { get; set; }
    public string FilePath { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    [ObservableProperty] private OfflineStatus status;
    [ObservableProperty] private long totalBytes;
    [ObservableProperty] private long downloadedBytes;
    [ObservableProperty] private long bytesPerSecond;
    [ObservableProperty] private string? error;

    /// <summary>Resume point recorded while offline; pushed to the server when back online.</summary>
    [ObservableProperty] private long offlinePositionMs;
    public long OfflineDurationMs { get; set; }
    public bool PendingSync { get; set; }

    public double Progress => TotalBytes <= 0 ? 0 : (double)DownloadedBytes / TotalBytes;
    public string PosterSource => LocalPosterPath is { } p && File.Exists(p) ? p : PosterUrl ?? "logo_mark.png";
    public bool IsComplete => Status == OfflineStatus.Completed;
    public bool CanPause => Status is OfflineStatus.Downloading or OfflineStatus.Queued or OfflineStatus.WaitingForWifi;
    public bool CanResume => Status is OfflineStatus.Paused or OfflineStatus.Failed;

    /// <summary>The status as shown under the title.</summary>
    public string StatusText => Status switch
    {
        OfflineStatus.Queued => "Waiting to start",
        OfflineStatus.Downloading => "Downloading",
        OfflineStatus.Paused => "Paused",
        OfflineStatus.Completed => "Ready to watch offline",
        OfflineStatus.Failed => "Download failed",
        OfflineStatus.WaitingForWifi => "Waiting for Wi-Fi",
        _ => Status.ToString(),
    };

    /// <summary>"1.2 GB of 4.5 GB · 3.1 MB/s" while downloading, the size once done.</summary>
    public string SizeText => IsComplete
        ? Phantom.BytesConverter.Format(TotalBytes)
        : TotalBytes <= 0 ? ""
        : $"{Phantom.BytesConverter.Format(DownloadedBytes)} of {Phantom.BytesConverter.Format(TotalBytes)}"
          + (BytesPerSecond > 0 ? $"  ·  {Phantom.BytesConverter.Format(BytesPerSecond)}/s" : "");

    partial void OnDownloadedBytesChanged(long value) { OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(SizeText)); }
    partial void OnTotalBytesChanged(long value) { OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(SizeText)); }
    partial void OnBytesPerSecondChanged(long value) => OnPropertyChanged(nameof(SizeText));
    partial void OnStatusChanged(OfflineStatus value)
    {
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SizeText));
    }
}

/// <summary>
/// Client-side downloads: copies the server's direct stream to app storage with resumable HTTP range requests,
/// up to <see cref="MaxParallel"/> at a time, honouring the Wi-Fi-only preference. The torrent itself is always
/// fetched by the server (see EnqueueAsync); the device only ever downloads over HTTP from it.
/// </summary>
public class DownloadManager
{
    private readonly HttpClient _http;
    private readonly PhantomApi _api;
    private readonly SettingsService _settings;
    private readonly string _root = Path.Combine(FileSystem.AppDataDirectory, "downloads");
    private readonly string _indexPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Dictionary<OfflineDownload, CancellationTokenSource> _running = [];
    private readonly Lock _queueLock = new();

    /// <summary>How many movies download to the device at once.</summary>
    public const int MaxParallel = 3;

    /// <summary>Waits before retrying an interrupted download (each picks up where it stopped, via the .part file);
    /// after the last it shows as failed, and Resume starts the retries over.</summary>
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>No data for this long counts as an interrupted download (retried) rather than a slow one.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);

    private readonly Dictionary<OfflineDownload, int> _retries = [];
    private readonly HashSet<OfflineDownload> _waiting = [];

    public ObservableCollection<OfflineDownload> Items { get; } = [];

    public DownloadManager(HttpClient http, PhantomApi api, SettingsService settings)
    {
        _http = http;
        _api = api;
        _settings = settings;
        _indexPath = Path.Combine(_root, "index.json");
        Directory.CreateDirectory(_root);
        Load();
        Connectivity.Current.ConnectivityChanged += (_, _) => Kick();
    }

    public OfflineDownload? Find(int movieId) => Items.FirstOrDefault(i => i.EpisodeId is null && i.MovieId == movieId);

    public OfflineDownload? FindEpisode(int episodeId) => Items.FirstOrDefault(i => i.EpisodeId == episodeId);

    /// <summary>The downloaded episode of the same show after <paramref name="item"/>, for playing on offline.</summary>
    public OfflineDownload? NextDownloadedEpisode(OfflineDownload item) => Items
        .Where(i => i.IsComplete && i.ShowId == item.ShowId && i.EpisodeId != item.EpisodeId
                    && (i.SeasonNumber > item.SeasonNumber || i.SeasonNumber == item.SeasonNumber && i.EpisodeNumber > item.EpisodeNumber))
        .OrderBy(i => i.SeasonNumber).ThenBy(i => i.EpisodeNumber)
        .FirstOrDefault();

    public long UsedBytes => Items.Sum(i => i.DownloadedBytes);

    public Task EnqueueAsync(MovieDetailDto movie, int? releaseId)
    {
        if (Find(movie.Id) is { } existing) return ResumeIfStopped(existing);
        return AddAsync(new OfflineDownload
        {
            MovieId = movie.Id,
            ReleaseId = releaseId,
            Title = movie.Title,
            Year = movie.Year,
            Overview = movie.Overview,
            RuntimeMinutes = movie.RuntimeMinutes,
            PosterUrl = movie.PosterUrl,
            Status = OfflineStatus.Queued,
        });
    }

    /// <summary>A show's episode to the device. It's listed under the show (its poster and title), with the episode below.</summary>
    public Task EnqueueEpisodeAsync(ShowDetailDto show, EpisodeDto episode)
    {
        if (FindEpisode(episode.Id) is { } existing) return ResumeIfStopped(existing);
        return AddAsync(new OfflineDownload
        {
            EpisodeId = episode.Id,
            ShowId = show.Id,
            SeasonNumber = episode.SeasonNumber,
            EpisodeNumber = episode.EpisodeNumber,
            Subtitle = $"S{episode.SeasonNumber} · E{episode.EpisodeNumber}" + (string.IsNullOrWhiteSpace(episode.Title) ? "" : $" · {episode.Title}"),
            Title = show.Title,
            Year = show.Year,
            Overview = episode.Overview ?? show.Overview,
            RuntimeMinutes = episode.RuntimeMinutes,
            PosterUrl = show.PosterUrl,
            Status = OfflineStatus.Queued,
        });
    }

    private Task ResumeIfStopped(OfflineDownload existing)
    {
        if (existing.Status is OfflineStatus.Failed or OfflineStatus.Paused) Resume(existing);
        return Task.CompletedTask;
    }

    private async Task AddAsync(OfflineDownload item)
    {
        Items.Insert(0, item);
        await CachePosterAsync(item);
        await SaveAsync();
        Kick();

        // Have the server download (and keep) the whole torrent too: it fetches from the swarm at full speed while
        // this device copies from it, and a paused or interrupted device download picks up from the server's copy.
        try
        {
            await _api.QueueServerDownloadAsync(item.MovieId, item.ReleaseId, item.EpisodeId);
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException)
        {
            // The device download still works without it, streaming the torrent through the server as it goes.
        }
    }

    public void Pause(OfflineDownload item)
    {
        Cancel(item);
        item.Status = OfflineStatus.Paused;
        item.BytesPerSecond = 0;
        _ = SaveAsync();
    }

    public void Resume(OfflineDownload item)
    {
        lock (_queueLock) _retries.Remove(item);
        item.Status = OfflineStatus.Queued;
        item.Error = null;
        _ = SaveAsync();
        Kick();
    }

    public async Task DeleteAsync(OfflineDownload item)
    {
        Cancel(item);
        lock (_queueLock)
        {
            _retries.Remove(item);
            _waiting.Remove(item);
        }
        Items.Remove(item);
        var posterShared = Items.Any(i => i.LocalPosterPath == item.LocalPosterPath);
        foreach (var path in new[] { item.FilePath, item.FilePath + ".part", posterShared ? null : item.LocalPosterPath })
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        await SaveAsync();
    }

    /// <summary>Records an offline resume point (a movie's, or with <paramref name="episodeId"/> an episode's), synced to the server later.</summary>
    public async Task RecordOfflineProgressAsync(int movieId, long positionMs, long durationMs, int? episodeId = null)
    {
        if ((episodeId is { } e ? FindEpisode(e) : Find(movieId)) is not { } item) return;
        item.OfflinePositionMs = positionMs;
        item.OfflineDurationMs = durationMs;
        item.PendingSync = true;
        await SaveAsync();
    }

    public async Task SyncOfflineProgressAsync()
    {
        foreach (var item in Items.Where(i => i.PendingSync).ToList())
        {
            try
            {
                await _api.ReportTimelineAsync(new TimelineUpdate(item.MovieId, item.ReleaseId, item.OfflinePositionMs, item.OfflineDurationMs,
                    PlayerState.Stopped, _settings.ClientName, item.EpisodeId));
                item.PendingSync = false;
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException)
            {
                return; // still offline; try again later
            }
        }
        await SaveAsync();
    }

    private void Cancel(OfflineDownload item)
    {
        lock (_queueLock)
        {
            if (_running.TryGetValue(item, out var cts)) cts.Cancel();
        }
    }

    /// <summary>Starts queued items until <see cref="MaxParallel"/> are downloading.</summary>
    private void Kick()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet && Connectivity.Current.NetworkAccess != NetworkAccess.ConstrainedInternet)
            return;

        lock (_queueLock)
        {
            // Oldest first: new downloads are inserted at the top of the list.
            foreach (var next in Items.ToList().AsEnumerable().Reverse().Where(i => i.Status is OfflineStatus.Queued or OfflineStatus.WaitingForWifi).ToList())
            {
                if (_running.Count >= MaxParallel) return;
                if (_running.ContainsKey(next) || _waiting.Contains(next)) continue;
                if (_settings.DownloadOnWifiOnly && !OnWifi())
                {
                    MainThread.BeginInvokeOnMainThread(() => next.Status = OfflineStatus.WaitingForWifi);
                    continue;
                }

                var cts = new CancellationTokenSource();
                _running[next] = cts; // DownloadAsync marks it Downloading, on the UI thread
                _ = Task.Run(() => RunAsync(next, cts));
            }
        }
    }

    private static bool OnWifi() =>
        Connectivity.Current.ConnectionProfiles.Any(p => p is ConnectionProfile.WiFi or ConnectionProfile.Ethernet);

    private async Task RunAsync(OfflineDownload item, CancellationTokenSource cts)
    {
        try
        {
            await DownloadAsync(item, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Paused or deleted.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ApiException or TimeoutException)
        {
            int attempt;
            lock (_queueLock)
            {
                _retries.TryGetValue(item, out attempt);
                _retries[item] = attempt + 1;
            }

            if (attempt < RetryDelays.Length && !cts.IsCancellationRequested)
            {
                // Interrupted (connection dropped, server restarted, torrent stalled...): try again shortly.
                var delay = RetryDelays[attempt];
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    item.Status = OfflineStatus.Queued;
                    item.Error = $"Interrupted, retrying in {(delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} min" : $"{delay.TotalSeconds:0} s")}…";
                    item.BytesPerSecond = 0;
                });
                lock (_queueLock)
                {
                    _running.Remove(item);
                    _waiting.Add(item);
                }
                cts.Dispose();
                await SaveAsync();
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    lock (_queueLock) _waiting.Remove(item);
                    Kick();
                }, TaskScheduler.Default);
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.Status = OfflineStatus.Failed;
                item.Error = ex.Message;
                item.BytesPerSecond = 0;
            });
        }
        finally
        {
            lock (_queueLock) _running.Remove(item);
        }
        cts.Dispose();
        await SaveAsync();
        Kick();
    }

    private async Task DownloadAsync(OfflineDownload item, CancellationToken ct)
    {
        MainThread.BeginInvokeOnMainThread(() => item.Status = OfflineStatus.Downloading);

        // Fresh URL every attempt: it embeds an auth token.
        var info = await _api.StreamInfoAsync(item.MovieId, item.ReleaseId, ct, item.EpisodeId);
        item.ReleaseId = info.ReleaseId;
        var ext = Path.GetExtension(info.FileName);
        item.FilePath = Path.Combine(_root, $"{item.FileKey}{(string.IsNullOrEmpty(ext) ? ".mp4" : ext)}");
        var part = item.FilePath + ".part";

        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, info.DirectUrl + "&download=true");
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && res.StatusCode != HttpStatusCode.PartialContent) existing = 0; // server ignored the range
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException(PhantomApi.Describe(res.StatusCode, await res.Content.ReadAsStringAsync(ct)), null, res.StatusCode);

        var total = existing + (res.Content.Headers.ContentLength ?? info.FileSize - existing);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            item.TotalBytes = total;
            item.DownloadedBytes = existing;
            item.Error = null;
        });

        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = existing, windowBytes = 0;
            var window = System.Diagnostics.Stopwatch.StartNew();
            int read;
            while ((read = await ReadWithStallTimeoutAsync(src, buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                windowBytes += read;
                if (window.ElapsedMilliseconds >= 500)
                {
                    var rate = (long)(windowBytes * 1000.0 / window.ElapsedMilliseconds);
                    var snapshot = done;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        item.DownloadedBytes = snapshot;
                        item.BytesPerSecond = rate;
                    });
                    windowBytes = 0;
                    window.Restart();
                }
            }
        }

        File.Move(part, item.FilePath, overwrite: true);
        lock (_queueLock) _retries.Remove(item);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            item.Error = null;
            item.DownloadedBytes = item.TotalBytes = new FileInfo(item.FilePath).Length;
            item.BytesPerSecond = 0;
            item.Status = OfflineStatus.Completed;
        });
    }

    /// <summary>A read that gives up (as an interruption, so it's retried) when no data arrives for a while.</summary>
    private static async Task<int> ReadWithStallTimeoutAsync(Stream src, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await src.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("No data from the server for a while.");
        }
    }

    private async Task CachePosterAsync(OfflineDownload item)
    {
        if (string.IsNullOrEmpty(item.PosterUrl)) return;
        try
        {
            var bytes = await _http.GetByteArrayAsync(item.PosterUrl);
            // Episodes of one show share its poster.
            item.LocalPosterPath = Path.Combine(_root, item.ShowId is { } show ? $"show{show}.poster.jpg" : $"{item.MovieId}.poster.jpg");
            await File.WriteAllBytesAsync(item.LocalPosterPath, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            item.LocalPosterPath = null; // fall back to the remote URL
        }
    }

    private record Snapshot(int MovieId, int? ReleaseId, string Title, int? Year, string? Overview, int? RuntimeMinutes, string? PosterUrl,
        string? LocalPosterPath, string FilePath, DateTime AddedAt, OfflineStatus Status, long TotalBytes, long DownloadedBytes, string? Error,
        long OfflinePositionMs, long OfflineDurationMs, bool PendingSync,
        // Episodes (absent, so null, in indexes written before they could be downloaded).
        int? EpisodeId = null, int? ShowId = null, int? SeasonNumber = null, int? EpisodeNumber = null, string? Subtitle = null);

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var snapshot = Items.Select(i => new Snapshot(i.MovieId, i.ReleaseId, i.Title, i.Year, i.Overview, i.RuntimeMinutes, i.PosterUrl,
                i.LocalPosterPath, i.FilePath, i.AddedAt, i.Status == OfflineStatus.Downloading ? OfflineStatus.Queued : i.Status,
                i.TotalBytes, i.DownloadedBytes, i.Error, i.OfflinePositionMs, i.OfflineDurationMs, i.PendingSync,
                i.EpisodeId, i.ShowId, i.SeasonNumber, i.EpisodeNumber, i.Subtitle)).ToList();
            await File.WriteAllTextAsync(_indexPath, JsonSerializer.Serialize(snapshot));
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_indexPath)) return;
        try
        {
            var items = JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(_indexPath)) ?? [];
            foreach (var s in items)
            {
                var complete = s.Status == OfflineStatus.Completed && File.Exists(s.FilePath);
                Items.Add(new OfflineDownload
                {
                    MovieId = s.MovieId, ReleaseId = s.ReleaseId, Title = s.Title, Year = s.Year, Overview = s.Overview,
                    RuntimeMinutes = s.RuntimeMinutes, PosterUrl = s.PosterUrl, LocalPosterPath = s.LocalPosterPath,
                    FilePath = s.FilePath, AddedAt = s.AddedAt,
                    Status = complete ? OfflineStatus.Completed : s.Status == OfflineStatus.Completed ? OfflineStatus.Queued : s.Status,
                    TotalBytes = s.TotalBytes, DownloadedBytes = s.DownloadedBytes, Error = s.Error,
                    OfflinePositionMs = s.OfflinePositionMs, OfflineDurationMs = s.OfflineDurationMs, PendingSync = s.PendingSync,
                    EpisodeId = s.EpisodeId, ShowId = s.ShowId, SeasonNumber = s.SeasonNumber, EpisodeNumber = s.EpisodeNumber, Subtitle = s.Subtitle,
                });
            }
            Kick();
        }
        catch (JsonException)
        {
            // Corrupt index: start fresh rather than crash.
        }
    }
}
