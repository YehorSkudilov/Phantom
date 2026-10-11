using Phantom.Controls;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using Phantom.Services;
using Phantom.ViewModels;
using Phantom.Shared;

namespace Phantom.Views;

public partial class PlayerPage : ContentPage
{
    private readonly PlayerViewModel _vm;
    private bool _seeking;
    private bool _pendingResume;
    private IDispatcherTimer? _hideTimer;

    public PlayerPage(PlayerViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _vm.SourceChanged += (_, _) => Dispatcher.Dispatch(LoadSource);

        Media.MediaOpened += (_, _) => Dispatcher.Dispatch(OnOpened);
        Media.PositionChanged += (_, e) => Dispatcher.Dispatch(() => OnPositionChanged(e.Position));
        Media.StateChanged += (_, e) => Dispatcher.Dispatch(() => OnStateChanged(e.NewState));
        Media.MediaEnded += (_, _) => Dispatcher.Dispatch(OnEnded);
        Media.MediaFailed += (_, e) => Dispatcher.Dispatch(() => _vm.ErrorMessage = $"Playback failed: {e.ErrorMessage}");

        // Material only has numbered skip icons for 5, 10 and 30 seconds; other lengths get the plain arrow.
        BackButton.Source = IconExtension.Create(_vm.SkipBack is 5 or 10 or 30 ? $"replay_{_vm.SkipBack}" : "fast_rewind", Colors.White, size: 36);
        ForwardButton.Source = IconExtension.Create(_vm.SkipForward is 5 or 10 or 30 ? $"forward_{_vm.SkipForward}" : "fast_forward", Colors.White, size: 36);
        SemanticProperties.SetDescription(BackButton, $"Back {_vm.SkipBack} seconds");
        SemanticProperties.SetDescription(ForwardButton, $"Forward {_vm.SkipForward} seconds");
        ToolTipProperties.SetText(BackButton, $"Back {_vm.SkipBack}s");
        ToolTipProperties.SetText(ForwardButton, $"Forward {_vm.SkipForward}s");

        // Loading from the moment the page opens: finding the torrent can take a while before there's a source at all.
        SetLoading(true);
    }

    /// <summary>
    /// While the movie is still loading or buffering there's nothing to control yet: only the title and close button show,
    /// over the loading panel.
    /// </summary>
    private void SetLoading(bool loading)
    {
        Buffering.IsVisible = loading;
        TransportControls.IsVisible = SeekArea.IsVisible = !loading;
        SubtitlesButton.IsVisible = QualityButton.IsVisible = SpeedButton.IsVisible = !loading;
        if (loading)
        {
            _vm.StartLoadingStatus();
            Controls.IsVisible = true; // keep the close button reachable
        }
        else _vm.StopLoadingStatus();
    }

    private void LoadSource()
    {
        if (_vm.SourceUrl is not { } url) return;
        _pendingResume = _vm.ResumePosition > TimeSpan.Zero;
        SetLoading(true);
        Media.Source = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaSource.FromUri(url) : MediaSource.FromFile(url);
        ShowControls();
    }

    private async void OnOpened()
    {
        SetLoading(false);
        if (_pendingResume)
        {
            _pendingResume = false;
            await Media.SeekTo(_vm.ResumePosition);
        }
        Media.Play();
    }

    private void OnPositionChanged(TimeSpan elementPosition)
    {
        var total = _vm.TotalDuration(Media.Duration);
        var position = _vm.ActualPosition(elementPosition);
        if (!_seeking && total > TimeSpan.Zero) Seek.Value = Math.Clamp(position / total, 0, 1);
        PositionLabel.Text = TimeSpanConverter.Format(position);
        DurationLabel.Text = total > TimeSpan.Zero ? $"-{TimeSpanConverter.Format(total - position)}" : "";
        _vm.OnPosition(elementPosition, Media.Duration, Media.CurrentState == MediaElementState.Playing);
    }

    private void OnStateChanged(MediaElementState state)
    {
        SetLoading(state is MediaElementState.Buffering or MediaElementState.Opening);
        PlayPause.Source = IconExtension.Create(state == MediaElementState.Playing ? "pause" : "play_arrow", Colors.White, size: 40);
        if (state == MediaElementState.Paused) _ = Report(PlayerState.Paused);
    }

    private async void OnEnded()
    {
        await Report(PlayerState.Stopped, atEnd: true);
        if (!_vm.TryPlayNext()) await Shell.Current.GoToAsync("..");
    }

    private Task Report(PlayerState state, bool atEnd = false)
    {
        var total = _vm.TotalDuration(Media.Duration);
        var position = atEnd ? total : _vm.ActualPosition(Media.Position);
        return _vm.ReportAsync(position, Media.Duration, state);
    }

    // ---------- Controls ----------

