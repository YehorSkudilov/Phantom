using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Phantom.Shared;

namespace Phantom.Services;

public enum OfflineStatus { Queued, Downloading, Paused, Completed, Failed, WaitingForWifi }

/// <summary>A movie copied to this device for offline playback ("Downloads" in Plex).</summary>
public partial class OfflineDownload : ObservableObject
{
    public int MovieId { get; set; }
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

    partial void OnDownloadedBytesChanged(long value) => OnPropertyChanged(nameof(Progress));
    partial void OnTotalBytesChanged(long value) => OnPropertyChanged(nameof(Progress));
    partial void OnStatusChanged(OfflineStatus value) => OnPropertyChanged(nameof(IsComplete));
}

/// <summary>
/// Client-side downloads: copies the server's direct stream to app storage with resumable
/// HTTP range requests, one item at a time, honouring the Wi-Fi-only preference.
/// </summary>
public class DownloadManager
{
    private readonly HttpClient _http;
    private readonly PhantomApi _api;
    private readonly SettingsService _settings;
    private readonly string _root = Path.Combine(FileSystem.AppDataDirectory, "downloads");
    private readonly string _indexPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _current;
    private Task? _worker;

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

    public OfflineDownload? Find(int movieId) => Items.FirstOrDefault(i => i.MovieId == movieId);

    public long UsedBytes => Items.Sum(i => i.DownloadedBytes);

    public async Task EnqueueAsync(MovieDetailDto movie, int? releaseId)
    {
        if (Find(movie.Id) is { } existing)
        {
            if (existing.Status is OfflineStatus.Failed or OfflineStatus.Paused) Resume(existing);
            return;
        }

        var item = new OfflineDownload
        {
            MovieId = movie.Id,
            ReleaseId = releaseId,
            Title = movie.Title,
            Year = movie.Year,
            Overview = movie.Overview,
            RuntimeMinutes = movie.RuntimeMinutes,
            PosterUrl = movie.PosterUrl,
            Status = OfflineStatus.Queued,
        };
        Items.Insert(0, item);
        await CachePosterAsync(item);
        await SaveAsync();
        Kick();
    }

    public void Pause(OfflineDownload item)
    {
        if (item.Status == OfflineStatus.Downloading) _current?.Cancel();
        item.Status = OfflineStatus.Paused;
        item.BytesPerSecond = 0;
        _ = SaveAsync();
    }

    public void Resume(OfflineDownload item)
    {
        item.Status = OfflineStatus.Queued;
        item.Error = null;
        _ = SaveAsync();
        Kick();
    }

    public async Task DeleteAsync(OfflineDownload item)
    {
        if (item.Status == OfflineStatus.Downloading) _current?.Cancel();
        Items.Remove(item);
        foreach (var path in new[] { item.FilePath, item.FilePath + ".part", item.LocalPosterPath })
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        await SaveAsync();
    }

    /// <summary>Records an offline resume point, synced to the server later.</summary>
    public async Task RecordOfflineProgressAsync(int movieId, long positionMs, long durationMs)
    {
        if (Find(movieId) is not { } item) return;
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
                await _api.ReportTimelineAsync(new TimelineUpdate(item.MovieId, item.ReleaseId, item.OfflinePositionMs, item.OfflineDurationMs, PlayerState.Stopped, _settings.ClientName));
                item.PendingSync = false;
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException)
            {
                return; // still offline; try again later
            }
        }
        await SaveAsync();
    }

    private void Kick()
    {
        if (_worker is { IsCompleted: false }) return;
        _worker = Task.Run(RunQueueAsync);
    }

    private static bool OnWifi() =>
        Connectivity.Current.ConnectionProfiles.Any(p => p is ConnectionProfile.WiFi or ConnectionProfile.Ethernet);

    private async Task RunQueueAsync()
    {
        while (true)
        {
            var next = Items.LastOrDefault(i => i.Status is OfflineStatus.Queued or OfflineStatus.WaitingForWifi);
            if (next is null) return;
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet && Connectivity.Current.NetworkAccess != NetworkAccess.ConstrainedInternet)
                return;
            if (_settings.DownloadOnWifiOnly && !OnWifi())
            {
                MainThread.BeginInvokeOnMainThread(() => next.Status = OfflineStatus.WaitingForWifi);
                return;
            }

            _current = new CancellationTokenSource();
            try
            {
                await DownloadAsync(next, _current.Token);
            }
            catch (OperationCanceledException)
            {
                // Paused or deleted.
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ApiException)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    next.Status = OfflineStatus.Failed;
                    next.Error = ex.Message;
                    next.BytesPerSecond = 0;
                });
            }
            await SaveAsync();
        }
    }

    private async Task DownloadAsync(OfflineDownload item, CancellationToken ct)
    {
        MainThread.BeginInvokeOnMainThread(() => item.Status = OfflineStatus.Downloading);

        // Fresh URL every attempt: it embeds an auth token.
        var info = await _api.StreamInfoAsync(item.MovieId, item.ReleaseId, ct);
        item.ReleaseId = info.ReleaseId;
        var ext = Path.GetExtension(info.FileName);
        item.FilePath = Path.Combine(_root, $"{item.MovieId}{(string.IsNullOrEmpty(ext) ? ".mp4" : ext)}");
        var part = item.FilePath + ".part";

        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, info.DirectUrl + "&download=true");
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && res.StatusCode != HttpStatusCode.PartialContent) existing = 0; // server ignored the range
        res.EnsureSuccessStatusCode();

        var total = existing + (res.Content.Headers.ContentLength ?? info.FileSize - existing);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            item.TotalBytes = total;
            item.DownloadedBytes = existing;
        });

        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = existing, windowBytes = 0;
            var window = System.Diagnostics.Stopwatch.StartNew();
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
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
        MainThread.BeginInvokeOnMainThread(() =>
        {
            item.DownloadedBytes = item.TotalBytes = new FileInfo(item.FilePath).Length;
            item.BytesPerSecond = 0;
            item.Status = OfflineStatus.Completed;
        });
    }

    private async Task CachePosterAsync(OfflineDownload item)
    {
        if (string.IsNullOrEmpty(item.PosterUrl)) return;
        try
        {
            var bytes = await _http.GetByteArrayAsync(item.PosterUrl);
            item.LocalPosterPath = Path.Combine(_root, $"{item.MovieId}.poster.jpg");
            await File.WriteAllBytesAsync(item.LocalPosterPath, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            item.LocalPosterPath = null; // fall back to the remote URL
        }
    }

    private record Snapshot(int MovieId, int? ReleaseId, string Title, int? Year, string? Overview, int? RuntimeMinutes, string? PosterUrl,
        string? LocalPosterPath, string FilePath, DateTime AddedAt, OfflineStatus Status, long TotalBytes, long DownloadedBytes, string? Error,
        long OfflinePositionMs, long OfflineDurationMs, bool PendingSync);

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var snapshot = Items.Select(i => new Snapshot(i.MovieId, i.ReleaseId, i.Title, i.Year, i.Overview, i.RuntimeMinutes, i.PosterUrl,
                i.LocalPosterPath, i.FilePath, i.AddedAt, i.Status == OfflineStatus.Downloading ? OfflineStatus.Queued : i.Status,
                i.TotalBytes, i.DownloadedBytes, i.Error, i.OfflinePositionMs, i.OfflineDurationMs, i.PendingSync)).ToList();
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
