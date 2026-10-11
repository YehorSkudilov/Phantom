using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using MonoTorrent;
using MonoTorrent.Client;
using Phantom.Api.Data;
using Phantom.Api.Services.Sources;

namespace Phantom.Api.Services.Torrents;

/// <summary>Either a finished file on disk (<see cref="LocalPath"/>) or a live torrent stream.</summary>
public record TorrentMedia(string FileName, string? LocalPath, Stream? Stream);

public record TorrentStats(string InfoHash, string Name, TorrentState State, double Progress, long DownloadRate, long UploadRate, int Peers, long Size, bool Complete);

/// <summary>
/// Wraps a single MonoTorrent engine. Every release gets one TorrentManager, added in streaming
/// mode so pieces are fetched around the current playback position first. Torrents a client only
/// streams live in the cache directory and are evicted when idle; server downloads are kept.
/// </summary>
public sealed class TorrentEngine : IHostedService, IAsyncDisposable
{
    private readonly PhantomOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<TorrentEngine> _log;
    private readonly SemaphoreSlim _addLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastAccess = new();
    private readonly ConcurrentDictionary<string, bool> _pinned = new();
    private readonly ConcurrentDictionary<string, SharedTorrentStream> _streams = new();
    private ClientEngine? _engine;

    public TorrentEngine(IOptions<PhantomOptions> options, IHttpClientFactory httpFactory, ILogger<TorrentEngine> log)
    {
        _options = options.Value;
        _http = httpFactory.CreateClient(nameof(TorrentEngine));
        // Torrent sites and caches turn away requests with no browser-like user agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Phantom");
        _log = log;
    }

    private ClientEngine Engine => _engine ?? throw new InvalidOperationException("Torrent engine not started.");

