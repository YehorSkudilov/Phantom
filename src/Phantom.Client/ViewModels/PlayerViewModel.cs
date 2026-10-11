using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

public record SubtitleChoice(string Label, SubtitleTrackDto? Track)
{
    public override string ToString() => Label;
}

/// <summary>
/// Player state: source selection (direct / transcoded / offline file), resume, subtitles overlay,
/// timeline reporting and the up-next queue. The page wires MediaElement events into it.
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

    public ObservableCollection<SubtitleChoice> SubtitleChoices { get; } = [];
    public IReadOnlyList<QualityOption> Qualities => QualityOption.All;
    public IReadOnlyList<double> Speeds { get; } = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    [ObservableProperty] private int movieId;
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
        await api.QueueServerDownloadAsync(MovieId, _releaseId);
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
                var status = await api.StreamStatusAsync(MovieId, _releaseId, StatusPosition, ct);
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
                    ? $"Downloading at {BytesConverter.Format(status.DownloadRate)}/s, but this movie needs about {BytesConverter.Format(status.NeededRate)}/s to play without pausing."
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

    private static string FormatWait(int seconds) => seconds switch
    {
        < 60 => $"{Math.Max(1, seconds)} s",
        < 3600 => $"{(seconds + 30) / 60} min",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60} min",
    };

    /// <summary>The seek bar's preview frame at <paramref name="at"/>, or null when the server can't make one.</summary>
    public string? ThumbnailUrl(TimeSpan at) =>
        Stream?.ThumbnailUrl is { } url && !_offline ? $"{url}&t={(int)at.TotalSeconds / 10 * 10}" : null;

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

    public int SkipForward => settings.SkipForwardSeconds;
    public int SkipBack => settings.SkipBackSeconds;

    public event EventHandler? SourceChanged;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        MovieId = int.Parse(query["id"].ToString()!);
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
        LoadingText = "Opening…";
        LoadingDetail = null;
        LoadingProgress = -1;
        TranscodeOffset = TimeSpan.Zero;
        SubtitleText = null;
        _cues = [];
        SubtitleChoices.Clear();
        SubtitleChoices.Add(new SubtitleChoice("Off", null));

        var offline = downloads.Find(MovieId);
        if (_offline && offline is { IsComplete: true })
        {
            Title = offline.Title;
            PosterUrl = offline.PosterSource;
            ResumePosition = _resume ? TimeSpan.FromMilliseconds(offline.OfflinePositionMs) : TimeSpan.Zero;
            Stream = null;
            IsTranscoding = false;
            SourceUrl = offline.FilePath;
            SourceChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        StartLoadingStatus(); // the stream info below waits until the torrent is found, which can take a while
        var movie = await api.MovieAsync(MovieId);
        Title = movie.Title;
        _runtime = TimeSpan.FromMinutes(movie.RuntimeMinutes ?? 0);
        PosterUrl = movie.BackdropUrl ?? movie.PosterUrl;
        Stream = await api.StreamInfoAsync(MovieId, _releaseId);
        _releaseId = Stream.ReleaseId;
        ResumePosition = _resume ? TimeSpan.FromMilliseconds(Stream.ResumePositionMs) : TimeSpan.Zero;
        StatusPosition = ResumePosition;

        foreach (var s in Stream.Subtitles) SubtitleChoices.Add(new SubtitleChoice($"{s.Label} ({s.Language})", s));
        SelectedSubtitle = settings.SubtitlesByDefault
            ? SubtitleChoices.FirstOrDefault(c => c.Track?.Language.StartsWith(settings.PreferredSubtitleLanguage, StringComparison.OrdinalIgnoreCase) == true) ?? SubtitleChoices[0]
            : SubtitleChoices[0];

        await UpdateUpNextAsync();
        ApplySource(ResumePosition);
    });

    /// <summary>Chooses direct play or a transcode URL starting at <paramref name="start"/>.</summary>
    private void ApplySource(TimeSpan start)
    {
        if (Stream is null) return;
        var quality = SelectedQuality.Resolve();
        QualityLabel = SelectedQuality.IsAuto ? $"Auto · {ShortName(quality)}" : ShortName(quality);
        IsTranscoding = !quality.IsOriginal && Stream.TranscodeUrl is not null;
        if (IsTranscoding)
        {
            TranscodeOffset = start;
            var url = $"{Stream.TranscodeUrl}&start={start.TotalSeconds:0.###}";
            if (quality.MaxKbps is { } kbps) url += $"&maxKbps={kbps}";
            if (quality.MaxHeight is { } h) url += $"&maxHeight={h}";
            SourceUrl = url;
            ResumePosition = TimeSpan.Zero; // the transcode already starts there
        }
        else
        {
            TranscodeOffset = TimeSpan.Zero;
            SourceUrl = Stream.DirectUrl;
            ResumePosition = start;
        }
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seeking a transcode means restarting ffmpeg at the new offset.</summary>
    public bool TrySeekTranscode(TimeSpan target)
    {
        if (!IsTranscoding) return false;
        ApplySource(target < TimeSpan.Zero ? TimeSpan.Zero : target);
        return true;
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

    public async Task ReportAsync(TimeSpan position, TimeSpan duration, PlayerState state)
    {
        _lastReport = DateTime.UtcNow;
        // A live transcode often reports no duration (or only what's left), so fall back to metadata.
        var total = !IsTranscoding ? duration
            : duration > TimeSpan.Zero ? duration + TranscodeOffset
            : _runtime;
        var pos = (long)position.TotalMilliseconds;
        var dur = (long)total.TotalMilliseconds;
        if (dur <= 0) return;

        if (_offline)
        {
            await downloads.RecordOfflineProgressAsync(MovieId, pos, dur);
            return;
        }
        try
        {
            await api.ReportTimelineAsync(new TimelineUpdate(MovieId, _releaseId, pos, dur, state, settings.ClientName));
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException)
        {
            // Lost connection mid-play; remember locally if downloaded.
            await downloads.RecordOfflineProgressAsync(MovieId, pos, dur);
        }
    }

    private async Task UpdateUpNextAsync()
    {
        UpNextTitle = null;
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

    /// <summary>Advances the play queue (playlists / "play all"). Returns false at the end.</summary>
    public bool TryPlayNext()
    {
        if (!settings.AutoPlayNext) return false;
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
