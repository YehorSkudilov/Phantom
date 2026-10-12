using Phantom.Services;

namespace Phantom.Controls;

/// <summary>
/// An episode's Save on server button: says whether the server has it ("On server"), is saving it (filling as it goes,
/// like the Download button), or hasn't got it, and follows it as it changes.
/// </summary>
public class EpisodeServerButton : Button
{
    public static readonly BindableProperty TrackerProperty =
        BindableProperty.Create(nameof(Tracker), typeof(ServerDownloadTracker), typeof(EpisodeServerButton),
            propertyChanged: (b, o, n) => ((EpisodeServerButton)b).Watch(o as ServerDownloadTracker, n as ServerDownloadTracker));

    public static readonly BindableProperty EpisodeIdProperty =
        BindableProperty.Create(nameof(EpisodeId), typeof(int), typeof(EpisodeServerButton), 0,
            propertyChanged: (b, _, _) => ((EpisodeServerButton)b).Update());

    public ServerDownloadTracker? Tracker { get => (ServerDownloadTracker?)GetValue(TrackerProperty); set => SetValue(TrackerProperty, value); }
    public int EpisodeId { get => (int)GetValue(EpisodeIdProperty); set => SetValue(EpisodeIdProperty, value); }

    private bool _onScreen;
    private bool _running;
    private bool? _on;

    public EpisodeServerButton()
    {
        // Only listens while on screen, so rows of a closed page aren't kept alive by the tracker.
        Loaded += (_, _) =>
        {
            _onScreen = true;
            Watch(null, Tracker);
        };
        Unloaded += (_, _) =>
        {
            Watch(Tracker, null);
            _onScreen = false;
        };
        Update();
    }

    private void Watch(ServerDownloadTracker? before, ServerDownloadTracker? after)
    {
        if (before is not null) before.Changed -= OnChanged;
        if (after is not null && _onScreen)
        {
            after.Changed -= OnChanged;
            after.Changed += OnChanged;
        }
        Update();
    }

    private void OnChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var d = EpisodeId == 0 ? null : Tracker?.ForEpisode(EpisodeId);
        var text = ServerDownloadTracker.Label(d);
        if (Text != text) Text = text;
        var on = d is not null;
        if (on != _on)
        {
            _on = on;
            var color = (Color)Application.Current!.Resources[on ? "PrimaryLight" : "TextPrimary"];
            ImageSource = IconExtension.Create(on ? "cloud_done" : "cloud_download", color, size: 18);
            TextColor = color;
        }
        SemanticProperties.SetDescription(this, $"Save on the server: {text}");
        EpisodeDownloadButton.ShowProgress(this, ServerDownloadTracker.IsRunning(d) ? d!.Progress / 100 : null, ref _running);
    }
}