    public long TotalDownloadRate => _engine?.TotalDownloadRate ?? 0;
    public long TotalUploadRate => _engine?.TotalUploadRate ?? 0;
    public int ActiveTorrents => _engine?.Torrents.Count(t => t.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata) ?? 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.EngineDirectory);
        Directory.CreateDirectory(_options.CacheDirectory);
        Directory.CreateDirectory(_options.MediaDirectory);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = _options.EngineDirectory,
            AllowPortForwarding = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            // Archive.org and similar torrents carry HTTP web seeds; use them immediately for fast starts.
            WebSeedDelay = TimeSpan.Zero,
            ListenEndPoints = new() { { "ipv4", new IPEndPoint(IPAddress.Any, _options.TorrentListenPort) } },
            DhtEndPoint = new IPEndPoint(IPAddress.Any, _options.TorrentListenPort),
        }.ToSettings();
        _engine = new ClientEngine(settings);
        _log.LogInformation("Torrent engine listening on port {Port}", _options.TorrentListenPort);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_engine is null) return;
        await _engine.StopAllAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        _engine?.Dispose();
        await Task.CompletedTask;
    }

    public async Task UpdateRateLimitsAsync(int maxDownloadKbps, int maxUploadKbps)
    {
        var builder = new EngineSettingsBuilder(Engine.Settings)
        {
            MaximumDownloadRate = maxDownloadKbps * 1024,
            MaximumUploadRate = maxUploadKbps * 1024,
        };
        await Engine.UpdateSettingsAsync(builder.ToSettings());
    }

    public TorrentManager? Find(string infoHash) =>
        _engine?.Torrents.FirstOrDefault(t => t.InfoHashes.V1OrV2.ToHex().Equals(infoHash, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds (or returns) the torrent for a release and makes sure it is running.</summary>
    public async Task<TorrentManager> EnsureStartedAsync(Release release, bool keep, CancellationToken ct)
    {
        Touch(release.InfoHash);
        if (keep) _pinned[release.InfoHash] = true;

        var manager = Find(release.InfoHash);
        if (manager is null)
        {
            await _addLock.WaitAsync(ct);
            try
            {
                manager = Find(release.InfoHash) ?? await AddAsync(release, keep, ct);
            }
            finally
            {
                _addLock.Release();
            }
        }

        if (manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error)
            await manager.StartAsync();

        if (!manager.HasMetadata)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await manager.WaitForMetadataAsync(timeout.Token);
        }

        await PrioritiseAsync(manager);
        return manager;
    }

    private async Task<TorrentManager> AddAsync(Release release, bool keep, CancellationToken ct)
    {
        var savePath = Path.Combine(keep ? _options.MediaDirectory : _options.CacheDirectory, release.InfoHash);
        var settings = new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings();

        // A .torrent file starts straight away. A magnet link first has to fetch the torrent's metadata from peers,
        // which hangs whenever the trackers are slow (and MonoTorrent's DHT rarely finds any), so it's the last resort:
        // the source's own .torrent link first, then public torrent caches, which have most popular torrents by hash.
        var urls = new List<string>();
        if (!string.IsNullOrEmpty(release.TorrentUrl) && !release.TorrentUrl.StartsWith("magnet:")) urls.Add(release.TorrentUrl);
        urls.AddRange(TorrentCaches.Select(c => string.Format(c, release.InfoHash.ToUpperInvariant())));
        foreach (var url in urls)
        {
            if (await DownloadTorrentAsync(url, release.InfoHash, ct) is { } torrent)
                return await Engine.AddStreamingAsync(torrent, savePath, settings);
        }

        _log.LogInformation("No .torrent file for {Release}; fetching its metadata from peers", release.Name);
        var magnetText = release.MagnetUri ?? (release.TorrentUrl?.StartsWith("magnet:") == true ? release.TorrentUrl : null)
                         ?? Magnets.Build(release.InfoHash, release.Name);
        return await Engine.AddStreamingAsync(MagnetLink.Parse(magnetText), savePath, settings);
    }

    /// <summary>Public caches serving .torrent files by info hash ({0}, upper case).</summary>
    private static readonly string[] TorrentCaches = ["https://itorrents.net/torrent/{0}.torrent", "https://itorrents.org/torrent/{0}.torrent"];

    /// <summary>Downloads a .torrent file, or null if it can't be had or isn't the torrent we want.</summary>
    private async Task<Torrent?> DownloadTorrentAsync(string url, string infoHash, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, timeout.Token);
            var torrent = await Torrent.LoadAsync(bytes);
            if (torrent.InfoHashes.V1OrV2.ToHex().Equals(infoHash, StringComparison.OrdinalIgnoreCase)) return torrent;
            _log.LogWarning("{Url} returned a different torrent than {InfoHash}", url, infoHash);
        }
        catch (Exception ex) when (ex is HttpRequestException or TorrentException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "No .torrent from {Url}", url);
        }
        return null;
    }

    /// <summary>Only download the main video and subtitles; skip samples, artwork, extras.</summary>
    private static async Task PrioritiseAsync(TorrentManager manager)
    {
        var video = MainVideo(manager);
        foreach (var file in manager.Files)
        {
            var wanted = file == video ? Priority.Normal
                : VideoFiles.IsSubtitle(file.Path) ? Priority.Highest
                : Priority.DoNotDownload;
            if (file.Priority != wanted) await manager.SetFilePriorityAsync(file, wanted);
        }
    }

    public static ITorrentManagerFile? MainVideo(TorrentManager manager) =>
        manager.Files.Where(f => VideoFiles.IsVideo(f.Path)).OrderByDescending(f => f.Length).FirstOrDefault()
        ?? manager.Files.OrderByDescending(f => f.Length).FirstOrDefault();

    public static IReadOnlyList<ITorrentManagerFile> Subtitles(TorrentManager manager) =>
        manager.Files.Where(f => VideoFiles.IsSubtitle(f.Path)).OrderBy(f => f.Path).ToList();

    public static bool IsFileComplete(ITorrentManagerFile file) => file.BitField.AllTrue;

    /// <summary>
    /// Opens the main video for playback. A finished file is returned as a path so it can be served
    /// straight from disk; otherwise a seekable stream whose reads wait for the needed pieces.
    /// </summary>
    public async Task<TorrentMedia> OpenAsync(Release release, CancellationToken ct)
    {
        var manager = await EnsureStartedAsync(release, keep: _pinned.ContainsKey(release.InfoHash), ct);
        var file = MainVideo(manager) ?? throw new InvalidOperationException("Torrent has no files.");
        if (IsFileComplete(file) && File.Exists(file.FullPath)) return new TorrentMedia(file.Path, file.FullPath, null);

        var key = release.InfoHash.ToLowerInvariant();
        if (!_streams.TryGetValue(key, out var shared))
        {
            await _addLock.WaitAsync(ct);
            try
            {
                if (!_streams.TryGetValue(key, out shared))
                {
                    var inner = await manager.StreamProvider!.CreateStreamAsync(file, prebuffer: true, ct);
                    shared = _streams[key] = new SharedTorrentStream(inner);
                }
            }
            finally
            {
                _addLock.Release();
            }
        }
        return new TorrentMedia(file.Path, null, new TorrentReadStream(shared));
    }

    private void DropStream(string infoHash)
    {
        if (_streams.TryRemove(infoHash.ToLowerInvariant(), out var shared)) shared.Dispose();
    }

    public void Touch(string infoHash) => _lastAccess[infoHash] = DateTime.UtcNow;

    public void Unpin(string infoHash) => _pinned.TryRemove(infoHash, out _);

    public async Task PauseAsync(string infoHash)
    {
        if (Find(infoHash) is { } m) await m.PauseAsync();
    }

    public async Task RemoveAsync(string infoHash, bool deleteData)
    {
        _pinned.TryRemove(infoHash, out _);
        _lastAccess.TryRemove(infoHash, out _);
        DropStream(infoHash);
        if (Find(infoHash) is not { } m) return;
        if (m.State != TorrentState.Stopped) await m.StopAsync(TimeSpan.FromSeconds(5));
        await Engine.RemoveAsync(m, deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);
    }

    /// <summary>
    /// Moves a finished download into the permanent media folder (it may have started life in the
    /// stream cache) and returns the final path of the video file. Optionally keeps seeding.
    /// </summary>
    public async Task<string?> FinalizeDownloadAsync(string infoHash, bool seed)
    {
        if (Find(infoHash) is not { } m || MainVideo(m) is not { } video) return null;
        var target = Path.Combine(_options.MediaDirectory, infoHash.ToLowerInvariant());
        if (!Path.GetFullPath(m.SavePath).Equals(Path.GetFullPath(target), StringComparison.Ordinal))
        {
            DropStream(infoHash);
            await m.StopAsync(TimeSpan.FromSeconds(5));
            await m.MoveFilesAsync(target, true);
        }
        if (seed && m.State == TorrentState.Stopped) await m.StartAsync();
        if (!seed && m.State != TorrentState.Stopped) await m.StopAsync(TimeSpan.FromSeconds(5));
        return video.FullPath;
    }

    public TorrentStats? Stats(string infoHash)
    {
        if (Find(infoHash) is not { } m) return null;
        var video = m.HasMetadata ? MainVideo(m) : null;
        var progress = video is null ? m.PartialProgress : video.BitField.PercentComplete;
        return new TorrentStats(infoHash, m.Name, m.State, progress, m.Monitor.DownloadRate, m.Monitor.UploadRate,
            m.Peers.Available + m.OpenConnections, video?.Length ?? 0, video is not null && IsFileComplete(video));
    }

    /// <summary>Stops and deletes streaming-cache torrents nobody has touched for a while.</summary>
    public async Task EvictIdleAsync(TimeSpan idle)
    {
        if (_engine is null) return;
        var cutoff = DateTime.UtcNow - idle;
        foreach (var m in _engine.Torrents.ToList())
        {
            var hash = m.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
            if (_pinned.ContainsKey(hash)) continue;
            if (_lastAccess.TryGetValue(hash, out var last) && last > cutoff) continue;
            _log.LogInformation("Evicting idle stream cache for {Name}", m.Name);
            await RemoveAsync(hash, deleteData: true);
        }
    }
}
