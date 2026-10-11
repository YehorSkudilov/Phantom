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
/// Which video in a torrent to play: its main (largest) one, as for a movie, or one episode of a show, found in a season
/// pack by its file name.
/// </summary>
public readonly record struct VideoChoice(int? Season, int? Episode)
{
    public static VideoChoice Main => default;
    public bool IsEpisode => Episode is not null;
}

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
    private readonly ConcurrentDictionary<string, DateTime> _addedAt = new();
    private readonly ConcurrentDictionary<string, SharedTorrentStream> _streams = new();

    // The video each torrent is being played from, when it isn't the main one (an episode in a pack).
    private readonly ConcurrentDictionary<string, string> _chosen = new();

    // The episodes the server keeps from each pack it downloads (by lower-case hash): only those are fetched, not the
    // whole season or show.
    private readonly ConcurrentDictionary<string, IReadOnlySet<VideoChoice>> _kept = new();
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

    /// <summary>
    /// Adds (or returns) the torrent for a release and makes sure it is running. With an episode <paramref name="choice"/>
    /// that episode's file is the one downloaded from now on; otherwise it carries on with whichever it was.
    /// </summary>
    public async Task<TorrentManager> EnsureStartedAsync(Release release, bool keep, CancellationToken ct, VideoChoice choice = default)
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
                _addedAt.TryAdd(release.InfoHash.ToLowerInvariant(), DateTime.UtcNow);
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

        var hash = release.InfoHash.ToLowerInvariant();
        if (choice.IsEpisode && Video(manager, choice, release.SeasonNumber) is { } chosen) _chosen[hash] = chosen.Path;
        await PrioritiseAsync(manager, hash, release);
        return manager;
    }

    /// <summary>
    /// The episodes the server keeps from a pack: only their files are downloaded (besides one being played), so saving
    /// one episode, or one season of a whole-show pack, doesn't fetch the rest.
    /// </summary>
    public async Task KeepEpisodesAsync(Release release, IEnumerable<VideoChoice> episodes)
    {
        var hash = release.InfoHash.ToLowerInvariant();
        var wanted = episodes.Where(e => e.IsEpisode).ToHashSet();
        if (_kept.TryGetValue(hash, out var before) && before.SetEquals(wanted)) return;
        _kept[hash] = wanted;
        if (Find(hash) is not { HasMetadata: true } manager) return;
        // An episode no longer wanted (paused or removed) stops downloading, unless someone is watching it.
        if (_chosen.TryGetValue(hash, out var chosen) && !(_streams.TryGetValue(hash, out var stream) && stream.FilePath == chosen)
            && !wanted.Any(e => Video(manager, e, release.SeasonNumber)?.Path == chosen))
            _chosen.TryRemove(hash, out _);
        await PrioritiseAsync(manager, hash, release);
    }

    private Task PrioritiseAsync(TorrentManager manager, string hash, Release release)
    {
        // A pack nobody has picked an episode of yet has no video of its own to fetch (its largest is just one episode).
        var playing = _chosen.ContainsKey(hash) || !IsPack(manager) ? CurrentVideo(manager, hash) : null;
        var kept = _pinned.ContainsKey(release.InfoHash) && _kept.TryGetValue(hash, out var episodes)
            ? episodes.Select(e => Video(manager, e, release.SeasonNumber)).OfType<ITorrentManagerFile>().ToHashSet()
            : [];
        return PrioritiseAsync(manager, playing, kept);
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

    /// <summary>
    /// Only download the video being played and subtitles; skip samples, artwork, extras. The episodes of a pack the
    /// server keeps (<paramref name="kept"/>) are fetched too, after the one being played; its other episodes aren't.
    /// </summary>
    private static async Task PrioritiseAsync(TorrentManager manager, ITorrentManagerFile? video, IReadOnlySet<ITorrentManagerFile> kept)
    {
        foreach (var file in manager.Files)
        {
            var wanted = file == video ? Priority.Normal
                : VideoFiles.IsSubtitle(file.Path) ? Priority.Highest
                : kept.Contains(file) ? Priority.Low
                : Priority.DoNotDownload;
            if (file.Priority != wanted) await manager.SetFilePriorityAsync(file, wanted);
        }
    }

    public static ITorrentManagerFile? MainVideo(TorrentManager manager) =>
        manager.Files.Where(f => VideoFiles.IsVideo(f.Path)).OrderByDescending(f => f.Length).FirstOrDefault()
        ?? manager.Files.OrderByDescending(f => f.Length).FirstOrDefault();

    /// <summary>The video for <paramref name="choice"/>: the main one, or the episode's file (null if the torrent hasn't got it).</summary>
    public static ITorrentManagerFile? Video(TorrentManager manager, VideoChoice choice, int? packSeason)
    {
        if (!choice.IsEpisode) return MainVideo(manager);
        var videos = manager.Files.Where(f => VideoFiles.IsVideo(f.Path)).ToList();
        // The largest match, so a sample of the same episode loses to the real thing.
        return videos.Where(f => ReleaseNameParser.EpisodeOfFile(f.Path, packSeason) == (choice.Season, choice.Episode))
                   .OrderByDescending(f => f.Length).FirstOrDefault()
               // A single episode's torrent whose file is named some other way.
               ?? (IsPack(manager) ? null : MainVideo(manager));
    }

    /// <summary>A torrent of several episodes, rather than one movie or episode (with perhaps a sample).</summary>
    public static bool IsPack(TorrentManager manager) =>
        manager.Files.Where(f => VideoFiles.IsVideo(f.Path)).Select(f => ReleaseNameParser.EpisodeOfFile(f.Path, null).Episode)
            .OfType<int>().Distinct().Skip(1).Any();

    private ITorrentManagerFile? CurrentVideo(TorrentManager manager, string hash) =>
        _chosen.TryGetValue(hash, out var path) && manager.Files.FirstOrDefault(f => f.Path == path) is { } chosen ? chosen : MainVideo(manager);

    /// <summary>
    /// Subtitle files for <paramref name="video"/>: all of them for a movie, and for an episode in a pack the ones named
    /// for that episode (or after its file).
    /// </summary>
    public static IReadOnlyList<ITorrentManagerFile> Subtitles(TorrentManager manager, ITorrentManagerFile? video = null)
    {
        var all = manager.Files.Where(f => VideoFiles.IsSubtitle(f.Path)).OrderBy(f => f.Path).ToList();
        if (video is null || !IsPack(manager)) return all;
        var episode = ReleaseNameParser.EpisodeOfFile(video.Path, null);
        var stem = Path.GetFileNameWithoutExtension(video.Path);
        return all.Where(s => Path.GetFileName(s.Path).StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                              || episode.Episode is not null && ReleaseNameParser.EpisodeOfFile(s.Path, episode.Season) == episode).ToList();
    }

    public static bool IsFileComplete(ITorrentManagerFile file) => file.BitField.AllTrue;

    /// <summary>
    /// Opens the video for <paramref name="choice"/> for playback. A finished file is returned as a path so it can be
    /// served straight from disk; otherwise a seekable stream whose reads wait for the needed pieces. Null when the
    /// torrent hasn't got that episode.
    /// </summary>
    /// <remarks>
    /// A torrent can only stream one file at a time, so opening another episode of a pack ends the stream of the one
    /// before (someone else watching it from the same pack is cut off).
    /// </remarks>
    public async Task<TorrentMedia?> OpenAsync(Release release, CancellationToken ct, VideoChoice choice = default)
    {
        Interlocked.Exchange(ref _lastStreamTicks, DateTime.UtcNow.Ticks);
        var manager = await EnsureStartedAsync(release, keep: _pinned.ContainsKey(release.InfoHash), ct, choice);
        var file = Video(manager, choice, release.SeasonNumber);
        if (file is null) return null;
        if (IsFileComplete(file) && File.Exists(file.FullPath)) return new TorrentMedia(file.Path, file.FullPath, null);

        var key = release.InfoHash.ToLowerInvariant();
        if (!_streams.TryGetValue(key, out var shared) || shared.FilePath != file.Path)
        {
            await _addLock.WaitAsync(ct);
            try
            {
                if (!_streams.TryGetValue(key, out shared) || shared.FilePath != file.Path)
                {
                    DropStream(key);
                    var inner = await manager.StreamProvider!.CreateStreamAsync(file, prebuffer: true, ct);
                    shared = _streams[key] = new SharedTorrentStream(inner, file.Path, () => Touch(key));
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

    // When a player last opened a torrent stream (server downloads Touch torrents too, so that can't tell).
    private long _lastStreamTicks;

    /// <summary>A player has opened a torrent stream within <paramref name="window"/>.</summary>
    public bool StreamedWithin(TimeSpan window) => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastStreamTicks) < window.Ticks;

    public void Unpin(string infoHash) => _pinned.TryRemove(infoHash, out _);

    /// <summary>
    /// Stops streamed torrents nobody has read from for <paramref name="idle"/>: without this a torrent opened to watch
    /// went on to download the whole file and then seed it, until its cache expired a day later. What's downloaded stays,
    /// and playing it again starts it back up (see EnsureStartedAsync). Torrents the server keeps carry on.
    /// </summary>
    public async Task StopIdleStreamsAsync(TimeSpan idle)
    {
        if (_engine is null) return;
        var cutoff = DateTime.UtcNow - idle;
        foreach (var m in _engine.Torrents.ToList())
        {
            var hash = m.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
            if (_pinned.ContainsKey(hash) || m.State is TorrentState.Stopped or TorrentState.Stopping or TorrentState.Paused) continue;
            if (_lastAccess.TryGetValue(hash, out var last) && last > cutoff) continue;
            _log.LogInformation("Nobody's watching {Name}: stopping its download", m.Name);
            DropStream(hash);
            try
            {
                await m.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TorrentException)
            {
                _log.LogDebug(ex, "Couldn't stop {Name}", m.Name);
            }
        }
    }

    public async Task PauseAsync(string infoHash)
    {
        if (Find(infoHash) is { } m) await m.PauseAsync();
    }

    public async Task RemoveAsync(string infoHash, bool deleteData)
    {
        _addedAt.TryRemove(infoHash.ToLowerInvariant(), out _);
        _pinned.TryRemove(infoHash, out _);
        _lastAccess.TryRemove(infoHash, out _);
        _chosen.TryRemove(infoHash.ToLowerInvariant(), out _);
        _kept.TryRemove(infoHash.ToLowerInvariant(), out _);
        DropStream(infoHash);
        if (Find(infoHash) is not { } m) return;
        if (m.State != TorrentState.Stopped) await m.StopAsync(TimeSpan.FromSeconds(5));
        await Engine.RemoveAsync(m, deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);
    }

    /// <summary>
    /// Moves a finished download into the permanent media folder (it may have started life in the
    /// stream cache) and returns the final path of its video (<paramref name="choice"/>'s, for an episode).
    /// Optionally keeps seeding; with <paramref name="moreToCome"/> (other episodes of the pack still downloading)
    /// it carries on regardless.
    /// </summary>
    public async Task<string?> FinalizeDownloadAsync(Release release, bool seed, VideoChoice choice = default, bool moreToCome = false)
    {
        var infoHash = release.InfoHash;
        if (Find(infoHash) is not { } m || Video(m, choice, release.SeasonNumber) is not { } video) return null;
        var target = Path.Combine(_options.MediaDirectory, infoHash.ToLowerInvariant());
        if (!Path.GetFullPath(m.SavePath).Equals(Path.GetFullPath(target), StringComparison.Ordinal))
        {
            DropStream(infoHash);
            await m.StopAsync(TimeSpan.FromSeconds(5));
            await m.MoveFilesAsync(target, true);
        }
        if ((seed || moreToCome) && m.State == TorrentState.Stopped) await m.StartAsync();
        if (!seed && !moreToCome && m.State != TorrentState.Stopped) await m.StopAsync(TimeSpan.FromSeconds(5));
        if (IsPack(m)) await RecordCompleteFilesAsync(m, target);
        return video.FullPath;
    }

    /// <summary>Lists, in a pack's folder, which of its videos are fully downloaded (the others may be partly there).</summary>
    private const string CompleteList = ".phantom-complete";

    private static Task RecordCompleteFilesAsync(TorrentManager m, string folder) =>
        File.WriteAllLinesAsync(Path.Combine(folder, CompleteList),
            m.Files.Where(f => VideoFiles.IsVideo(f.Path) && IsFileComplete(f)).Select(f => f.Path.Replace('\\', '/')));

    /// <summary>
    /// Whether a saved pack's video at <paramref name="relativePath"/> is fully downloaded: the torrent says so when it's
    /// loaded; otherwise the list written as its episodes finished. Packs saved whole, before episodes could be picked
    /// out, haven't got a list, and all their files are complete.
    /// </summary>
    private bool IsSavedFileComplete(string infoHash, string root, string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/');
        if (Find(infoHash) is { HasMetadata: true } m)
        {
            var file = m.Files.FirstOrDefault(f => f.Path.Replace('\\', '/') == relativePath
                                                   || Path.GetFullPath(f.FullPath) == Path.GetFullPath(Path.Combine(root, relativePath)));
            if (file is not null) return IsFileComplete(file);
        }
        var list = Path.Combine(MediaFolder(infoHash), CompleteList);
        if (!File.Exists(list)) return true;
        var done = File.ReadAllLines(list);
        return done.Any(p => p == relativePath || relativePath.EndsWith("/" + p, StringComparison.Ordinal) || p.EndsWith("/" + relativePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// Progress of the video being played, or with an episode <paramref name="choice"/> of that episode's file (a server
    /// download of one episode from a pack is complete once its own file is in).
    /// </summary>
    public TorrentStats? Stats(string infoHash, VideoChoice choice = default, int? packSeason = null)
    {
        if (Find(infoHash) is not { } m) return null;
        var peers = m.Peers.Available + m.OpenConnections;
        var video = !m.HasMetadata ? null
            : choice.IsEpisode ? Video(m, choice, packSeason)
            : CurrentVideo(m, infoHash.ToLowerInvariant());
        var progress = video is null ? m.PartialProgress : video.BitField.PercentComplete;
        return new TorrentStats(infoHash, m.Name, m.State, progress, m.Monitor.DownloadRate, m.Monitor.UploadRate,
            peers, video?.Length ?? 0, video is not null && IsFileComplete(video));
    }

    /// <summary>Where a release the server keeps has its files.</summary>
    public string MediaFolder(string infoHash) => Path.Combine(_options.MediaDirectory, infoHash.ToLowerInvariant());

    /// <summary>
    /// The file to play for <paramref name="choice"/> from a release the server has finished downloading, or null when
    /// it hasn't (or the pack hasn't got that episode).
    /// </summary>
    public string? LocalVideo(Release release, VideoChoice choice)
    {
        if (release.LocalFilePath is not { } main || !File.Exists(main)) return null;
        if (!choice.IsEpisode) return main;
        var root = MediaFolder(release.InfoHash);
        if (!Directory.Exists(root)) root = Path.GetDirectoryName(main)!;
        var videos = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(VideoFiles.IsVideo)
            .Select(f => (Path: f, Episode: ReleaseNameParser.EpisodeOfFile(Path.GetRelativePath(root, f), release.SeasonNumber)))
            .ToList();
        var match = videos.Where(v => v.Episode == (choice.Season, choice.Episode)).OrderByDescending(v => new FileInfo(v.Path).Length)
            .Select(v => v.Path).FirstOrDefault();
        // Only some of a pack's episodes may have been saved: another one's file can be there, partly downloaded.
        if (match is not null && !IsSavedFileComplete(release.InfoHash, root, Path.GetRelativePath(root, match))) return null;
        // A single episode's release whose file is named some other way.
        return match ?? (videos.Select(v => v.Episode.Episode).OfType<int>().Distinct().Skip(1).Any() ? null : main);
    }

    /// <summary>Deletes one episode's file from a saved pack (the torrent should no longer want it: see KeepEpisodesAsync).</summary>
    public void DeleteSavedEpisode(Release release, VideoChoice choice)
    {
        var root = MediaFolder(release.InfoHash);
        var path = Find(release.InfoHash) is { HasMetadata: true } m && Video(m, choice, release.SeasonNumber) is { } file ? file.FullPath
            : Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(VideoFiles.IsVideo)
                    .Where(f => ReleaseNameParser.EpisodeOfFile(Path.GetRelativePath(root, f), release.SeasonNumber) == (choice.Season, choice.Episode))
                    .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                : null;
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Couldn't delete {Path}", path);
        }
    }

    /// <summary>How long the torrent has been downloading in this run of the server.</summary>
    public TimeSpan Age(string infoHash) =>
        _addedAt.TryGetValue(infoHash.ToLowerInvariant(), out var at) ? DateTime.UtcNow - at : TimeSpan.Zero;

    /// <summary>
    /// How much of the video being played is downloaded unbroken from <paramref name="fraction"/> of the way in (where
    /// playback will start), and the video's size. Null until the torrent's metadata is in.
    /// </summary>
    public (long Buffered, long Length)? BufferedFrom(string infoHash, double fraction)
    {
        if (Find(infoHash) is not { HasMetadata: true } manager || CurrentVideo(manager, infoHash.ToLowerInvariant()) is not { } file
            || manager.Torrent is not { } torrent) return null;
        var pieceLength = (long)torrent.PieceLength;
        var offset = (long)(file.Length * Math.Clamp(fraction, 0, 1));
        var first = (int)((file.OffsetInTorrent + offset) / pieceLength) - file.StartPieceIndex;
        var pieces = file.BitField;
        var count = 0;
        for (var i = Math.Max(0, first); i < pieces.Length && pieces[i]; i++) count++;
        return (Math.Clamp(count * pieceLength, 0, file.Length - offset), file.Length);
    }

    /// <summary>Free space on the disk holding the stream cache.</summary>
    public long FreeDiskBytes => new DriveInfo(Path.GetFullPath(_options.CacheDirectory)).AvailableFreeSpace;

    /// <summary>
    /// When the disk runs low, clears stream caches (never server downloads) that aren't playing right now, least
    /// recently watched first, plus the seek-preview thumbnails, until there's <see cref="PhantomOptions.MinFreeDiskGb"/>
    /// free again. A full disk otherwise stops every stream and download, and the database's writes.
    /// </summary>
    public async Task EnsureFreeSpaceAsync()
    {
        if (_engine is null) return;
        var wanted = (long)_options.MinFreeDiskGb << 30;
        if (FreeDiskBytes >= wanted) return;

        var thumbnails = Path.Combine(_options.CacheDirectory, "thumbnails");
        if (Directory.Exists(thumbnails)) Directory.Delete(thumbnails, recursive: true);

        var playing = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        var candidates = _engine.Torrents
            .Select(m => (Manager: m, Hash: m.InfoHashes.V1OrV2.ToHex().ToLowerInvariant()))
            .Where(t => !_pinned.ContainsKey(t.Hash))
            .Select(t => (t.Hash, t.Manager.Name, Last: _lastAccess.TryGetValue(t.Hash, out var last) ? last : DateTime.MinValue))
            .Where(t => t.Last < playing)
            .OrderBy(t => t.Last)
            .ToList();
        foreach (var (hash, name, _) in candidates)
        {
            if (FreeDiskBytes >= wanted) break;
            _log.LogWarning("Low disk space: clearing the stream cache of {Name}", name);
            await RemoveAsync(hash, deleteData: true);
        }
        if (FreeDiskBytes < wanted)
            _log.LogWarning("Disk space still low ({Free} MB free) after clearing idle stream caches", FreeDiskBytes >> 20);
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

    // ---------- Storage ----------

    /// <summary>A torrent's folder on disk: a stream cache, or a copy kept in the media folder.</summary>
    public record StoredTorrent(string InfoHash, string? Name, bool Saved, long SizeBytes, DateTime? LastUsed, bool InUse);

    /// <summary>A stream that was read from within this long counts as in use: it isn't offered for clearing.</summary>
    private static readonly TimeSpan InUseWindow = TimeSpan.FromMinutes(5);

    /// <summary>Every torrent folder in the stream cache and the media folder, with its size on disk.</summary>
    public IReadOnlyList<StoredTorrent> StoredTorrents()
    {
        var list = new List<StoredTorrent>();
        foreach (var (root, saved) in new[] { (_options.CacheDirectory, false), (_options.MediaDirectory, true) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var hash = Path.GetFileName(dir).ToLowerInvariant();
                if (hash.Length != 40) continue; // "thumbnails" and the like
                DateTime? last = _lastAccess.TryGetValue(hash, out var touched) ? touched : null;
                // A streamed file is made at its full length when the end is read (MP4s keep their index there), so its size
                // on disk says little; for a loaded torrent what's actually downloaded is counted instead.
                var manager = Find(hash);
                var size = manager is { HasMetadata: true }
                    ? manager.Files.Sum(f => (long)(f.Length * f.BitField.PercentComplete / 100))
                    : DirectorySize(dir);
                list.Add(new StoredTorrent(hash, manager?.Name, saved, size, last ?? Directory.GetLastWriteTimeUtc(dir),
                    last is { } t && DateTime.UtcNow - t < InUseWindow));
            }
        }
        return list;
    }

    /// <summary>Bytes in a folder and everything under it (0 when it's gone or can't be read).</summary>
    public static long DirectorySize(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Clears one torrent's stream cache. False when it's being watched or kept on the server (saved copies are removed
    /// with their download).
    /// </summary>
    public async Task<bool> ClearStreamCacheAsync(string infoHash)
    {
        var hash = infoHash.ToLowerInvariant();
        if (_pinned.ContainsKey(hash) || _lastAccess.TryGetValue(hash, out var last) && DateTime.UtcNow - last < InUseWindow) return false;
        await RemoveAsync(hash, deleteData: true);
        // After a restart the folder can be there without the torrent being loaded.
        var dir = Path.Combine(_options.CacheDirectory, hash);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return true;
    }

    /// <summary>Clears every stream cache nobody is watching from. Returns how many were cleared.</summary>
    public async Task<int> ClearIdleStreamCachesAsync()
    {
        var cleared = 0;
        foreach (var item in StoredTorrents().Where(t => !t.Saved && !t.InUse))
            if (await ClearStreamCacheAsync(item.InfoHash)) cleared++;
        return cleared;
    }

    /// <summary>Keeps the stream cache under <paramref name="maxBytes"/>, clearing the least recently watched first.</summary>
    public async Task EnforceCacheLimitAsync(long maxBytes)
    {
        if (maxBytes <= 0) return;
        var caches = StoredTorrents().Where(t => !t.Saved).ToList();
        var total = caches.Sum(c => c.SizeBytes);
        foreach (var cache in caches.Where(c => !c.InUse).OrderBy(c => c.LastUsed))
        {
            if (total <= maxBytes) break;
            _log.LogInformation("Stream cache over its {Limit} GB limit: clearing {Name}", maxBytes >> 30, cache.Name ?? cache.InfoHash);
            if (await ClearStreamCacheAsync(cache.InfoHash)) total -= cache.SizeBytes;
        }
    }
}
