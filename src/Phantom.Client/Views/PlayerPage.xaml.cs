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
        Media.MediaFailed += (_, e) => Dispatcher.Dispatch(() =>
        {
            SetLoading(false);
            _vm.ErrorMessage = PlaybackError(e.ErrorMessage);
        });

        // Material only has numbered skip icons for 5, 10 and 30 seconds; other lengths get the plain arrow.
        BackButton.Source = IconExtension.Create(_vm.SkipBack is 5 or 10 or 30 ? $"replay_{_vm.SkipBack}" : "fast_rewind", Colors.White, size: 36);
        ForwardButton.Source = IconExtension.Create(_vm.SkipForward is 5 or 10 or 30 ? $"forward_{_vm.SkipForward}" : "fast_forward", Colors.White, size: 36);
        SemanticProperties.SetDescription(BackButton, $"Back {_vm.SkipBack} seconds");
        SemanticProperties.SetDescription(ForwardButton, $"Forward {_vm.SkipForward} seconds");
        ToolTipProperties.SetText(BackButton, $"Back {_vm.SkipBack}s");
        ToolTipProperties.SetText(ForwardButton, $"Forward {_vm.SkipForward}s");

        // Loading from the moment the page opens: finding the torrent can take a while before there's a source at all.
        SetLoading(true);

        VolumeSlider.Value = _vm.Volume;
        Media.Volume = _vm.Volume;
        UpdateMuteIcon();
        FullscreenButton.IsVisible = DeviceInfo.Platform == DevicePlatform.WinUI;
    }

    // ---------- Volume, aspect, full screen ----------

    private void OnVolumeChanged(object? sender, ValueChangedEventArgs e)
    {
        Media.Volume = e.NewValue;
        _vm.Volume = e.NewValue;
        if (e.NewValue > 0 && Media.ShouldMute) Media.ShouldMute = false;
        UpdateMuteIcon();
    }

    private void OnMute(object? sender, EventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        Media.ShouldMute = !Media.ShouldMute;
        UpdateMuteIcon();
        ShowControls();
    }

    private void ChangeVolume(double by) => VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + by, 0, 1);

    private void UpdateMuteIcon() => MuteButton.Source = IconExtension.Create(
        Media.ShouldMute || Media.Volume == 0 ? "volume_off" : Media.Volume < 0.5 ? "volume_down" : "volume_up", Colors.White);

    private void OnAspect(object? sender, EventArgs e)
    {
        var fill = Media.Aspect != Aspect.AspectFill;
        Media.Aspect = fill ? Aspect.AspectFill : Aspect.AspectFit;
        AspectButton.Source = IconExtension.Create(fill ? "fit_screen" : "crop_free", Colors.White, outlined: true);
        Flash(fill ? "Fill screen" : "Fit to screen");
    }

    private bool _fullscreen;

    private void OnFullscreen(object? sender, EventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
        {
            window.AppWindow.SetPresenter(on ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            _fullscreen = on;
            FullscreenButton.Source = IconExtension.Create(on ? "fullscreen_exit" : "fullscreen", Colors.White);
        }
#endif
    }

    // ---------- Double-tap to skip ----------

    private async void OnSurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not View surface || e.GetPosition(surface) is not { } at || TransportControls.IsVisible == false && Buffering.IsVisible) return;
        if (at.X < surface.Width / 3) await SkipAsync(-_vm.SkipBack);
        else if (at.X > surface.Width * 2 / 3) await SkipAsync(_vm.SkipForward);
    }

    private async Task SkipAsync(int seconds)
    {
        Flash(seconds < 0 ? $"−{-seconds}s" : $"+{seconds}s", left: seconds < 0);
        await SeekToAsync(_vm.ActualPosition(Media.Position) + TimeSpan.FromSeconds(seconds));
    }

    private CancellationTokenSource? _flash;

    /// <summary>A short message over the video: "+30s", "Fill screen"...</summary>
    private async void Flash(string text, bool? left = null)
    {
        SkipFlashText.Text = text;
        SkipFlash.HorizontalOptions = left switch { true => LayoutOptions.Start, false => LayoutOptions.End, null => LayoutOptions.Center };
        SkipFlash.IsVisible = true;
        SkipFlash.Opacity = 1;
        _flash?.Cancel();
        var cts = _flash = new CancellationTokenSource();
        try
        {
            await Task.Delay(700, cts.Token);
            await SkipFlash.FadeToAsync(0, 250);
            if (!cts.IsCancellationRequested) SkipFlash.IsVisible = false;
        }
        catch (TaskCanceledException) { }
    }

    // ---------- Up next ----------

    private static readonly TimeSpan UpNextLead = TimeSpan.FromSeconds(15);
    private bool _upNextCancelled;

    private void UpdateUpNext(TimeSpan position, TimeSpan total)
    {
        var remaining = total - position;
        var show = !_upNextCancelled && _vm.UpNextTitle is not null && total > TimeSpan.Zero && remaining <= UpNextLead && remaining > TimeSpan.Zero;
        UpNextCard.IsVisible = show;
        if (show)
        {
            UpNextCountdown.Text = $"Starts in {Math.Ceiling(remaining.TotalSeconds / _vm.Speed):0}s";
            Controls.IsVisible = true;
        }
    }

    private void OnPlayNextNow(object? sender, EventArgs e)
    {
        UpNextCard.IsVisible = false;
        _ = Report(PlayerState.Stopped, atEnd: true);
        _vm.TryPlayNext();
    }

    private void OnCancelUpNext(object? sender, EventArgs e)
    {
        _upNextCancelled = true;
        UpNextCard.IsVisible = false;
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
            // The estimate is for where playback will carry on from.
            if (Media.Position > TimeSpan.Zero) _vm.StatusPosition = _vm.ActualPosition(Media.Position);
            _vm.StartLoadingStatus();
            Controls.IsVisible = true; // keep the close button reachable
        }
        else _vm.StopLoadingStatus();
    }

    /// <summary>The player's error as something people can act on (the raw one is often an HTTP status or a codec code).</summary>
    private static string PlaybackError(string? raw)
    {
        var text = raw ?? "";
        if (text.Contains("502") || text.Contains("503") || text.Contains("504") || text.Contains("Gateway", StringComparison.OrdinalIgnoreCase))
            return "The Phantom server isn't responding right now. It may be restarting — try again in a minute.";
        if (text.Contains("404")) return "The server couldn't find this movie's file. Try another version.";
        if (text.Contains("401") || text.Contains("403")) return "Your sign-in has expired. Sign in again.";
        return "This movie couldn't be played. Try again, choose another version, or pick a lower quality (the server converts it).";
    }

    private void LoadSource()
    {
        if (_vm.SourceUrl is not { } url) return;
        _upNextCancelled = false;
        UpNextCard.IsVisible = false;
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
        // When it'll finish at this speed, as Plex shows.
        EndsAtLabel.Text = total > position ? $"Ends at {DateTime.Now + (total - position) / Math.Max(0.25, _vm.Speed):t}" : "";
        UpdateUpNext(position, total);
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
        if (_upNextCancelled || !_vm.TryPlayNext()) await Shell.Current.GoToAsync("..");
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
        await SkipAsync(-_vm.SkipBack);
    }

    private async void OnSkipForward(object? sender, EventArgs e)
    {
        ShowControls();
        await SkipAsync(_vm.SkipForward);
    }

    // ---------- Keyboard (Windows) ----------

    /// <summary>YouTube-style keys. Returns true when the key did something.</summary>
    private bool HandleKey(string key)
    {
        if (Buffering.IsVisible && key is not ("Escape" or "F")) return false;
        switch (key)
        {
            case "Space" or "K": OnPlayPause(null, EventArgs.Empty); break;
            case "Left" or "J": _ = SkipAsync(-_vm.SkipBack); break;
            case "Right" or "L": _ = SkipAsync(_vm.SkipForward); break;
            case "Up": ChangeVolume(0.1); Flash($"Volume {VolumeSlider.Value:P0}"); break;
            case "Down": ChangeVolume(-0.1); Flash($"Volume {VolumeSlider.Value:P0}"); break;
            case "M": ToggleMute(); Flash(Media.ShouldMute ? "Muted" : "Sound on"); break;
            case "F": SetFullscreen(!_fullscreen); break;
            case "C": ToggleSubtitles(); break;
            case "Escape":
                if (_fullscreen) SetFullscreen(false);
                else OnClose(null, EventArgs.Empty);
                break;
            default: return false;
        }
        ShowControls();
        return true;
    }

    /// <summary>Subtitles off, or on in the first available language.</summary>
    private void ToggleSubtitles()
    {
        if (_vm.SubtitleChoices.Count <= 1)
        {
            Flash("No subtitles");
            return;
        }
        _vm.SelectedSubtitle = _vm.SelectedSubtitle?.Track is null ? _vm.SubtitleChoices[1] : _vm.SubtitleChoices[0];
        Flash(_vm.SelectedSubtitle.Track is null ? "Subtitles off" : $"Subtitles: {_vm.SelectedSubtitle.Label}");
    }

