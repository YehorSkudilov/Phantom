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
    private bool _started; // the current source has actually played, so a Paused state is the user's, not loading
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
            // Most failures are a file this device can't decode (DTS or AC3 sound, 10-bit HEVC, an AVI...): the server
            // converts it, from the same point, before giving up. A server error wouldn't be helped by that.
            if (!IsServerError(e.ErrorMessage)
                && _vm.TryConvertAfterFailure(_pendingResume || !_started ? _vm.ResumePosition + _vm.TranscodeOffset : _vm.ActualPosition(Media.Position)))
            {
                Flash("Converting it on the server…");
                return;
            }
            SetLoading(false);
            // Said in a pop-up, with the player's own words underneath (selectable, to pass on); closing it leaves the player.
            var message = PlaybackError(e.ErrorMessage, canConvert: _vm.Stream?.TranscodeUrl is not null);
            if (e.ErrorMessage is { Length: > 0 } raw && !raw.Contains('<'))
                message += $"\n\nDetails{(_vm.IsTranscoding ? " (converted by the server)" : "")}: {(raw.Length > 300 ? raw[..300] + "…" : raw)}";
            Guard(async () =>
            {
                // A failed conversion: the player only knows it got no video; the server knows why.
                if (await _vm.ConversionErrorAsync() is { } reason) message += $"\n\nServer: {reason}";
                await Dialog.ErrorAsync(message, "Can't play this");
                await Shell.Current.GoToAsync("..");
            });
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
#if IOS || MACCATALYST
        // Apple's slider only moves by dragging its thumb; a tap on the bar jumps there, as it does on Android and Windows.
        var seekTap = new TapGestureRecognizer();
        seekTap.Tapped += OnSeekTapped;
        Seek.GestureRecognizers.Add(seekTap);
