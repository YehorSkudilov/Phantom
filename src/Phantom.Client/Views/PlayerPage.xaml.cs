using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using Phantom.Client.Services;
using Phantom.Client.ViewModels;
using Phantom.Shared;

namespace Phantom.Client.Views;

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

        BackButton.Text = $"↺ {_vm.SkipBack}";
        ForwardButton.Text = $"{_vm.SkipForward} ↻";
    }

    private void LoadSource()
    {
        if (_vm.SourceUrl is not { } url) return;
        _pendingResume = _vm.ResumePosition > TimeSpan.Zero;
        Buffering.IsVisible = true;
        Media.Source = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaSource.FromUri(url) : MediaSource.FromFile(url);
        ShowControls();
    }

    private async void OnOpened()
    {
        Buffering.IsVisible = false;
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
        Buffering.IsVisible = state is MediaElementState.Buffering or MediaElementState.Opening;
        PlayPause.Text = state == MediaElementState.Playing ? "❚❚" : "▶";
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

    private async void OnSeekCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
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
        await Report(PlayerState.Stopped);
        Media.Stop();
        Media.Handler?.DisconnectHandler();
    }
}
