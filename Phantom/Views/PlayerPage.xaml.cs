using Phantom.Controls;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using Phantom.Services;
using Phantom.Services.Casting;
using Phantom.ViewModels;
using Phantom.Shared;

namespace Phantom.Views;

public partial class PlayerPage : ContentPage
{
    private readonly PlayerViewModel _vm;
    private bool _seeking;
    // Moving along the timeline with a TV remote (see Scrub): paused, showing the frame there over the whole picture.
    private bool _scrubbing;
    private bool _scrubResume;
    private TimeSpan _scrubFrom, _scrubAt;
    private bool _pendingResume;
    private bool _started; // the current source has actually played, so a Paused state is the user's, not loading
    private IDispatcherTimer? _hideTimer;

    public PlayerPage(PlayerViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        _vm.SourceChanged += (_, _) => Dispatcher.Dispatch(LoadSource);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.BufferedTo)) Dispatcher.Dispatch(UpdateBufferedTrack);
        };
        SeekTrack.SizeChanged += (_, _) => UpdateBufferedTrack();

        // While casting, this device's player is stopped: what it says then is about nothing on show.
        Media.MediaOpened += (_, _) => Dispatcher.Dispatch(() => { if (_cast is null) OnOpened(); });
        Media.PositionChanged += (_, e) => Dispatcher.Dispatch(() => { if (_cast is null) OnPositionChanged(e.Position); });
        Media.StateChanged += (_, e) => Dispatcher.Dispatch(() => { if (_cast is null) OnStateChanged(e.NewState); });
        Media.MediaEnded += (_, _) => Dispatcher.Dispatch(() => { if (_cast is null) OnEnded(); });
        Media.MediaFailed += (_, e) => Dispatcher.Dispatch(() =>
        {
            if (_cast is not null) return;
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
                // A file the server can't read at all says so itself, and what to do: that's the message then.
                if (await _vm.ConversionErrorAsync() is { } reason)
                    message = reason.Contains("probably fake or damaged") ? reason : message + $"\n\nServer: {reason}";
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

        // The device's own media volume where the app can set it (Android), else the player's (see DeviceControls).
        Media.Volume = DeviceControls.HasDeviceVolume ? 1 : _vm.Volume;
        VolumeSlider.Value = DeviceControls.HasDeviceVolume ? DeviceControls.GetVolume() : _vm.Volume;
        UpdateMuteIcon();
        BrightnessIcon.IsVisible = BrightnessSlider.IsVisible = DeviceControls.HasBrightness;
        FullscreenButton.IsVisible = DeviceInfo.Platform == DevicePlatform.WinUI;
        CastButton.IsVisible = CanCast;
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
        if (_cast is { } cast)
        {
            // The TV's volume, while casting.
            if (Math.Abs(cast.Volume - e.NewValue) > 0.005) Guard(() => cast.SetVolumeAsync(e.NewValue));
            UpdateMuteIcon();
            return;
        }
        if (DeviceControls.HasDeviceVolume) DeviceControls.SetVolume(e.NewValue);
        else
        {
            Media.Volume = e.NewValue;
            _vm.Volume = e.NewValue;
        }
        if (e.NewValue > 0 && Media.ShouldMute) Media.ShouldMute = false;
        UpdateMuteIcon();
    }

    private void OnMute(object? sender, EventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (_cast is { } cast) Guard(() => cast.SetMutedAsync(!cast.Muted));
        else Media.ShouldMute = !Media.ShouldMute;
        UpdateMuteIcon();
        ShowControls();
    }

    private void ChangeVolume(double by) => VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + by, 0, 1);

    private bool IsMuted => _cast?.Muted ?? Media.ShouldMute;

    private void UpdateMuteIcon() => MuteButton.Source = IconExtension.Create(
        IsMuted || VolumeSlider.Value == 0 ? "volume_off" : VolumeSlider.Value < 0.5 ? "volume_down" : "volume_up", Colors.White);

    // The window's brightness (the system's comes back when the player closes), kept for next time.
    private void OnBrightnessChanged(object? sender, ValueChangedEventArgs e)
    {
        if (!DeviceControls.HasBrightness || !_brightnessReady) return;
        DeviceControls.SetBrightness(e.NewValue);
        _vm.Brightness = e.NewValue;
    }

    private bool _brightnessReady;

    /// <summary>The brightness picked last time (or the system's on the slider), each time the player shows.</summary>
    private void ApplyBrightness()
    {
        if (!DeviceControls.HasBrightness) return;
        _brightnessReady = false;
        if (_vm.Brightness is { } kept) DeviceControls.SetBrightness(kept);
        BrightnessSlider.Value = _vm.Brightness ?? DeviceControls.GetBrightness();
        _brightnessReady = true;
    }

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

    /// <summary>Which third of the screen a tap is in: -1 the left, 1 the right, 0 the middle.</summary>
    private static int TapSide(object? sender, TappedEventArgs e) =>
        sender is View surface && surface.Width > 0 && e.GetPosition(surface) is { } at
            ? at.X < surface.Width / 3 ? -1 : at.X > surface.Width * 2 / 3 ? 1 : 0
            : 0;

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
        var from = _skipFrom ??= CurrentPosition;
        var total = CurrentTotal;
        var target = (_skipTarget ?? from) + TimeSpan.FromSeconds(seconds);
        if (total > TimeSpan.Zero && target > total) target = total - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        _skipTarget = target;

        var moved = (int)Math.Round((target - from).TotalSeconds);
        // Just the "+30s" on its side, as on YouTube, while the movie plays on. On a TV the frame they'll land on shows
        // above it too (there's no picture under a finger to go by there), and the presses get longer to add up (a
        // remote's are slower than taps).
        var tv = DeviceInfo.Idiom == DeviceIdiom.TV;
        Flash(moved < 0 ? $"−{-moved}s" : $"+{moved}s", left: moved < 0, showFor: tv ? 1300 : 1000, keepPreview: tv);
        if (tv) PreviewAt(target, large: false, _skipSlot, ShowSkipFrame, () => SkipPreview.IsVisible = false);
        PositionLabel.Text = TimeSpanConverter.Format(target);
        if (total > TimeSpan.Zero) Seek.Value = Math.Clamp(target / total, 0, 1);

        _skipDelay?.Cancel();
        var cts = _skipDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(tv ? 900 : 350, cts.Token);
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
    /// <param name="keepPreview">A skip's: the frame above it (see SkipAsync) stays; any other message has none.</param>
    private async void Flash(string text, bool? left = null, int showFor = 700, bool keepPreview = false)
    {
        SkipFlashText.Text = text;
        if (!keepPreview) SkipPreview.IsVisible = false;
        SkipFlash.HorizontalOptions = left switch { true => LayoutOptions.Start, false => LayoutOptions.End, null => LayoutOptions.Center };
        SkipFlash.IsVisible = true;
        SkipFlash.Opacity = 1;
        _flash?.Cancel();
        var cts = _flash = new CancellationTokenSource();
        try
        {
            await Task.Delay(showFor, cts.Token);
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
            ShowControls(); // up while it counts down, and hidden again in time after
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
    /// While the movie is still loading there's nothing to control yet: only the title and close button show, over the
    /// loading panel. Once it has played, buffering shows the panel but leaves the controls usable (pause, seek, skip…).
    /// </summary>
    private void SetLoading(bool loading)
    {
        Buffering.IsVisible = loading;
        var ready = Ready;
        TransportControls.IsVisible = SeekArea.IsVisible = ready;
        SubtitlesButton.IsVisible = QualityButton.IsVisible = SpeedButton.IsVisible = ready;
        CastButton.IsVisible = ready && CanCast;
        if (loading)
        {
            // The estimate is for where playback will carry on from.
            if (Media.Position > TimeSpan.Zero) _vm.StatusPosition = _vm.ActualPosition(Media.Position);
            _vm.StartLoadingStatus();
            ShowControls(); // keep the close button reachable (they stay up while it loads; see ShowControls)
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

    /// <summary>The controls can be used: it isn't loading, or it's only buffering after it has played.</summary>
    private bool Ready => _started || !Buffering.IsVisible;

    private void LoadSource()
    {
        if (_vm.SourceUrl is not { } url) return;
        _upNextCancelled = false;
        UpNextCard.IsVisible = false;
        if (_cast is not null)
        {
            // Another quality, the next episode...: on the TV too. A downloaded copy can't go there, so it plays here.
            if (_vm.CastMediaFrom(_vm.TranscodeOffset + _vm.ResumePosition) is { } media)
            {
                Guard(() => CastLoadAsync(media));
                return;
            }
            Guard(() => StopCastingAsync(resume: false));
        }
        _started = false;
        _playedTo = TimeSpan.Zero;
        _pendingResume = _vm.ResumePosition > TimeSpan.Zero;
        SetLoading(true);
        Media.Source = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaSource.FromUri(url) : MediaSource.FromFile(url);
        ShowControls();
        _vm.StartBufferWatch();
    }

    // ---------- The player's own buffer, on the timeline ----------

    private IDispatcherTimer? _playerBufferTimer;

    /// <summary>
    /// The timeline's third shade: how far ahead this device's player has the video loaded, read from the platform's
    /// player (MediaElement doesn't say). It grows while paused too, so it's looked at on a timer rather than on position
    /// changes.
    /// </summary>
    private void UpdatePlayerBufferTrack()
    {
        double fraction = 0;
        try
        {
            if (_cast is null && PlayerBufferedTo() is { } buffered && buffered > TimeSpan.Zero)
            {
                var total = _vm.TotalDuration(Media.Duration);
                if (total > TimeSpan.Zero) fraction = Math.Clamp(_vm.ActualPosition(buffered) / total, 0, 1);
            }
        }
        catch (Exception ex)
        {
            // The native player between sources, or already gone: nothing loaded to show.
            System.Diagnostics.Debug.WriteLine($"Player buffer: {ex.Message}");
        }
        PlayerBufferTrack.WidthRequest = Math.Max(0, SeekTrack.Width) * fraction;
    }

    /// <summary>How far the platform's player has the video loaded, in its own source's time (null where it can't say).</summary>
    private TimeSpan? PlayerBufferedTo()
    {
#if ANDROID
        return Media.Handler?.PlatformView is Android.Views.View view && FindPlayerView(view)?.Player is { } player
            ? TimeSpan.FromMilliseconds(player.BufferedPosition)
            : null;
#elif IOS || MACCATALYST
        if (FindAVPlayer()?.CurrentItem is not { } item) return null;
        var now = item.CurrentTime.Seconds;
        // The loaded stretch that's playing now (others are left from before a seek).
        foreach (var value in item.LoadedTimeRanges)
        {
            var range = value.CMTimeRangeValue;
            double start = range.Start.Seconds, end = start + range.Duration.Seconds;
            if (double.IsFinite(end) && now >= start - 1 && now <= end) return TimeSpan.FromSeconds(end);
        }
        return null;
#elif WINDOWS
        if (Media.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root
            || FindChild<Microsoft.UI.Xaml.Controls.MediaPlayerElement>(root)?.MediaPlayer?.PlaybackSession is not { } session)
            return null;
        var now = session.Position;
        foreach (var range in session.GetBufferedRanges())
            if (now >= range.Start - TimeSpan.FromSeconds(1) && now <= range.End) return range.End;
        return null;
#else
        return null;
#endif
    }

#if ANDROID
    private static AndroidX.Media3.UI.PlayerView? FindPlayerView(Android.Views.View view)
    {
        if (view is AndroidX.Media3.UI.PlayerView player) return player;
        if (view is Android.Views.ViewGroup group)
            for (var i = 0; i < group.ChildCount; i++)
                if (group.GetChildAt(i) is { } child && FindPlayerView(child) is { } found) return found;
        return null;
    }
#elif WINDOWS
    private static T? FindChild<T>(Microsoft.UI.Xaml.DependencyObject parent) where T : class
    {
        if (parent is T match) return match;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
#endif

    /// <summary>The timeline's second shade: how far the server has the video (see PlayerViewModel.BufferedTo).</summary>
    private void UpdateBufferedTrack() => BufferedTrack.WidthRequest = Math.Max(0, SeekTrack.Width) * Math.Clamp(_vm.BufferedTo, 0, 1);

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
    /// Only while it's still at the start, though: a seek can land and still be reported as failed (iOS does when
    /// buffering cuts it short), and retrying that one sent the movie back to the same place each time it buffered and
    /// played on, while its progress went unsaved (see OnPositionChanged), so it came back there next time too.
    /// </summary>
    private async Task TryResumeAsync()
    {
        if (!_pendingResume) return;
        _pendingResume = false;
        if (ResumeLanded()) return;
        if (!await TrySeekAsync(_vm.ResumePosition) && !ResumeLanded()) _pendingResume = true;
    }

    /// <summary>Playing from where it was left off (near enough), or well into the movie anyway.</summary>
    private bool ResumeLanded() =>
        Media.Position > TimeSpan.FromSeconds(10) || Media.Position >= _vm.ResumePosition - TimeSpan.FromSeconds(2);

    /// <summary>
    /// MediaElement's SeekTo, without the exceptions it throws while the player isn't ready. On iOS those are thrown
    /// inside the toolkit's own async void handler, where no catch here can reach them and they end the app, so there the
    /// AVPlayer is sought directly, and only once it's ReadyToPlay. False when it isn't ready (or the seek didn't land).
    /// </summary>
    private async Task<bool> TrySeekAsync(TimeSpan target)
    {
#if IOS || MACCATALYST
        var player = FindAVPlayer();
        if (player?.CurrentItem is null || player.Status != AVFoundation.AVPlayerStatus.ReadyToPlay) return false;
        return await player.SeekAsync(CoreMedia.CMTime.FromSeconds(target.TotalSeconds, 1000));
#else
        try
        {
            await Media.SeekTo(target);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
#endif
    }

#if IOS || MACCATALYST
    /// <summary>The AVPlayer behind MediaElement: its view hosts an AVPlayerViewController's view, whose next responder is the controller.</summary>
    private AVFoundation.AVPlayer? FindAVPlayer()
    {
        if (Media.Handler?.PlatformView is not UIKit.UIView host) return null;
        foreach (var view in host.Subviews)
            if (view.NextResponder is AVKit.AVPlayerViewController controller) return controller.Player;
        return null;
    }
#endif

    private TimeSpan _playedTo; // where the current source has got to, for the converted stream's restart (above)

    private void OnPositionChanged(TimeSpan elementPosition)
    {
        var total = _vm.TotalDuration(Media.Duration);
        var position = _vm.ActualPosition(elementPosition);
        // A converted stream can't be fetched again from the middle: when one breaks off, Android's player fetches it
        // again from its start, which is where the conversion started, not where it had got to, and it went back there
        // each time. The conversion is started again from where it had got to instead.
        if (_vm.IsTranscoding && _started && !_pendingResume && _playedTo - position > TimeSpan.FromSeconds(5))
        {
            System.Diagnostics.Debug.WriteLine($"Player: conversion restarted at {position}, carrying on from {_playedTo}");
            _vm.TrySeekTranscode(_playedTo);
            return;
        }
        _playedTo = position;
        ShowPosition(position, total);
        // Until the resume seek lands the position is the start of the file, not progress worth saving.
        if (!_pendingResume) _vm.OnPosition(elementPosition, Media.Duration, Media.CurrentState == MediaElementState.Playing);
    }

    /// <summary>The timeline, the times beside it and the up-next card, for <paramref name="position"/> of <paramref name="total"/>.</summary>
    private void ShowPosition(TimeSpan position, TimeSpan total)
    {
        if (!_seeking && _skipTarget is null)
        {
            if (total > TimeSpan.Zero) Seek.Value = Math.Clamp(position / total, 0, 1);
            PositionLabel.Text = TimeSpanConverter.Format(position);
        }
        DurationLabel.Text = total > TimeSpan.Zero ? $"-{TimeSpanConverter.Format(total - position)}" : "";
        // When it'll finish at this speed, as Plex shows.
        EndsAtLabel.Text = total > position ? $"Ends at {DateTime.Now + (total - position) / Math.Max(0.25, _vm.Speed):t}" : "";
        UpdateUpNext(position, total);
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
            if (_cast is not null || _vm.SourceUrl != source || Media.CurrentState != MediaElementState.Playing || Media.MediaWidth > 0) return;
            if (_vm.TryConvertAfterFailure(_vm.ActualPosition(Media.Position)))
                Flash("Converting it on the server…");
            else if (!_vm.IsTranscoding)
                Flash("Windows can't show this video's picture (try installing HEVC Video Extensions)");
        });
    }
#endif

    private void OnEnded() => Guard(FinishAsync);

    /// <summary>At the end: the next in the queue, or the player closes.</summary>
    private async Task FinishAsync()
    {
        await Report(PlayerState.Stopped, atEnd: true);
        if (_upNextCancelled || !_vm.TryPlayNext()) await Shell.Current.GoToAsync("..");
    }

    private Task Report(PlayerState state, bool atEnd = false)
    {
        if (_cast is not null)
        {
            var length = CurrentTotal;
            return _vm.ReportProgressAsync(atEnd ? length : CurrentPosition, length, state);
        }
        var total = _vm.TotalDuration(Media.Duration);
        var position = atEnd ? total : _vm.ActualPosition(Media.Position);
        return _vm.ReportAsync(position, Media.Duration, state);
    }

    // ---------- Controls ----------

    /// <summary>
    /// Shows the controls, and hides them again once left alone: after 4 seconds while it plays, 10 otherwise (paused,
    /// or the player saying it's buffering, which it does for moments while it plays: only hiding while Playing left
    /// them up for good then). Not while the timeline is being dragged or moved along, or the loading panel (with its
    /// Close) is up before it has played: then it looks again a second later, rather than giving up.
    /// </summary>
    private void ShowControls()
    {
        // The device's volume keys change it too: the slider shows where it is now.
        if (DeviceControls.HasDeviceVolume && !Controls.IsVisible) VolumeSlider.Value = DeviceControls.GetVolume();
        Controls.IsVisible = true;
        _hideTimer?.Stop();
        _shownAt = DateTime.UtcNow;
        _hideTimer = Dispatcher.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(1);
        _hideTimer.IsRepeating = true;
        _hideTimer.Tick += (sender, _) =>
        {
            var playing = _cast is { } cast ? cast.State == CastPlayerState.Playing : Media.CurrentState == MediaElementState.Playing;
            var after = TimeSpan.FromSeconds(playing ? 4 : 10);
            if (DateTime.UtcNow - _shownAt < after || _seeking || _scrubbing || !Ready) return;
            Controls.IsVisible = false;
            (sender as IDispatcherTimer)?.Stop();
        };
        _hideTimer.Start();
    }

    private DateTime _shownAt;

    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(350);
    private DateTime _lastTap;
    private int _tapSide, _tapStreak;
    private bool _controlsBeforeTap;

    /// <summary>
    /// A tap shows or hides the controls. Two in quick succession on the left or right third skip back or forward, as on
    /// YouTube and Netflix, and each further quick tap there skips on (the skips add up, see SkipAsync). Double-taps are
    /// told apart here rather than by a double-tap recognizer: the first tap shows or hides the controls, which cover the
    /// screen, so the second landed on the other view and neither ever saw two.
    /// </summary>
    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        var now = DateTime.UtcNow;
        var since = now - _lastTap;
        // The controls and the surface under them both listen; where a platform hands one tap to both, it counts once.
        if (since < TimeSpan.FromMilliseconds(60)) return;
        _lastTap = now;
        var side = TapSide(sender, e);
        if (since < DoubleTapWindow && side != 0 && side == _tapSide && Ready)
        {
            // The first tap's show or hide is undone: a double-tap only skips.
            if (_tapStreak++ == 0)
            {
                if (_controlsBeforeTap) ShowControls();
                else Controls.IsVisible = false;
            }
            Guard(() => SkipAsync(side < 0 ? -_vm.SkipBack : _vm.SkipForward));
            return;
        }
        _tapSide = side;
        _tapStreak = 0;
        _controlsBeforeTap = Controls.IsVisible;
        if (Controls.IsVisible && Ready) Controls.IsVisible = false; // while loading, Close stays reachable
        else ShowControls();
    }

    private void OnPlayPause(object? sender, EventArgs e)
    {
        if (_cast is { } cast)
        {
            if (cast.State is CastPlayerState.Paused or CastPlayerState.Idle) Guard(cast.PlayAsync);
            else Guard(cast.PauseAsync);
            ShowControls();
            return;
        }
        // Paused, stopped or not started: play. Anything else pauses: the player reports Buffering for moments while it
        // plays, and only pausing on Playing made a press then play on instead (the controls came up, the movie didn't stop).
        if (Media.CurrentState is MediaElementState.Paused or MediaElementState.Stopped or MediaElementState.None or MediaElementState.Failed)
            Media.Play();
        else
            Media.Pause();
        ShowControls();
    }

    private Task SeekToAsync(TimeSpan target)
    {
        var total = CurrentTotal;
        if (total > TimeSpan.Zero && target > total) target = total - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_cast is not null) return CastSeekAsync(target);
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

    // ---------- Casting ----------

    private CastSession? _cast;
    private CastMedia? _castMedia; // what the TV is playing
    private IDispatcherTimer? _castTimer;
    private bool? _castShowsPause;

    /// <summary>A TV casting to another TV makes no sense: the remote's for this one.</summary>
    private static bool CanCast => DeviceInfo.Idiom != DeviceIdiom.TV;

    /// <summary>Where the movie is, on the TV while casting, else here.</summary>
    private TimeSpan CurrentPosition => _cast is { } cast && _castMedia is { } media
        ? media.Offset + cast.Position
        : _vm.ActualPosition(Media.Position);

    /// <summary>The movie's full length, as far as whatever is playing it knows.</summary>
    private TimeSpan CurrentTotal => _cast is { } cast && _castMedia is { } media
        ? _vm.CastLength(cast.Duration, media)
        : _vm.TotalDuration(Media.Duration);

    private void Play()
    {
        if (_cast is { } cast) Guard(cast.PlayAsync);
        else Media.Play();
    }

    private void Pause()
    {
        if (_cast is { } cast) Guard(cast.PauseAsync);
        else Media.Pause();
    }

    private const string AirPlayOption = "AirPlay…";

    /// <summary>The cast button: the TVs on this network to play it on, or, while casting, stopping.</summary>
    private void OnCast(object? sender, EventArgs e) => Guard(async () =>
    {
        ShowControls();
        if (_cast is { } casting)
        {
            if (await Dialog.ChooseAsync($"Playing on {casting.Device.Name}", "Stop casting") == "Stop casting")
                await StopCastingAsync(resume: true);
            return;
        }
        if (_vm.CastMediaFrom(CurrentPosition) is null)
        {
            await Dialog.AlertAsync("Cast", "This is the copy downloaded to this device, which a TV can't reach. Play it from the server to cast it.");
            return;
        }

        Flash("Looking for TVs…", showFor: 2500);
        var devices = await CastDiscovery.FindAsync(TimeSpan.FromSeconds(3));
        var options = devices.Select(d => d.Name).ToList();
#if IOS || MACCATALYST
        options.Add(AirPlayOption); // Apple TVs and AirPlay TVs: the system's own picker
#endif
        if (options.Count == 0)
        {
            await Dialog.AlertAsync("Cast", "No Chromecast or Google TV found. Make sure it's on, and on the same Wi-Fi as this device.");
            return;
        }
        var choice = await Dialog.ChooseAsync("Play on", null, [.. options]);
#if IOS || MACCATALYST
        if (choice == AirPlayOption)
        {
            ShowAirPlayPicker();
            return;
        }
#endif
        if (devices.FirstOrDefault(d => d.Name == choice) is { } device) await StartCastingAsync(device);
    });

    /// <summary>
    /// Plays the movie on <paramref name="device"/> from where it is here. This device's player stops (the TV fetches the
    /// video from the server itself), and the controls control the TV until casting stops.
    /// </summary>
    private async Task StartCastingAsync(CastDevice device)
    {
        var position = CurrentPosition;
        if (_vm.CastMediaFrom(position) is not { } media) return;
        CastStatus.Text = $"Connecting to {device.Name}…";
        CastPanel.IsVisible = true;
        CastSession cast;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            cast = await CastSession.ConnectAsync(device, timeout.Token);
        }
        catch (Exception ex)
        {
            CastPanel.IsVisible = false;
            await Dialog.ErrorAsync($"Couldn't connect to {device.Name}. {ex.Message}", "Can't cast");
            return;
        }

        _cast = cast;
        cast.Ended += (_, reason) => Dispatcher.Dispatch(() => OnCastEnded(cast, reason));
        cast.Disconnected += (_, _) => Dispatcher.Dispatch(() =>
        {
            if (_cast != cast) return;
            Flash($"Lost {device.Name}");
            Guard(() => StopCastingAsync(resume: true));
        });

        // Nothing plays here meanwhile, nor is fetched: the TV does that.
        _vm.StopBufferWatch();
        _pendingResume = false;
        try
        {
            Media.Stop();
            Media.Source = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Player stop for casting: {ex}");
        }
        _started = true; // the controls are for the TV now, whatever this player was doing
        SetLoading(false);
        // The TV plays at normal speed and fits its own screen.
        SpeedButton.IsVisible = AspectButton.IsVisible = false;
        _vm.Speed = 1;
        SpeedButton.Text = "1x";
        CastButton.Source = IconExtension.Create("cast_connected", Colors.White);
        VolumeSlider.Value = cast.Volume;
        _castShowsPause = null;

        _castTimer?.Stop();
        _castTimer = Dispatcher.CreateTimer();
        _castTimer.Interval = TimeSpan.FromMilliseconds(500);
        _castTimer.Tick += (_, _) => OnCastTick();
        _castTimer.Start();

        await CastLoadAsync(media);
    }

    /// <summary>Sends <paramref name="media"/> to the TV; if it can't play the file as it is, the server's conversion.</summary>
    private async Task CastLoadAsync(CastMedia media)
    {
        if (_cast is not { } cast) return;
        _castMedia = media;
        _vm.StopLoadingStatus();
        _vm.StopBufferWatch();
        CastStatus.Text = $"Loading on {cast.Device.Name}…";
        try
        {
            await cast.LoadAsync(media.Url, media.ContentType, _vm.Title ?? "", _vm.PosterUrl, media.StartAt);
        }
        catch (CastException) when (_cast == cast && !media.Converted
                                    && _vm.CastMediaFrom(media.Offset + media.StartAt, convert: true) is { Converted: true } converted)
        {
            Flash("Converting it on the server…");
            await CastLoadAsync(converted);
        }
        catch (Exception ex) when (_cast == cast)
        {
            await Dialog.ErrorAsync(ex.Message, $"Can't play on {cast.Device.Name}");
            await StopCastingAsync(resume: true);
        }
    }

    /// <summary>Seeks on the TV; a conversion is started again from there, as it is here.</summary>
    private Task CastSeekAsync(TimeSpan target)
    {
        if (_cast is not { } cast || _castMedia is not { } media) return Task.CompletedTask;
        if (media.Converted)
            return _vm.CastMediaFrom(target, convert: true) is { } again ? CastLoadAsync(again) : Task.CompletedTask;
        return cast.SeekAsync(target);
    }

    private void OnCastEnded(CastSession cast, string reason)
    {
        if (_cast != cast) return;
        switch (reason)
        {
            case "FINISHED":
                Guard(FinishAsync);
                break;
            case "ERROR" when _castMedia is { Converted: false } media
                              && _vm.CastMediaFrom(media.Offset + cast.Position, convert: true) is { Converted: true } converted:
                // A file the TV took but couldn't decode after all (HEVC on an older Chromecast, say).
                Flash("Converting it on the server…");
                Guard(() => CastLoadAsync(converted));
                break;
            case "ERROR":
                Guard(async () =>
                {
                    await Dialog.ErrorAsync("The TV couldn't play this video.", $"Can't play on {cast.Device.Name}");
                    await StopCastingAsync(resume: true);
                });
                break;
            default:
                // Stopped on the TV, or another phone took it over: it carries on here.
                Flash($"Stopped on {cast.Device.Name}");
                Guard(() => StopCastingAsync(resume: true));
                break;
        }
    }

    /// <summary>The TV's position and state, shown as this device's own are.</summary>
    private void OnCastTick()
    {
        if (_cast is not { } cast || _castMedia is null) return;
        var total = CurrentTotal;
        var position = CurrentPosition;
        if (total > TimeSpan.Zero && position > total) position = total;
        ShowPosition(position, total);
        if (cast.State is CastPlayerState.Playing or CastPlayerState.Paused)
            _vm.OnCastPosition(position, total, cast.State == CastPlayerState.Playing);

        var showsPause = cast.State is not (CastPlayerState.Paused or CastPlayerState.Idle);
        if (showsPause != _castShowsPause)
        {
            _castShowsPause = showsPause;
            PlayPause.Source = IconExtension.Create(showsPause ? "pause" : "play_arrow", Colors.White, size: 40);
        }
        CastStatus.Text = cast.State switch
        {
            CastPlayerState.Loading or CastPlayerState.Buffering => $"Loading on {cast.Device.Name}…",
            CastPlayerState.Paused => $"Paused on {cast.Device.Name}",
            _ => $"Playing on {cast.Device.Name}",
        };
        UpdateMuteIcon();
    }

    /// <summary>
    /// Stops casting, and with <paramref name="resume"/> carries on here from where the TV got to.
    /// </summary>
    private async Task StopCastingAsync(bool resume)
    {
        if (_cast is not { } cast) return;
        var position = CurrentPosition;
        _cast = null;
        _castMedia = null;
        _castTimer?.Stop();
        CastPanel.IsVisible = false;
        CastButton.Source = IconExtension.Create("cast", Colors.White);
        SpeedButton.IsVisible = AspectButton.IsVisible = true;
        VolumeSlider.Value = DeviceControls.HasDeviceVolume ? DeviceControls.GetVolume() : _vm.Volume;
        UpdateMuteIcon();
        if (resume) _vm.RestartAt(position);
        try
        {
            await cast.StopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cast stop: {ex.Message}");
        }
        await cast.DisposeAsync();
    }

#if IOS || MACCATALYST
    private AVKit.AVRoutePickerView? _routePicker;

    /// <summary>
    /// The system's AirPlay picker. Apple's player sends the video to the Apple TV itself, so nothing else changes: the
    /// controls here go on controlling it. The picker has no way to open it but its button, so that's pressed.
    /// </summary>
    private void ShowAirPlayPicker()
    {
        if (Media.Handler?.PlatformView is not UIKit.UIView host) return;
        if (FindAVPlayer() is { } player) player.AllowsExternalPlayback = true;
        if (_routePicker is null)
        {
            // Where the cast button is, top right, so the picker's popover (on iPad and Mac) points there.
            _routePicker = new AVKit.AVRoutePickerView(new CoreGraphics.CGRect(host.Bounds.Width - 60, 12, 44, 44))
            {
                Alpha = 0.02f,
                AutoresizingMask = UIKit.UIViewAutoresizing.FlexibleLeftMargin,
            };
            host.AddSubview(_routePicker);
        }
        foreach (var view in _routePicker.Subviews)
            if (view is UIKit.UIButton button)
            {
                button.SendActionForControlEvents(UIKit.UIControlEvent.TouchUpInside);
                return;
            }
    }
#endif

    // ---------- Keyboard (Windows) ----------

    /// <summary>YouTube-style keys. Returns true when the key did something.</summary>
    private bool HandleKey(string key)
    {
        if (!Ready && key is not ("Escape" or "F")) return false;
        switch (key)
        {
            case "Space" or "K": OnPlayPause(null, EventArgs.Empty); break;
            case "Left" or "J": Guard(() => SkipAsync(-_vm.SkipBack)); break;
            case "Right" or "L": Guard(() => SkipAsync(_vm.SkipForward)); break;
            case "Up": ChangeVolume(0.1); Flash($"Volume {VolumeSlider.Value:P0}"); break;
            case "Down": ChangeVolume(-0.1); Flash($"Volume {VolumeSlider.Value:P0}"); break;
            case "M": ToggleMute(); Flash(IsMuted ? "Muted" : "Sound on"); break;
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
        if (e.Action != Android.Views.KeyEventActions.Down || !HandleRemoteKey(e.KeyCode, e.RepeatCount)) return false;
        _usedKeys.Add(e.KeyCode);
        return true;
    }

    /// <summary>
    /// The remote, as on Netflix or YouTube: with the controls hidden, left and right skip (held, they keep going), OK
    /// plays or pauses and up or down bring the controls up. With them up, the arrows move between them as usual (on
    /// the seek bar, left and right skip) and Back hides them before it closes the player. The media keys always work.
    /// </summary>
    private bool HandleRemoteKey(Android.Views.Keycode key, int repeat)
    {
        var ready = Ready;
        var hidden = !Controls.IsVisible;
        // Another slider being adjusted (the volume): its keys are its own (see TvNavigation.HandleSlider).
        if (!_scrubbing && Platform.CurrentActivity?.CurrentFocus is Android.Widget.AbsSeekBar adjusting && Phantom.Droid.DpadFocus.IsAdjusting(adjusting))
            return false;
        var ok = key is Android.Views.Keycode.DpadCenter or Android.Views.Keycode.Enter or Android.Views.Keycode.NumpadEnter or Android.Views.Keycode.ButtonA;
        switch (key)
        {
            // The timeline, as every slider on a TV: OK starts moving along it (see Scrub); then OK goes there, Back goes
            // back to where it was, and up and down stay put.
            case var _ when ok && _scrubbing:
                EndScrub(go: true);
                return true;
            case Android.Views.Keycode.Back when _scrubbing:
                EndScrub(go: false);
                return true;
            case Android.Views.Keycode.DpadUp or Android.Views.Keycode.DpadDown when _scrubbing:
                return true;
            case var _ when ok && ready && !hidden && IsFocused(Seek):
                StartScrub();
                return true;
            case Android.Views.Keycode.MediaPlayPause:
                OnPlayPause(null, EventArgs.Empty);
                return true;
            case Android.Views.Keycode.MediaPlay when ready:
                Play();
                ShowControls();
                return true;
            case Android.Views.Keycode.MediaPause when ready:
                Pause();
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
            case Android.Views.Keycode.DpadLeft or Android.Views.Keycode.DpadRight when _scrubbing:
                Scrub(key == Android.Views.Keycode.DpadLeft ? -1 : 1, repeat);
                return true;
            case Android.Views.Keycode.DpadLeft or Android.Views.Keycode.DpadRight when ready && hidden:
                // Skips add up while the movie plays on (see SkipAsync); the controls stay down.
                Guard(() => SkipAsync(key == Android.Views.Keycode.DpadLeft ? -_vm.SkipBack : _vm.SkipForward));
                return true;
            case Android.Views.Keycode.DpadCenter or Android.Views.Keycode.Enter or Android.Views.Keycode.NumpadEnter or Android.Views.Keycode.ButtonA when hidden:
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

    // ---------- Moving along the timeline with the remote ----------

    /// <summary>
    /// Left or right on the timeline, once OK has started moving along it (StartScrub): the movie is paused and the position moves along it, showing the frame there (over
    /// the whole picture as well as above the bar); held, it moves faster the longer it's held. Nothing is loaded until
    /// OK (see EndScrub). A slider moved by its keys never says it's being dragged, so it's done here.
    /// </summary>
    private void StartScrub()
    {
        if (CurrentTotal <= TimeSpan.Zero) return;
        _scrubbing = _seeking = true;
        _shownFrame = _fullFrame = null; // the whole-picture preview gets the first frame too, whatever the box above the bar had
        _scrubAt = _scrubFrom = CurrentPosition;
        _scrubResume = _cast is { } cast ? cast.State is not (CastPlayerState.Paused or CastPlayerState.Idle)
            : Media.CurrentState is not (MediaElementState.Paused or MediaElementState.Stopped);
        Pause();
        if (Seek.Handler?.PlatformView is Android.Views.View seek) Phantom.Droid.DpadFocus.SetAdjusting(seek);
        ShowControls();
    }

    private void Scrub(int direction, int repeat)
    {
        var total = CurrentTotal;
        if (total <= TimeSpan.Zero) return;
        var step = TimeSpan.FromSeconds(repeat switch { < 4 => 10, < 12 => 30, < 24 => 60, _ => 180 });
        var at = _scrubAt + direction * step;
        _scrubAt = at < TimeSpan.Zero ? TimeSpan.Zero : at > total - TimeSpan.FromSeconds(1) ? total - TimeSpan.FromSeconds(1) : at;
        ShowControls();
        PositionLabel.Text = TimeSpanConverter.Format(_scrubAt);
        Seek.Value = Math.Clamp(_scrubAt / total, 0, 1); // shows the frame there (OnSeekValueChanged)
    }

    /// <summary>Done moving along the timeline: to where it got to (or back where it was), playing again if it was.</summary>
    private void EndScrub(bool go)
    {
        if (!_scrubbing) return;
        _scrubbing = _seeking = false;
        Phantom.Droid.DpadFocus.SetAdjusting(null);
        HidePreviews();
        var total = CurrentTotal;
        if (go) Guard(() => SeekToAsync(_scrubAt));
        else if (total > TimeSpan.Zero)
        {
            Seek.Value = Math.Clamp(_scrubFrom / total, 0, 1);
            PositionLabel.Text = TimeSpanConverter.Format(_scrubFrom);
        }
        if (_scrubResume) Play();
        ShowControls();
    }

    private void FocusPlayPause() => Dispatcher.Dispatch(() => (TransportControls.IsVisible ? PlayPause : (View)SubtitlesButton).Focus());

    /// <summary>
    /// The controls come up by themselves too (loading, a skip, the end): on a TV the remote then goes to them unless
    /// it's already on one. Left on the video, which fills the screen, the arrows had nothing beyond it to go to until
    /// the controls hid again.
    /// </summary>
    private void FocusControlsIfAway()
    {
        if (DeviceInfo.Idiom != DeviceIdiom.TV || !Controls.IsVisible) return;
        if (Controls.Handler?.PlatformView is Android.Views.ViewGroup controls && controls.FindFocus() is not null) return;
        FocusPlayPause();
    }

    private static bool IsFocused(View view) => view.Handler?.PlatformView is Android.Views.View { IsFocused: true };

    private void OnControlsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsVisible)) FocusControlsIfAway();
    }
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
        ApplyBrightness();
        _playerBufferTimer?.Stop();
        _playerBufferTimer = Dispatcher.CreateTimer();
        _playerBufferTimer.Interval = TimeSpan.FromMilliseconds(500);
        _playerBufferTimer.Tick += (_, _) => UpdatePlayerBufferTrack();
        _playerBufferTimer.Start();
#if WINDOWS
        HookKeys();
#elif ANDROID
        MainActivity.KeyHandler = OnRemoteKey;
        Controls.PropertyChanged -= OnControlsChanged;
        Controls.PropertyChanged += OnControlsChanged;
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
        var total = CurrentTotal;
        // Some platforms also call the end of a drag a tap: the drag has seeked already, and a second seek would start
        // loading all over again (restarting a conversion, even).
        if (DateTime.UtcNow - _dragEnded < TimeSpan.FromMilliseconds(500)) return;
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

    /// <summary>One kind of preview being fetched (the small one, the big one, the skip's): only its latest is.</summary>
    private sealed class PreviewSlot
    {
        public CancellationTokenSource? Fetch;
        public int? Step;

        public void Cancel()
        {
            Fetch?.Cancel();
            Step = null;
        }
    }

    private readonly PreviewSlot _smallSlot = new(), _largeSlot = new(), _skipSlot = new();
    private byte[]? _shownFrame, _fullFrame;
    private bool _fullIsLarge;
    private TimeSpan _previewTarget;

    // While dragging (or moving along with the remote): show where it would land, the frame there in the box above the
    // bar, and over the whole picture too, sharper (a big frame) once it rests somewhere. Only one frame of each size
    // is ever being fetched: moving on cancels the request.
    private void OnSeekValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (!_seeking || !double.IsFinite(e.NewValue)) return;
        var total = CurrentTotal;
        if (total <= TimeSpan.Zero) return;
        var at = _scrubbing ? _scrubAt : total * Math.Clamp(e.NewValue, 0, 1);

        SeekPreview.IsVisible = true;
        SeekPreviewTime.Text = TimeSpanConverter.Format(at);
        var half = SeekPreview.WidthRequest / 2;
        SeekPreview.TranslationX = Math.Clamp(Seek.Width * e.NewValue - half, 0, Math.Max(0, Seek.Width - SeekPreview.WidthRequest));

        _previewTarget = at;
        SchedulePreview();
    }

    private bool _previewPending;

    /// <summary>
    /// The pictures follow the position at most every 120ms, for where it has got to by then: decoding frames for each
    /// move (a held remote button moves twenty times a second) kept the UI thread too busy to move smoothly.
    /// </summary>
    private void SchedulePreview()
    {
        if (_previewPending) return;
        _previewPending = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), () =>
        {
            _previewPending = false;
            if (!_seeking) return;
            var at = _previewTarget;
            PreviewAt(at, large: false, _smallSlot, ShowPreviewFrame, () => SeekPreviewImage.IsVisible = false);
            if (_vm.LargePreviewFrame(at) is { } big) ShowFullFrame(big, large: true);
            else PreviewAt(at, large: true, _largeSlot, big => ShowFullFrame(big, large: true), () => { }, wait: 300);
        });
    }

    /// <summary>
    /// The preview at <paramref name="at"/>, given to <paramref name="show"/>. A small one straight from those already
    /// here (all of them, once the server has made them), the exact one or the nearest while the exact one is fetched; a
    /// big one only once fetched, after <paramref name="wait"/> ms in the same place. <paramref name="none"/> when there's
    /// none at all.
    /// </summary>
    private async void PreviewAt(TimeSpan at, bool large, PreviewSlot slot, Action<byte[]> show, Action none, int wait = 80)
    {
        byte[]? frame = null;
        if (!large)
        {
            frame = _vm.PreviewFrame(at, out var exact);
            if (frame is not null) show(frame);
            if (exact) return;
        }

        if (!_vm.CanFetchPreview)
        {
            if (frame is null) none();
            return;
        }
        var step = PlayerViewModel.PreviewStep(at);
        if (step == slot.Step) return; // same frame (they're 10 s apart), already on its way
        slot.Fetch?.Cancel();
        slot.Step = step;
        var cts = slot.Fetch = new CancellationTokenSource();
        try
        {
            await Task.Delay(wait, cts.Token);
            var jpeg = await _vm.FetchPreviewFrameAsync(at, large, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (slot.Step == step) slot.Step = null; // a miss is asked again next time
            // The frame on show stays up while the next one loads; only hide it when there's none at all.
            if (jpeg is not null) show(jpeg);
            else if (frame is null) none();
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

    private byte[]? _skipFrame;

    private void ShowSkipFrame(byte[] jpeg)
    {
        SkipPreview.IsVisible = true;
        if (ReferenceEquals(jpeg, _skipFrame)) return;
        _skipFrame = jpeg;
        SkipPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(jpeg));
    }

    private void ShowPreviewFrame(byte[] jpeg)
    {
        if (!_seeking) return;
        SeekPreviewImage.IsVisible = true;
        if (!ReferenceEquals(jpeg, _shownFrame)) // the same picture: no need to decode it again
        {
            _shownFrame = jpeg;
            SeekPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(jpeg));
        }
        // Over the whole picture too, until the big one for here comes.
        if (!(_fullIsLarge && ReferenceEquals(_fullFrame, _vm.LargePreviewFrame(_previewTarget)))) ShowFullFrame(jpeg, large: false);
    }

    private void ShowFullFrame(byte[] jpeg, bool large)
    {
        if (!_seeking) return;
        // A big frame that came for where the drag has since left isn't shown.
        if (large && !ReferenceEquals(jpeg, _vm.LargePreviewFrame(_previewTarget))) return;
        FullPreview.IsVisible = true;
        if (ReferenceEquals(jpeg, _fullFrame)) return;
        _fullFrame = jpeg;
        _fullIsLarge = large;
        FullPreview.Source = ImageSource.FromStream(() => new MemoryStream(jpeg));
    }

    /// <summary>Done moving along the timeline: the previews go.</summary>
    private void HidePreviews()
    {
        _smallSlot.Cancel();
        _largeSlot.Cancel();
        _shownFrame = _fullFrame = null;
        _fullIsLarge = false;
        SeekPreview.IsVisible = false;
        FullPreview.IsVisible = false;
    }

    private DateTime _dragEnded;

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
        _dragEnded = DateTime.UtcNow;
        HidePreviews();
        Guard(() => SeekToAsync(CurrentTotal * Seek.Value));
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
            _vm.ChangeQuality(quality, CurrentPosition);
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
        DeviceControls.SetBrightness(null); // the system's again outside the player
        _hideTimer?.Stop();
        _playerBufferTimer?.Stop();
        _skipDelay?.Cancel();
        _scrubbing = false;
        HidePreviews();
        _skipSlot.Cancel();
        _vm.StopLoadingStatus();
        _vm.StopPreviewFrames();
        _vm.StopBufferWatch();
        SetFullscreen(false);
        SetSystemBarsHidden(false);
#if WINDOWS
        if (_keyRoot is not null && _keyHandler is not null) _keyRoot.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, _keyHandler);
#elif ANDROID
        if (MainActivity.KeyHandler == (Func<Android.Views.KeyEvent, bool>)OnRemoteKey) MainActivity.KeyHandler = null;
        Controls.PropertyChanged -= OnControlsChanged;
        _usedKeys.Clear();
#endif
        // The position is read now, then the player is stopped straight away: once this method awaits anything the page
        // is on its way out, and stopping a MediaElement whose native player has gone throws (which ended the app).
        var report = Report(PlayerState.Stopped);
        if (_cast is not null) _ = StopCastingAsync(resume: false); // closing the player stops it on the TV too
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