#endif
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

    private void OnSurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not View surface || e.GetPosition(surface) is not { } at || TransportControls.IsVisible == false && Buffering.IsVisible) return;
        if (at.X < surface.Width / 3) Guard(() => SkipAsync(-_vm.SkipBack));
        else if (at.X > surface.Width * 2 / 3) Guard(() => SkipAsync(_vm.SkipForward));
    }

    /// <summary>
    /// Runs an event handler's work. An exception from an async void handler ends the app on iOS, so anything unexpected
    /// (MediaElement throws while the player is between states) is shown over the video instead.
    /// </summary>
    private async void Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Player: {ex}");
            _vm.ErrorMessage = $"Something went wrong in the player: {ex.Message}";
        }
    }

    // Taps in quick succession add up ("+10s", "+20s", ...) and seek once at the end: every seek makes the server fetch
    // a new part of the torrent (or restart a transcode), so seeking on each tap made skipping crawl.
    private TimeSpan? _skipFrom, _skipTarget;
    private CancellationTokenSource? _skipDelay;

    private async Task SkipAsync(int seconds)
    {
        var from = _skipFrom ??= _vm.ActualPosition(Media.Position);
        var total = _vm.TotalDuration(Media.Duration);
        var target = (_skipTarget ?? from) + TimeSpan.FromSeconds(seconds);
        if (total > TimeSpan.Zero && target > total) target = total - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        _skipTarget = target;

        var moved = (int)Math.Round((target - from).TotalSeconds);
        Flash(moved < 0 ? $"−{-moved}s" : $"+{moved}s", left: moved < 0);
        PositionLabel.Text = TimeSpanConverter.Format(target);
        if (total > TimeSpan.Zero) Seek.Value = Math.Clamp(target / total, 0, 1);

        _skipDelay?.Cancel();
        var cts = _skipDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return; // another tap; that one seeks
        }
        _skipFrom = _skipTarget = null;
        await SeekToAsync(target);
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
        catch (Exception)
        {
            // Replaced by a newer message, or the page is closing; either way nothing to show.
        }
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
    private static string PlaybackError(string? raw, bool canConvert)
    {
        var text = raw ?? "";
        if (text.Contains("502") || text.Contains("503") || text.Contains("504") || text.Contains("Gateway", StringComparison.OrdinalIgnoreCase))
            return "The Phantom server isn't responding right now. It may be restarting — try again in a minute.";
        if (text.Contains("404")) return "The server couldn't find this file. Try another source.";
        if (text.Contains("401") || text.Contains("403")) return "Your sign-in has expired. Sign in again.";
        return canConvert
            ? "This couldn't be played, even converted by the server. Try again, or choose another source."
            : "This device can't play this file's format, and the server can't convert it (ffmpeg isn't installed there). Choose another source.";
    }

    private static bool IsServerError(string? raw) =>
        raw is { } text && new[] { "401", "403", "404", "502", "503", "504", "Gateway" }.Any(code => text.Contains(code, StringComparison.OrdinalIgnoreCase));

    private void LoadSource()
    {
        if (_vm.SourceUrl is not { } url) return;
        _upNextCancelled = false;
        _started = false;
        UpNextCard.IsVisible = false;
        _pendingResume = _vm.ResumePosition > TimeSpan.Zero;
        SetLoading(true);
        Media.Source = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaSource.FromUri(url) : MediaSource.FromFile(url);
        ShowControls();
    }

    // The loading panel stays until it's Playing (see OnStateChanged): on iOS this fires as soon as the file's header is
    // read, well before the first frame.
    private void OnOpened() => Guard(async () =>
    {
        await TryResumeAsync();
        Media.Play();
    });

    /// <summary>
    /// Jumps to where the movie was left off. On iOS MediaOpened comes when the item is ready but the player may not be,
    /// and SeekTo throws until it is, so a failed attempt is retried when it starts playing (see OnStateChanged).
    /// </summary>
    private async Task TryResumeAsync()
    {
        if (!_pendingResume) return;
        _pendingResume = false;
        if (!await TrySeekAsync(_vm.ResumePosition)) _pendingResume = true;
    }

    /// <summary>MediaElement's SeekTo, without the exceptions iOS throws while the player isn't ready (they'd crash the app from an async void handler).</summary>
    private async Task<bool> TrySeekAsync(TimeSpan target)
    {
        try
        {
            await Media.SeekTo(target);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnPositionChanged(TimeSpan elementPosition)
    {
        var total = _vm.TotalDuration(Media.Duration);
        var position = _vm.ActualPosition(elementPosition);
        if (!_seeking && _skipTarget is null)
        {
            if (total > TimeSpan.Zero) Seek.Value = Math.Clamp(position / total, 0, 1);
            PositionLabel.Text = TimeSpanConverter.Format(position);
        }
        DurationLabel.Text = total > TimeSpan.Zero ? $"-{TimeSpanConverter.Format(total - position)}" : "";
        // When it'll finish at this speed, as Plex shows.
        EndsAtLabel.Text = total > position ? $"Ends at {DateTime.Now + (total - position) / Math.Max(0.25, _vm.Speed):t}" : "";
        UpdateUpNext(position, total);
        // Until the resume seek lands the position is the start of the file, not progress worth saving.
        if (!_pendingResume) _vm.OnPosition(elementPosition, Media.Duration, Media.CurrentState == MediaElementState.Playing);
    }

    private void OnStateChanged(MediaElementState state)
    {
        // iOS reports Stopped and then Paused while the item is still loading (AVPlayer's status, not playback), so until
        // it has actually played only Playing ends the loading panel.
        switch (state)
        {
            case MediaElementState.Buffering or MediaElementState.Opening:
                SetLoading(true);
                break;
            case MediaElementState.Playing:
                _started = true;
                SetLoading(false);
                _ = TryResumeAsync();
#if WINDOWS
                CheckForPicture();
#endif
                break;
            case MediaElementState.Paused or MediaElementState.Stopped when _started:
                SetLoading(false);
                break;
        }
        PlayPause.Source = IconExtension.Create(state == MediaElementState.Playing ? "pause" : "play_arrow", Colors.White, size: 40);
        if (state == MediaElementState.Paused) _ = Report(PlayerState.Paused);
    }

#if WINDOWS
    private string? _pictureChecked;

    /// <summary>
    /// Windows plays a file whose sound it can decode but whose picture it can't (10-bit HEVC without the paid extension,
    /// say) without failing: the poster stays up while the timeline ticks. So a moment into playback, no picture means
    /// the same as a failure: the server converts it from here.
    /// </summary>
    private void CheckForPicture()
    {
        var source = _vm.SourceUrl;
        if (source is null || _pictureChecked == source) return;
        _pictureChecked = source;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), () =>
        {
            if (_vm.SourceUrl != source || Media.CurrentState != MediaElementState.Playing || Media.MediaWidth > 0) return;
            if (_vm.TryConvertAfterFailure(_vm.ActualPosition(Media.Position)))
                Flash("Converting it on the server…");
            else if (!_vm.IsTranscoding)
                Flash("Windows can't show this video's picture (try installing HEVC Video Extensions)");
        });
    }
