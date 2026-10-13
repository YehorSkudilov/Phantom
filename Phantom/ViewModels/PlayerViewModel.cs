using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

/// <summary>
/// What a cast device is sent: <paramref name="Url"/> from <paramref name="StartAt"/> into it. A conversion starts at the
/// position asked for, so the TV's own position is <paramref name="Offset"/> behind the movie's.
/// </summary>
public sealed record CastMedia(string Url, string ContentType, TimeSpan Offset, TimeSpan StartAt, bool Converted);

public record SubtitleChoice(string Label, SubtitleTrackDto? Track)
{
    public override string ToString() => Label;
}

/// <summary>
/// Player state: source selection (direct / transcoded / offline file), resume, subtitles overlay,
/// timeline reporting and the up-next queue. Plays a movie (<c>id=</c>) or a show's episode (<c>episode=</c>), whose
/// up next is the following episode. The page wires MediaElement events into it.
/// </summary>
public partial class PlayerViewModel(PhantomApi api, SettingsService settings, DownloadManager downloads) : BaseViewModel, IQueryAttributable
{
    private IReadOnlyList<SubtitleCue> _cues = [];
    private DateTime _lastReport = DateTime.MinValue;
    private bool _offline;
    private bool _resume;
    private int? _releaseId;
    private List<int> _queue = [];
    private TimeSpan _runtime;
    private EpisodeDto? _nextEpisode;
    private OfflineDownload? _nextOffline; // the show's next downloaded episode, when playing one offline

    // Direct play failed on this device, so the server converts it whatever the quality setting says.
    private bool _forceConvert;
    // Converting by copying the picture and sound into another file type (see ConversionUrl), and whether that
    // already failed to play here, so it's converted in full instead.
    private bool _remuxing, _remuxFailed;

    public ObservableCollection<SubtitleChoice> SubtitleChoices { get; } = [];
    public IReadOnlyList<QualityOption> Qualities => QualityOption.All;
    public IReadOnlyList<double> Speeds { get; } = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    [ObservableProperty] private int movieId;

    /// <summary>The episode playing, when it's a show's (MovieId is 0 then).</summary>
    [ObservableProperty] private int? episodeId;
    [ObservableProperty] private string? sourceUrl;
    [ObservableProperty] private string? posterUrl;
    [ObservableProperty] private string? subtitleText;
    [ObservableProperty] private SubtitleChoice? selectedSubtitle;
    [ObservableProperty] private QualityOption selectedQuality = settings.StreamingQuality;
    [ObservableProperty] private double speed = 1.0;
    [ObservableProperty] private double subtitleSize = settings.SubtitleSize;
    [ObservableProperty] private string? upNextTitle;
    [ObservableProperty] private bool isTranscoding;

    /// <summary>The quality in play, for the player's quality button ("Auto · 720p", "Original"...).</summary>
    [ObservableProperty] private string qualityLabel = "";

    private static string ShortName(QualityOption q) => q.IsOriginal ? "Original" : q.Name.Split(' ')[0];

    // The loading screen: what the server is doing, and how far along (0-1, or -1 while there's no figure yet).
    [ObservableProperty] private string loadingText = "Opening…";
    [ObservableProperty] private string? loadingDetail;
    [ObservableProperty] private double loadingProgress = -1;
    public bool HasLoadingProgress => LoadingProgress >= 0;
    partial void OnLoadingProgressChanged(double value) => OnPropertyChanged(nameof(HasLoadingProgress));

    private CancellationTokenSource? _statusPoll;

    /// <summary>Where playback will start (or carry on) from, so the loading estimate is for that point.</summary>
    public TimeSpan StatusPosition { get; set; }

    /// <summary>The download is slower than the movie plays: the loading screen offers saving it on the server.</summary>
    [ObservableProperty] private bool isTooSlow;
    [ObservableProperty] private string? tooSlowText;