#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? _keyRoot;
    private Microsoft.UI.Xaml.Input.KeyEventHandler? _keyHandler;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if ((Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement)?.XamlRoot?.Content is not { } root) return;
        _keyRoot = root;
        _keyHandler = (_, e) =>
        {
            var key = e.Key switch
            {
                Windows.System.VirtualKey.Space => "Space",
                Windows.System.VirtualKey.Left => "Left",
                Windows.System.VirtualKey.Right => "Right",
                Windows.System.VirtualKey.Up => "Up",
                Windows.System.VirtualKey.Down => "Down",
                Windows.System.VirtualKey.Escape => "Escape",
                >= Windows.System.VirtualKey.A and <= Windows.System.VirtualKey.Z => e.Key.ToString(),
                _ => "",
            };
            if (key.Length > 0 && HandleKey(key)) e.Handled = true;
        };
        root.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, _keyHandler, handledEventsToo: true);
    }
#endif

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

    // Menus mark what's in use with a tick, and say it in the title, so it's clear before anything is picked.
    private const string Tick = "✓  ";

    private static string[] Marked(IEnumerable<string> options, string current) =>
        [.. options.Select(o => o == current ? Tick + o : o)];

    private static string? Unmarked(string? choice) => choice?.StartsWith(Tick) == true ? choice[Tick.Length..] : choice;

    private async void OnSubtitles(object? sender, EventArgs e)
    {
        var labels = _vm.SubtitleChoices.Select(c => c.Label).ToList();
        if (labels.Count <= 1)
        {
            await DisplayAlertAsync("Subtitles", "This release has no subtitle files.", "OK");
            return;
        }
        var current = _vm.SelectedSubtitle?.Label ?? labels[0];
        var choice = Unmarked(await DisplayActionSheetAsync($"Subtitles · {current} · text size {_vm.SubtitleSize:0}", "Cancel", null,
            [.. Marked(labels, current), "Larger text", "Smaller text"]));
        if (choice == "Larger text") _vm.SubtitleSize = Math.Min(40, _vm.SubtitleSize + 2);
        else if (choice == "Smaller text") _vm.SubtitleSize = Math.Max(12, _vm.SubtitleSize - 2);
        else if (_vm.SubtitleChoices.FirstOrDefault(c => c.Label == choice) is { } picked) _vm.SelectedSubtitle = picked;
    }

    private async void OnQuality(object? sender, EventArgs e)
    {
        var current = _vm.SelectedQuality.Name;
        var choice = Unmarked(await DisplayActionSheetAsync($"Quality · {(string.IsNullOrEmpty(_vm.QualityLabel) ? current : _vm.QualityLabel)}", "Cancel", null,
            Marked(_vm.Qualities.Select(q => q.Name), current)));
        if (_vm.Qualities.FirstOrDefault(q => q.Name == choice) is { } quality && quality != _vm.SelectedQuality)
        {
            if (!quality.Resolve().IsOriginal && _vm.Stream?.TranscodeUrl is null)
            {
                await DisplayAlertAsync("Quality", "The server can't transcode (ffmpeg not installed), so only original quality is available.", "OK");
                return;
            }
            _vm.ChangeQuality(quality, _vm.ActualPosition(Media.Position));
        }
    }

    private async void OnSpeed(object? sender, EventArgs e)
    {
        var current = $"{_vm.Speed}x";
        var choice = Unmarked(await DisplayActionSheetAsync($"Playback speed · {current}", "Cancel", null, Marked(_vm.Speeds.Select(s => $"{s}x"), current)));
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
        SetFullscreen(false);
#if WINDOWS
        if (_keyRoot is not null && _keyHandler is not null) _keyRoot.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, _keyHandler);
#endif
        await Report(PlayerState.Stopped);
        Media.Stop();
        Media.Handler?.DisconnectHandler();
    }
}