    private void ShowControls()
    {
        Controls.IsVisible = true;
        _hideTimer?.Stop();
        _hideTimer = Dispatcher.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(4);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            if (Media.CurrentState == MediaElementState.Playing && !_seeking) Controls.IsVisible = false;
        };
        _hideTimer.Start();
    }

    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        if (Controls.IsVisible) Controls.IsVisible = false;
        else ShowControls();
    }

    private void OnPlayPause(object? sender, EventArgs e)
    {
        if (Media.CurrentState == MediaElementState.Playing) Media.Pause();
        else Media.Play();
        ShowControls();
    }

    private Task SeekToAsync(TimeSpan target)
    {
        var total = _vm.TotalDuration(Media.Duration);
        if (total > TimeSpan.Zero && target > total) target = total - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        // Transcodes restart server-side at the new offset; direct streams seek with HTTP ranges.
        if (_vm.TrySeekTranscode(target)) return Task.CompletedTask;
        return Media.SeekTo(target);
    }

    private async void OnSkipBack(object? sender, EventArgs e)
    {
        ShowControls();
        await SeekToAsync(_vm.ActualPosition(Media.Position) - TimeSpan.FromSeconds(_vm.SkipBack));
    }

    private async void OnSkipForward(object? sender, EventArgs e)
    {
        ShowControls();
        await SeekToAsync(_vm.ActualPosition(Media.Position) + TimeSpan.FromSeconds(_vm.SkipForward));
    }

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        _seeking = true;
        ShowControls();
    }

    private CancellationTokenSource? _previewDelay;

    // While dragging: show where the drag would land, and (after a short pause, so a fast drag doesn't ask the server
    // for every frame on the way) a preview frame from there.
    private async void OnSeekValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (!_seeking) return;
        var total = _vm.TotalDuration(Media.Duration);
        if (total <= TimeSpan.Zero) return;
        var at = total * e.NewValue;

        SeekPreview.IsVisible = true;
        SeekPreviewTime.Text = TimeSpanConverter.Format(at);
        var half = SeekPreview.WidthRequest / 2;
        SeekPreview.TranslationX = Math.Clamp(Seek.Width * e.NewValue - half, 0, Math.Max(0, Seek.Width - SeekPreview.WidthRequest));

        if (_vm.ThumbnailUrl(at) is not { } url)
        {
            SeekPreviewImage.IsVisible = false;
            return;
        }
        _previewDelay?.Cancel();
        _previewDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, _previewDelay.Token);
            SeekPreviewImage.IsVisible = true;
            SeekPreviewImage.Source = new UriImageSource { Uri = new Uri(url), CachingEnabled = true, CacheValidity = TimeSpan.FromDays(1) };
        }
        catch (TaskCanceledException)
        {
            // A newer position replaced this one.
        }
    }

    private async void OnSeekCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
        _previewDelay?.Cancel();
        SeekPreview.IsVisible = false;
        await SeekToAsync(_vm.TotalDuration(Media.Duration) * Seek.Value);
    }

    private async void OnSubtitles(object? sender, EventArgs e)
    {
        var labels = _vm.SubtitleChoices.Select(c => c.Label).ToList();
        if (labels.Count <= 1)
        {
            await DisplayAlertAsync("Subtitles", "This release has no subtitle files.", "OK");
            return;
        }
        var choice = await DisplayActionSheetAsync("Subtitles", "Cancel", null, [.. labels, "Larger text", "Smaller text"]);
        if (choice == "Larger text") _vm.SubtitleSize = Math.Min(40, _vm.SubtitleSize + 2);
        else if (choice == "Smaller text") _vm.SubtitleSize = Math.Max(12, _vm.SubtitleSize - 2);
        else if (_vm.SubtitleChoices.FirstOrDefault(c => c.Label == choice) is { } picked) _vm.SelectedSubtitle = picked;
    }

    private async void OnQuality(object? sender, EventArgs e)
    {
        var choice = await DisplayActionSheetAsync("Quality", "Cancel", null, [.. _vm.Qualities.Select(q => q.Name)]);
        if (_vm.Qualities.FirstOrDefault(q => q.Name == choice) is { } quality && quality != _vm.SelectedQuality)
        {
            if (!quality.IsOriginal && _vm.Stream?.TranscodeUrl is null)
            {
                await DisplayAlertAsync("Quality", "The server can't transcode (ffmpeg not installed), so only original quality is available.", "OK");
                return;
            }
            _vm.ChangeQuality(quality, _vm.ActualPosition(Media.Position));
        }
    }

    private async void OnSpeed(object? sender, EventArgs e)
    {
        var choice = await DisplayActionSheetAsync("Playback speed", "Cancel", null, [.. _vm.Speeds.Select(s => $"{s}x")]);
        if (choice is not null && double.TryParse(choice.TrimEnd('x'), System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            _vm.Speed = speed;
            SpeedButton.Text = choice;
        }
    }

    private async void OnClose(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        _hideTimer?.Stop();
        _vm.StopLoadingStatus();
        await Report(PlayerState.Stopped);
        Media.Stop();
        Media.Handler?.DisconnectHandler();
    }
}