    /// <summary>Has the server download the whole movie, to watch from Downloads → On the server when it's ready.</summary>
    [RelayCommand]
    private Task SaveOnServerInstead() => RunAsync(async () =>
    {
        await api.QueueServerDownloadAsync(MovieId, _releaseId, EpisodeId);
        StopLoadingStatus();
        await ToastAsync("Saving it on the server. Watch it from Downloads → On the server when it's ready.");
        await Shell.Current.GoToAsync("..");
    }, showBusy: false);

    /// <summary>Keeps the loading screen up to date from the server while the movie opens or buffers.</summary>
    public void StartLoadingStatus()
    {
        if (_offline || _statusPoll is not null) return;
        _statusPoll = new CancellationTokenSource();
        _ = PollStatusAsync(_statusPoll.Token);
    }

    public void StopLoadingStatus()
    {
        _statusPoll?.Cancel();
        _statusPoll = null;
        IsTooSlow = false;
    }

    private async Task PollStatusAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await api.StreamStatusAsync(MovieId, _releaseId, StatusPosition, ct, EpisodeId);
                if (ct.IsCancellationRequested) return;
                LoadingText = status.Ready ? "Buffering…" : status.Message;
                // The bar is the wait until playback starts, not the whole movie.
                LoadingProgress = status.Ready ? -1 : status.BufferProgress ?? -1;
                LoadingDetail = status.Ready ? null
                    : status.BufferProgress >= 1 ? "Starting…"
                    : status.SecondsToStart is { } s ? $"Ready to play in about {FormatWait(s)}"
                    : null;
                IsTooSlow = !status.Ready && status.TooSlow;
                TooSlowText = IsTooSlow
                    ? $"Downloading at {BytesConverter.Format(status.DownloadRate)}/s, but this {(EpisodeId is null ? "movie" : "episode")} needs about {BytesConverter.Format(status.NeededRate)}/s to play without pausing."
                    : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiException)
            {
                // The loading screen keeps its last message; playback errors are reported separately.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1.5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// How far into the video (0-1) it's there to play, for the second shade on the timeline: all of it for a file the
    /// server (or this device) has whole, otherwise as far as the torrent has downloaded unbroken from where it's playing.
    /// </summary>
    [ObservableProperty] private double bufferedTo;

    private CancellationTokenSource? _bufferWatch;
    private TimeSpan _playedTo;

    /// <summary>Keeps <see cref="BufferedTo"/> up to date while playing; stops by itself once the server has it all.</summary>
    public void StartBufferWatch()
    {
        _bufferWatch?.Cancel();
        if (_offline)
        {
            BufferedTo = 1;
            return;
        }
        BufferedTo = 0;
        var cts = _bufferWatch = new CancellationTokenSource();
        _ = WatchBufferAsync(cts.Token);
    }

    public void StopBufferWatch()
    {
        _bufferWatch?.Cancel();
        _bufferWatch = null;
    }

    private async Task WatchBufferAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await api.StreamStatusAsync(MovieId, _releaseId, _playedTo, ct, EpisodeId);
                if (ct.IsCancellationRequested) return;
                if (status.AvailableTo is { } available) BufferedTo = available;
                if (status.Ready) return; // the whole file is on the server: nothing more to watch
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiException)
            {
                // The shade keeps its last figure.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static string FormatWait(int seconds) => seconds switch
    {
        < 60 => $"{Math.Max(1, seconds)} s",
        < 3600 => $"{(seconds + 30) / 60} min",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60} min",
    };

    // ---------- Seek previews ----------
    // Two sizes, both kept: small ones (the box above the bar, the skip preview), which the server makes for the whole
    // film and sends in one go, and big ones for the picture over the whole screen while moving along the timeline,
    // fetched one at a time where it rests. Both are kept in memory while playing and on disk afterwards, so the next
    // time they show at once. A download playing offline takes its own from the file (see LocalPreviews).

    public const int SmallPreviewWidth = 320;
    public const int LargePreviewWidth = 960;

    private readonly Dictionary<int, byte[]> _previewFrames = [];
    private readonly Dictionary<int, byte[]> _largeFrames = [];
    private CancellationTokenSource? _previewFramesLoad;
    private string? _previewFolder;
    private bool _previewsComplete;
    private int _savedPreviewCount;

    public static int PreviewStep(TimeSpan at) => (int)Math.Max(0, at.TotalSeconds) / 10 * 10;

    /// <summary>Whether a preview not here yet can be fetched (from the server, or from the file offline).</summary>
    public bool CanFetchPreview => _offline ? LocalPreviews.IsSupported && SourceUrl is not null : Stream?.ThumbnailUrl is not null;

    /// <summary>
    /// The small preview for <paramref name="at"/> if it's here (<paramref name="exact"/>), else the nearest one within a
    /// minute to show meanwhile, else null.
    /// </summary>
    public byte[]? PreviewFrame(TimeSpan at, out bool exact)
    {
        var step = PreviewStep(at);
        exact = _previewFrames.TryGetValue(step, out var frame);
        if (exact) return frame;
        var nearest = -1;
        foreach (var key in _previewFrames.Keys)
            if (Math.Abs(key - step) <= 60 && (nearest < 0 || Math.Abs(key - step) < Math.Abs(nearest - step))) nearest = key;
        return nearest >= 0 ? _previewFrames[nearest] : null;
    }

    /// <summary>The big preview for <paramref name="at"/>, if it has been fetched before (now or on an earlier play).</summary>
    public byte[]? LargePreviewFrame(TimeSpan at)
    {
        var step = PreviewStep(at);
        if (_largeFrames.TryGetValue(step, out var frame)) return frame;
        if (_previewFolder is null) return null;
        try
        {
            var file = Path.Combine(_previewFolder, $"L{step}.jpg");
            if (!File.Exists(file)) return null;
            return _largeFrames[step] = File.ReadAllBytes(file);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Fetches the preview at <paramref name="at"/> (small or big) and keeps it. Null when there's none.</summary>
    public async Task<byte[]?> FetchPreviewFrameAsync(TimeSpan at, bool large, CancellationToken ct)
    {
        var step = PreviewStep(at);
        var width = large ? LargePreviewWidth : SmallPreviewWidth;
        byte[]? jpeg;
        if (_offline) jpeg = SourceUrl is { } file && LocalPreviews.IsSupported ? await LocalPreviews.FrameAsync(file, step, width, ct) : null;
        else if (Stream?.ThumbnailUrl is { } url) jpeg = await ThumbnailAsync($"{url}&t={step}" + (large ? $"&w={width}" : ""), ct);
        else return null;
        // A miss may be a timeout, so it's asked again next time.
        if (jpeg is null) return null;
        if (!large) _previewFrames[step] = jpeg;
        else
        {
            _largeFrames[step] = jpeg;
            if (_previewFolder is { } folder)
                _ = Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                        File.WriteAllBytes(Path.Combine(folder, $"L{step}.jpg"), jpeg);
                    }
                    catch (IOException)
                    {
                    }
                });
        }
        return jpeg;
    }

    /// <summary>
    /// Shows what was kept from earlier plays, then downloads every small preview the server has, again every 30
    /// seconds until it has made them all (it does once the file is complete on the server).
    /// </summary>
    private void StartPreviewFrames(int? releaseId)
    {
        StopPreviewFrames();
        _previewFrames.Clear();
        _largeFrames.Clear();
        _previewsComplete = false;
        var root = Path.Combine(FileSystem.CacheDirectory, "previews");
        _previewFolder = Path.Combine(root, $"m{MovieId}-e{EpisodeId}-r{releaseId}");
        var cts = _previewFramesLoad = new CancellationTokenSource();
        _ = LoadPreviewFramesAsync(_offline ? null : Stream?.ThumbnailPackUrl, _previewFolder, root, cts.Token);
    }

    /// <summary>Stops fetching, and keeps the small previews on disk for next time.</summary>
    public void StopPreviewFrames()
    {
        _previewFramesLoad?.Cancel();
        _previewFramesLoad = null;
        SavePreviewPack();
        if (_offline) LocalPreviews.Close();
    }

    private async Task LoadPreviewFramesAsync(string? url, string folder, string root, CancellationToken ct)
    {
        try
        {
            // Read off the UI thread: a film's previews are a few MB.
            var (kept, complete) = await Task.Run(() => ReadPreviewPack(folder, root), ct);
            if (ct.IsCancellationRequested) return;
            foreach (var (seconds, jpeg) in kept) _previewFrames.TryAdd(seconds, jpeg);
            _savedPreviewCount = _previewFrames.Count;
            _previewsComplete = complete;
            if (complete || url is null) return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var (frames, complete) = await api.ThumbnailPackAsync(url, ct);
                foreach (var (seconds, jpeg) in frames) _previewFrames[seconds] = jpeg;
                if (complete)
                {
                    _previewsComplete = true;
                    SavePreviewPack();
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private const string PackFile = "small.bin";

    /// <summary>
    /// The small previews kept for this video: <c>[complete][count]</c> then per frame <c>[seconds][length][JPEG]</c>.
    /// Also clears out the previews of videos not played for a month, keeping at most 40.
    /// </summary>
    private static (Dictionary<int, byte[]> Frames, bool Complete) ReadPreviewPack(string folder, string root)
    {
        var frames = new Dictionary<int, byte[]>();
        var complete = false;
        try
        {
            if (Directory.Exists(root))
            {
                var old = new DirectoryInfo(root).GetDirectories().Where(d => d.FullName != Path.GetFullPath(folder))
                    .OrderByDescending(d => d.LastWriteTimeUtc).ToList();
                foreach (var d in old.Skip(40).Concat(old.Take(40).Where(d => d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))))
                    d.Delete(recursive: true);
            }
            if (Directory.Exists(folder)) Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
            var file = Path.Combine(folder, PackFile);
            if (!File.Exists(file)) return (frames, false);
            using var reader = new BinaryReader(File.OpenRead(file));
            complete = reader.ReadBoolean();
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var seconds = reader.ReadInt32();
                frames[seconds] = reader.ReadBytes(reader.ReadInt32());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A pack cut short: what came is kept, the rest is fetched again.
        }
        return (frames, complete && frames.Count > 0);
    }

    private void SavePreviewPack()
    {
        if (_previewFolder is not { } folder || _previewFrames.Count == 0 || _previewFrames.Count == _savedPreviewCount && !_previewsComplete) return;
        var frames = _previewFrames.ToList();
        var complete = _previewsComplete && !_offline;
        _savedPreviewCount = frames.Count;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, PackFile);
                using (var writer = new BinaryWriter(File.Create(file + ".tmp")))
                {
                    writer.Write(complete);
                    writer.Write(frames.Count);
                    foreach (var (seconds, jpeg) in frames)
                    {
                        writer.Write(seconds);
                        writer.Write(jpeg.Length);
                        writer.Write(jpeg);
                    }
                }
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Couldn't keep previews: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Fetches the preview frame. Cancelling it (the drag moved on) closes the request, which stops the server's ffmpeg
    /// so it no longer pulls that part of the torrent ahead of playback. Null when there's no frame.
    /// </summary>
    public async Task<byte[]?> ThumbnailAsync(string url, CancellationToken ct)
    {
        try
        {
            return await api.ThumbnailAsync(url, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>When transcoding, the stream restarts at this offset on every seek.</summary>
    public TimeSpan TranscodeOffset { get; private set; }

    public TimeSpan ResumePosition { get; private set; }
    public StreamInfoDto? Stream { get; private set; }

    public double Volume
    {
        get => settings.PlayerVolume;
        set => settings.PlayerVolume = value;
    }

    public double? Brightness
    {
        get => settings.PlayerBrightness;
        set => settings.PlayerBrightness = value;
    }

    public int SkipForward => settings.SkipForwardSeconds;
    public int SkipBack => settings.SkipBackSeconds;

    public event EventHandler? SourceChanged;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        EpisodeId = query.TryGetValue("episode", out var ep) && int.TryParse(ep.ToString(), out var eid) ? eid : null;
        MovieId = EpisodeId is null ? int.Parse(query["id"].ToString()!) : 0;
        _resume = query.TryGetValue("resume", out var r) && bool.TryParse(r.ToString(), out var b) && b;
        _offline = query.TryGetValue("offline", out var o) && o.ToString() == "true";
        _releaseId = query.TryGetValue("releaseId", out var rel) && int.TryParse(rel.ToString(), out var rid) ? rid : null;
        _queue = query.TryGetValue("queue", out var q)
            ? q.ToString()!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList()
            : [];
        LoadCommand.Execute(null);
    }

    [RelayCommand]
    private Task Load() => RunAsync(async () =>
    {
        _forceConvert = _remuxFailed = false;
        LoadingText = "Opening…";
        LoadingDetail = null;
        LoadingProgress = -1;
        TranscodeOffset = TimeSpan.Zero;
        SubtitleText = null;
        _cues = [];
        SubtitleChoices.Clear();
        SubtitleChoices.Add(new SubtitleChoice("Off", null));

        var offline = EpisodeId is { } downloaded ? downloads.FindEpisode(downloaded) : downloads.Find(MovieId);
        _nextOffline = null;
        if (_offline && offline is { IsComplete: true })
        {
            Title = offline.IsEpisode ? $"{offline.Title} · {offline.Subtitle}" : offline.Title;
            _nextEpisode = null;
            if (offline.IsEpisode) _nextOffline = downloads.NextDownloadedEpisode(offline);
            PosterUrl = offline.PosterSource;
            ResumePosition = _resume ? TimeSpan.FromMilliseconds(offline.OfflinePositionMs) : TimeSpan.Zero;
            Stream = null;
            IsTranscoding = false;
            SourceUrl = offline.FilePath;
            StartPreviewFrames(offline.ReleaseId);
            SourceChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        StartLoadingStatus(); // the stream info below waits until the torrent is found, which can take a while
        if (EpisodeId is { } episodeId)
        {
            var detail = await api.EpisodeAsync(episodeId);
            var e = detail.Episode;
            Title = $"{detail.ShowTitle} · S{e.SeasonNumber}:E{e.EpisodeNumber}" + (string.IsNullOrWhiteSpace(e.Title) ? "" : $" {e.Title}");
            _runtime = TimeSpan.FromMinutes(e.RuntimeMinutes ?? 0);
            PosterUrl = e.StillUrl ?? detail.ShowBackdropUrl ?? detail.ShowPosterUrl;
            _nextEpisode = detail.Next is { Available: true } next ? next : null;
        }
        else
        {
            var movie = await api.MovieAsync(MovieId);
            Title = movie.Title;
            _runtime = TimeSpan.FromMinutes(movie.RuntimeMinutes ?? 0);
            PosterUrl = movie.BackdropUrl ?? movie.PosterUrl;
            _nextEpisode = null;
        }
        Stream = await api.StreamInfoAsync(MovieId, _releaseId, episodeId: EpisodeId);
        _releaseId = Stream.ReleaseId;
        StartPreviewFrames(_releaseId);
        ResumePosition = _resume ? TimeSpan.FromMilliseconds(Stream.ResumePositionMs) : TimeSpan.Zero;
        StatusPosition = ResumePosition;

        foreach (var s in Stream.Subtitles) SubtitleChoices.Add(new SubtitleChoice($"{s.Label} ({s.Language})", s));
        SelectedSubtitle = settings.SubtitlesByDefault
            ? SubtitleChoices.FirstOrDefault(c => c.Track?.Language.StartsWith(settings.PreferredSubtitleLanguage, StringComparison.OrdinalIgnoreCase) == true) ?? SubtitleChoices[0]
            : SubtitleChoices[0];

        await UpdateUpNextAsync();
        ApplySource(ResumePosition);
    });

    /// <summary>
    /// File types this device's player opens itself. Apple's only plays MP4 and MOV, so the MKVs and AVIs most torrents
    /// have are converted by the server from the start rather than failing first.
    /// </summary>
    public static bool CanPlayDirectly(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
#if IOS || MACCATALYST
        ".mp4" or ".m4v" or ".mov" => true,
#elif ANDROID
        ".mp4" or ".m4v" or ".mov" or ".mkv" or ".webm" or ".ts" => true,
#else
        ".mp4" or ".m4v" or ".mov" or ".mkv" or ".webm" or ".ts" or ".avi" or ".wmv" or ".mpg" or ".mpeg" => true,
#endif
        _ => false,
    };

    /// <summary>
    /// After direct play fails: switches to the server's conversion at <paramref name="position"/> (the file's video or
    /// audio is something this device can't decode). False when it was already converting, or the server can't.
    /// </summary>
    /// <summary>Why the server's conversion failed, in its own words, when it's the converted stream that wouldn't play.</summary>
    public Task<string?> ConversionErrorAsync() =>
        IsTranscoding && SourceUrl is { } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? api.StreamErrorAsync(url)
            : Task.FromResult<string?>(null);

    public bool TryConvertAfterFailure(TimeSpan position)
    {
        if (_remuxing && !_remuxFailed && !_offline)
        {
            // The copy wouldn't play (a codec this device can't decode after all): the full conversion will.
            _remuxFailed = true;
            ApplySource(position);
            return true;
        }
        if (IsTranscoding || _forceConvert || Stream?.TranscodeUrl is null || _offline) return false;
        _forceConvert = true;
        ApplySource(position);
        return true;
    }

    /// <summary>Chooses direct play or a transcode URL starting at <paramref name="start"/>.</summary>
    private void ApplySource(TimeSpan start)
    {
        if (Stream is null) return;
        var quality = SelectedQuality.Resolve();
        // "Original" still has to be converted when this device can't play the file as it is.
        var mustConvert = Stream.TranscodeUrl is not null && (_forceConvert || !CanPlayDirectly(Stream.FileName));
        QualityLabel = (SelectedQuality.IsAuto ? $"Auto · {ShortName(quality)}" : ShortName(quality)) + (mustConvert && quality.IsOriginal ? " · converted" : "");
        IsTranscoding = Stream.TranscodeUrl is not null && (!quality.IsOriginal || mustConvert);
        if (IsTranscoding)
        {
            TranscodeOffset = start;
            // Only the file type in the way: the server can copy the picture and sound across instead of re-encoding.
            var url = $"{ConversionUrl(Stream, remux: quality.IsOriginal && !_forceConvert && !_remuxFailed)}&start={start.TotalSeconds:0.###}";
            if (quality.MaxKbps is { } kbps) url += $"&maxKbps={kbps}";
            if (quality.MaxHeight is { } h) url += $"&maxHeight={h}";
            SourceUrl = url;
            ResumePosition = TimeSpan.Zero; // the transcode already starts there
        }
        else
        {
            TranscodeOffset = TimeSpan.Zero;
            _remuxing = false;
            SourceUrl = Stream.DirectUrl;
            ResumePosition = start;
        }
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Where the server's conversion is played from. Apple's player only plays HTTP video it can fetch in byte ranges,
    /// which the piped conversion hasn't got, so iOS and macOS take it as HLS instead, in a session of its own each time
    /// (a seek restarts the conversion). Older servers have no HLS: the piped one is all there is.
    /// <paramref name="remux"/> lets the server copy what Apple can decode rather than re-encode it, which starts and
    /// seeks almost at once.
    /// </summary>
    private string ConversionUrl(StreamInfoDto stream, bool remux)
    {
        _remuxing = false;
#if IOS || MACCATALYST
        if (stream.HlsUrl is { } hls)
        {
            _remuxing = remux;
            return hls.Replace("{session}", Guid.NewGuid().ToString("N")) + (remux ? "&remux=true" : "");
        }
#endif
        return stream.TranscodeUrl!;
    }

    /// <summary>Seeking a transcode means restarting ffmpeg at the new offset.</summary>
    public bool TrySeekTranscode(TimeSpan target)
    {
        if (!IsTranscoding) return false;
        ApplySource(target < TimeSpan.Zero ? TimeSpan.Zero : target);
        return true;
    }

    /// <summary>Plays here again from <paramref name="position"/> (after casting).</summary>
    public void RestartAt(TimeSpan position)
    {
        if (!_offline) ApplySource(position);
    }

    // ---------- Casting ----------

    /// <summary>
    /// What to send a cast device to play from <paramref name="start"/>: the file itself when the TV plays that kind (and
    /// original quality is asked for), else the server's conversion (H.264 and AAC in MP4, which every cast device
    /// plays), which starts there. Null for a downloaded copy: that's on this device, where the TV can't fetch it.
    /// </summary>
    public CastMedia? CastMediaFrom(TimeSpan start, bool convert = false)
    {
        if (_offline || Stream is null) return null;
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        var quality = SelectedQuality.Resolve();
        var contentType = CastContentType(Stream.FileName);
        if (Stream.TranscodeUrl is null || (!convert && quality.IsOriginal && contentType is not null))
            return new CastMedia(Stream.DirectUrl, contentType ?? "video/mp4", TimeSpan.Zero, start, Converted: false);
        var url = $"{Stream.TranscodeUrl}&start={start.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
        if (quality.MaxKbps is { } kbps) url += $"&maxKbps={kbps}";
        if (quality.MaxHeight is { } h) url += $"&maxHeight={h}";
        return new CastMedia(url, "video/mp4", start, TimeSpan.Zero, Converted: true);
    }

    /// <summary>
    /// The file types a cast device plays as they are. MKVs aren't among them: some play, but the AC3 or DTS sound most
    /// carry plays silently, with no error to switch to the conversion on.
    /// </summary>
    private static string? CastContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        _ => null,
    };

    /// <summary>The movie's full length while casting <paramref name="media"/>, of which the TV knows <paramref name="castDuration"/>.</summary>
    public TimeSpan CastLength(TimeSpan castDuration, CastMedia media) =>
        castDuration > TimeSpan.Zero ? media.Offset + castDuration : _runtime;

    /// <summary>The cast device's position, as OnPosition is this device's: subtitles here, and progress saved.</summary>
    public void OnCastPosition(TimeSpan position, TimeSpan total, bool playing)
    {
        _playedTo = position;
        if (_cues.Count > 0) SubtitleText = SubtitleParser.CueAt(_cues, position)?.Text;
        if (DateTime.UtcNow - _lastReport > TimeSpan.FromSeconds(10))
            _ = ReportProgressAsync(position, total, playing ? PlayerState.Playing : PlayerState.Paused);
    }

    public void ChangeQuality(QualityOption quality, TimeSpan currentPosition)
    {
        SelectedQuality = quality;
        if (!_offline) ApplySource(currentPosition);
    }

    partial void OnSelectedSubtitleChanged(SubtitleChoice? value) => _ = LoadSubtitlesAsync(value);

    private async Task LoadSubtitlesAsync(SubtitleChoice? choice)
    {
        _cues = [];
        SubtitleText = null;
        if (choice?.Track is not { } track) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                _cues = SubtitleParser.Parse(await api.SubtitleTextAsync(track.Url));
                return;
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
            {
                await Task.Delay(2000); // still downloading from the swarm
            }
            catch (HttpRequestException)
            {
                return;
            }
        }
    }

    /// <summary>Called on every position tick: updates subtitles and reports progress periodically.</summary>
    public void OnPosition(TimeSpan elementPosition, TimeSpan duration, bool playing)
    {
        var position = elementPosition + TranscodeOffset;
        _playedTo = position;
        if (_cues.Count > 0) SubtitleText = SubtitleParser.CueAt(_cues, position)?.Text;
        if (DateTime.UtcNow - _lastReport > TimeSpan.FromSeconds(10))
            _ = ReportAsync(position, duration, playing ? PlayerState.Playing : PlayerState.Paused);
    }

    public TimeSpan ActualPosition(TimeSpan elementPosition) => elementPosition + TranscodeOffset;

    /// <summary>Full length of the movie, even while a transcode only knows the remaining part.</summary>
    public TimeSpan TotalDuration(TimeSpan elementDuration) =>
        !IsTranscoding ? elementDuration
        : elementDuration > TimeSpan.Zero ? elementDuration + TranscodeOffset
        : _runtime;

    public Task ReportAsync(TimeSpan position, TimeSpan duration, PlayerState state)
    {
        // A live transcode often reports no duration (or only what's left), so fall back to metadata.
        var total = !IsTranscoding ? duration
            : duration > TimeSpan.Zero ? duration + TranscodeOffset
            : _runtime;
        return ReportProgressAsync(position, total, state);
    }

    /// <summary>Saves how far it's got (<paramref name="total"/> being the whole movie's length).</summary>
    public async Task ReportProgressAsync(TimeSpan position, TimeSpan total, PlayerState state)
    {
        _lastReport = DateTime.UtcNow;
        var pos = (long)position.TotalMilliseconds;
        var dur = (long)total.TotalMilliseconds;
        if (dur <= 0) return;

        if (_offline)
        {
            await downloads.RecordOfflineProgressAsync(MovieId, pos, dur, EpisodeId);
            return;
        }
        try
        {
            await api.ReportTimelineAsync(new TimelineUpdate(MovieId, _releaseId, pos, dur, state, settings.ClientName, EpisodeId));
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException)
        {
            // Lost connection mid-play; remember locally if downloaded.
            await downloads.RecordOfflineProgressAsync(MovieId, pos, dur, EpisodeId);
        }
    }

    private async Task UpdateUpNextAsync()
    {
        UpNextTitle = null;
        if (EpisodeId is not null)
        {
            if (_nextOffline is { } downloaded)
            {
                UpNextTitle = downloaded.Subtitle;
                return;
            }
            UpNextTitle = _nextEpisode is { } next ? $"S{next.SeasonNumber}:E{next.EpisodeNumber} {next.Title}".Trim() : null;
            return;
        }
        var index = _queue.IndexOf(MovieId);
        if (index < 0 || index + 1 >= _queue.Count) return;
        try
        {
            UpNextTitle = (await api.MovieAsync(_queue[index + 1])).Title;
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiException)
        {
            UpNextTitle = null;
        }
    }

    /// <summary>Advances the play queue (playlists / "play all") or to the show's next episode. Returns false at the end.</summary>
    public bool TryPlayNext()
    {
        if (!settings.AutoPlayNext) return false;
        if (EpisodeId is not null)
        {
            if (_offline && _nextOffline is { EpisodeId: { } downloaded } nextOffline)
            {
                EpisodeId = downloaded;
                _resume = nextOffline.OfflinePositionMs > 0;
                LoadCommand.Execute(null);
                return true;
            }
            if (_nextEpisode is not { } next) return false;
            EpisodeId = next.Id;
            _resume = next.Playback is { Watched: false, PositionMs: > 0 };
            _releaseId = null;
            LoadCommand.Execute(null);
            return true;
        }
        var index = _queue.IndexOf(MovieId);
        if (index < 0 || index + 1 >= _queue.Count) return false;
        MovieId = _queue[index + 1];
        _resume = false;
        _releaseId = null;
        LoadCommand.Execute(null);
        return true;
    }

    partial void OnSubtitleSizeChanged(double value) => settings.SubtitleSize = value;
}