#endif

    private void OnEnded() => Guard(async () =>
    {
        await Report(PlayerState.Stopped, atEnd: true);
        if (_upNextCancelled || !_vm.TryPlayNext()) await Shell.Current.GoToAsync("..");
    });

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

    private DateTime _lastToggle;

    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        // The controls and the surface under them both listen; where a platform hands one tap to both, it counts once.
        if (DateTime.UtcNow - _lastToggle < TimeSpan.FromMilliseconds(250)) return;
        _lastToggle = DateTime.UtcNow;
        if (Controls.IsVisible && !Buffering.IsVisible) Controls.IsVisible = false; // while loading, Close stays reachable
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
        return TrySeekAsync(target);
    }

    private void OnSkipBack(object? sender, EventArgs e)
    {
        ShowControls();
        Guard(() => SkipAsync(-_vm.SkipBack));
    }

    private void OnSkipForward(object? sender, EventArgs e)
    {
        ShowControls();
        Guard(() => SkipAsync(_vm.SkipForward));
    }

    // ---------- Keyboard (Windows) ----------

    /// <summary>YouTube-style keys. Returns true when the key did something.</summary>
    private bool HandleKey(string key)
    {
        if (Buffering.IsVisible && key is not ("Escape" or "F")) return false;
        switch (key)
        {
            case "Space" or "K": OnPlayPause(null, EventArgs.Empty); break;
            case "Left" or "J": Guard(() => SkipAsync(-_vm.SkipBack)); break;
            case "Right" or "L": Guard(() => SkipAsync(_vm.SkipForward)); break;
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

#if ANDROID
    // ---------- TV remote (Android) ----------

    private readonly HashSet<Android.Views.Keycode> _usedKeys = [];

    /// <summary>
    /// A key's press, used here, has its release used too, so it doesn't reach (and click) the focused button.
    /// </summary>
    private bool OnRemoteKey(Android.Views.KeyEvent e)
    {
        if (e.Action == Android.Views.KeyEventActions.Up) return _usedKeys.Remove(e.KeyCode);
        if (e.Action != Android.Views.KeyEventActions.Down || !HandleRemoteKey(e.KeyCode)) return false;
        _usedKeys.Add(e.KeyCode);
        return true;
    }

    /// <summary>
    /// The remote, as on Netflix or YouTube: with the controls hidden, left and right skip (held, they keep going), OK
    /// plays or pauses and up or down bring the controls up. With them up, the arrows move between them as usual (on
    /// the seek bar, left and right skip) and Back hides them before it closes the player. The media keys always work.
    /// </summary>
    private bool HandleRemoteKey(Android.Views.Keycode key)
    {
        var ready = !Buffering.IsVisible;
        var hidden = !Controls.IsVisible;
        switch (key)
        {
            case Android.Views.Keycode.MediaPlayPause when ready:
                OnPlayPause(null, EventArgs.Empty);
                return true;
            case Android.Views.Keycode.MediaPlay when ready:
                Media.Play();
                ShowControls();
                return true;
            case Android.Views.Keycode.MediaPause when ready:
                Media.Pause();
                ShowControls();
                return true;
            case Android.Views.Keycode.MediaRewind when ready:
                Guard(() => SkipAsync(-_vm.SkipBack));
                return true;
            case Android.Views.Keycode.MediaFastForward when ready:
                Guard(() => SkipAsync(_vm.SkipForward));
                return true;
            case Android.Views.Keycode.MediaStop:
                OnClose(null, EventArgs.Empty);
                return true;
            case Android.Views.Keycode.Back when !hidden && ready:
                Controls.IsVisible = false;
                return true;
            case Android.Views.Keycode.DpadLeft or Android.Views.Keycode.DpadRight when ready && (hidden || IsFocused(Seek)):
                // A slider moved by its keys never says it's being dragged, so it would move without seeking.
                if (!hidden) ShowControls();
                Guard(() => SkipAsync(key == Android.Views.Keycode.DpadLeft ? -_vm.SkipBack : _vm.SkipForward));
                return true;
            case Android.Views.Keycode.DpadCenter or Android.Views.Keycode.Enter or Android.Views.Keycode.NumpadEnter when hidden && ready:
                OnPlayPause(null, EventArgs.Empty);
                FocusPlayPause();
                return true;
            case Android.Views.Keycode.DpadUp or Android.Views.Keycode.DpadDown when hidden:
                ShowControls();
                FocusPlayPause();
                return true;
        }
        // Moving between the controls keeps them up.
        if (!hidden) ShowControls();
        return false;
    }

    private void FocusPlayPause() => Dispatcher.Dispatch(() => (TransportControls.IsVisible ? PlayPause : (View)SubtitlesButton).Focus());

    private static bool IsFocused(View view) => view.Handler?.PlatformView is Android.Views.View { IsFocused: true };
#endif

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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        SetSystemBarsHidden(true);
#if WINDOWS
        HookKeys();
#elif ANDROID
        MainActivity.KeyHandler = OnRemoteKey;
#endif
    }

    // The phone's status bar (clock, battery) and navigation bar are hidden while the player is open; a swipe from the
    // edge brings them back for a moment, as in other video players. Windows has its own full screen (F).
    private void SetSystemBarsHidden(bool hidden)
    {
#if ANDROID
        if (Platform.CurrentActivity?.Window is not { } window) return;
        var bars = AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView);
        if (hidden)
        {
            bars.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            bars.Hide(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
        }
        else bars.Show(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
        // In landscape the video also uses the strip beside a camera cutout, instead of leaving it black.
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && window.Attributes is { } attributes)
        {
            attributes.LayoutInDisplayCutoutMode = hidden ? Android.Views.LayoutInDisplayCutoutMode.ShortEdges : Android.Views.LayoutInDisplayCutoutMode.Default;
            window.Attributes = attributes;
        }
#elif IOS
        // The page asks for it (PrefersStatusBarHidden / PrefersHomeIndicatorAutoHidden in the XAML); iOS only rereads
        // that when told to.
        if (Window?.Handler?.PlatformView is UIKit.UIWindow { RootViewController: { } root })
        {
            root.SetNeedsStatusBarAppearanceUpdate();
            root.SetNeedsUpdateOfHomeIndicatorAutoHidden();
        }
#endif
    }

#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? _keyRoot;
    private Microsoft.UI.Xaml.Input.KeyEventHandler? _keyHandler;

    private void HookKeys()
    {
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

    private void OnSeekTapped(object? sender, TappedEventArgs e)
    {
        var total = _vm.TotalDuration(Media.Duration);
        if (_seeking || total <= TimeSpan.Zero || Seek.Width <= 0 || e.GetPosition(Seek) is not { } at) return;
        var fraction = Math.Clamp(at.X / Seek.Width, 0, 1);
        Seek.Value = fraction;
        PositionLabel.Text = TimeSpanConverter.Format(total * fraction);
        ShowControls();
        Guard(() => SeekToAsync(total * fraction));
    }

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        _seeking = true;
        ShowControls();
    }

    private CancellationTokenSource? _preview;
    private string? _previewUrl;
    private byte[]? _shownFrame;

    // While dragging: show where the drag would land, and (after a short pause, so a fast drag doesn't ask the server
    // for every frame on the way) a preview frame from there. Only one frame is ever being fetched: moving on cancels
    // the request, so the server stops pulling that part of the torrent and playback isn't kept waiting behind it.
    private async void OnSeekValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (!_seeking || !double.IsFinite(e.NewValue)) return;
        var total = _vm.TotalDuration(Media.Duration);
        if (total <= TimeSpan.Zero) return;
        var at = total * Math.Clamp(e.NewValue, 0, 1);

        SeekPreview.IsVisible = true;
        SeekPreviewTime.Text = TimeSpanConverter.Format(at);
        var half = SeekPreview.WidthRequest / 2;
        SeekPreview.TranslationX = Math.Clamp(Seek.Width * e.NewValue - half, 0, Math.Max(0, Seek.Width - SeekPreview.WidthRequest));

        // Straight from the previews already downloaded (all of them, once the server has made them): the exact one,
        // or the nearest while the exact one is fetched.
        var frame = _vm.PreviewFrame(at, out var exact);
        if (frame is not null) ShowPreviewFrame(frame);
        if (exact) return;

        if (_vm.ThumbnailUrl(at) is not { } url)
        {
            if (frame is null) SeekPreviewImage.IsVisible = false;
            return;
        }
        if (url == _previewUrl) return; // same frame (they're 10 s apart), already on its way
        _previewUrl = url;
        _preview?.Cancel();
        var cts = _preview = new CancellationTokenSource();
        try
        {
            await Task.Delay(80, cts.Token);
            var jpeg = await _vm.ThumbnailAsync(url, cts.Token);
            if (jpeg is not null) _vm.RememberPreviewFrame(at, jpeg); // a miss may be a timeout, so it's asked again next time
            if (cts.IsCancellationRequested) return;
            // The frame on show stays up while the next one loads; only hide it when there's none at all.
            if (jpeg is not null) ShowPreviewFrame(jpeg);
            else if (frame is null) SeekPreviewImage.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            // A newer position replaced this one.
        }
        catch (Exception ex)
        {
            // A preview is a nicety: losing one must never take the player down.
            System.Diagnostics.Debug.WriteLine($"Seek preview: {ex}");
        }
    }

    private void ShowPreviewFrame(byte[] jpeg)
    {
        SeekPreviewImage.IsVisible = true;
        if (ReferenceEquals(jpeg, _shownFrame)) return; // the same picture: no need to decode it again
        _shownFrame = jpeg;
        SeekPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(jpeg));
    }

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
        _preview?.Cancel();
        _previewUrl = null;
        SeekPreview.IsVisible = false;
        Guard(() => SeekToAsync(_vm.TotalDuration(Media.Duration) * Seek.Value));
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
            await Dialog.AlertAsync("Subtitles", "This release has no subtitle files.");
            return;
        }
        var current = _vm.SelectedSubtitle?.Label ?? labels[0];
        var choice = Unmarked(await Dialog.ChooseAsync($"Subtitles · {current} · text size {_vm.SubtitleSize:0}", null,
            [.. Marked(labels, current), "Larger text", "Smaller text"]));
        if (choice == "Larger text") _vm.SubtitleSize = Math.Min(40, _vm.SubtitleSize + 2);
        else if (choice == "Smaller text") _vm.SubtitleSize = Math.Max(12, _vm.SubtitleSize - 2);
        else if (_vm.SubtitleChoices.FirstOrDefault(c => c.Label == choice) is { } picked) _vm.SelectedSubtitle = picked;
    }

    private async void OnQuality(object? sender, EventArgs e)
    {
        var current = _vm.SelectedQuality.Name;
        var choice = Unmarked(await Dialog.ChooseAsync($"Quality · {(string.IsNullOrEmpty(_vm.QualityLabel) ? current : _vm.QualityLabel)}", null,
            Marked(_vm.Qualities.Select(q => q.Name), current)));
        if (_vm.Qualities.FirstOrDefault(q => q.Name == choice) is { } quality && quality != _vm.SelectedQuality)
        {
            if (!quality.Resolve().IsOriginal && _vm.Stream?.TranscodeUrl is null)
            {
                await Dialog.AlertAsync("Quality", "The server can't transcode (ffmpeg not installed), so only original quality is available.");
                return;
            }
            _vm.ChangeQuality(quality, _vm.ActualPosition(Media.Position));
        }
    }

    private async void OnSpeed(object? sender, EventArgs e)
    {
        var current = $"{_vm.Speed}x";
        var choice = Unmarked(await Dialog.ChooseAsync($"Playback speed · {current}", null, Marked(_vm.Speeds.Select(s => $"{s}x"), current)));
        if (choice is not null && double.TryParse(choice.TrimEnd('x'), System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            _vm.Speed = speed;
            SpeedButton.Text = choice;
        }
    }

    private void OnClose(object? sender, EventArgs e) => Guard(() => Shell.Current.GoToAsync(".."));

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _hideTimer?.Stop();
        _skipDelay?.Cancel();
        _preview?.Cancel();
        _vm.StopLoadingStatus();
        _vm.StopPreviewFrames();
        SetFullscreen(false);
        SetSystemBarsHidden(false);
#if WINDOWS
        if (_keyRoot is not null && _keyHandler is not null) _keyRoot.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, _keyHandler);
#elif ANDROID
        if (MainActivity.KeyHandler == (Func<Android.Views.KeyEvent, bool>)OnRemoteKey) MainActivity.KeyHandler = null;
        _usedKeys.Clear();
#endif
        // The position is read now, then the player is stopped straight away: once this method awaits anything the page
        // is on its way out, and stopping a MediaElement whose native player has gone throws (which ended the app).
        var report = Report(PlayerState.Stopped);
        try
        {
            Media.Stop();
            Media.Handler?.DisconnectHandler();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Player teardown: {ex}");
        }
        _ = report.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // a failed report mustn't surface later
    }
}
