using System.Collections.Specialized;
using System.ComponentModel;
using Phantom.Services;

namespace Phantom.Controls;

/// <summary>
/// An episode's Download button: its icon and label say whether the episode is on this device (downloaded,
/// downloading and how far along, paused or failed), and follow the download as it goes.
/// </summary>
public class EpisodeDownloadButton : Button
{
    public static readonly BindableProperty DownloadsProperty =
        BindableProperty.Create(nameof(Downloads), typeof(DownloadManager), typeof(EpisodeDownloadButton),
            propertyChanged: (b, o, n) => ((EpisodeDownloadButton)b).Watch(o as DownloadManager, n as DownloadManager));

    public static readonly BindableProperty EpisodeIdProperty =
        BindableProperty.Create(nameof(EpisodeId), typeof(int), typeof(EpisodeDownloadButton), 0,
            propertyChanged: (b, _, _) => ((EpisodeDownloadButton)b).Refresh());

    public DownloadManager? Downloads { get => (DownloadManager?)GetValue(DownloadsProperty); set => SetValue(DownloadsProperty, value); }
    public int EpisodeId { get => (int)GetValue(EpisodeIdProperty); set => SetValue(EpisodeIdProperty, value); }

    private OfflineDownload? _item;
    private (string Glyph, string Color) _look;
    private bool _onScreen;

    public EpisodeDownloadButton()
    {
        // Only listens while on screen, so rows of a closed page aren't kept alive by the downloads list.
        Loaded += (_, _) =>
        {
            _onScreen = true;
            Watch(null, Downloads);
        };
        Unloaded += (_, _) =>
        {
            Watch(Downloads, null);
            _onScreen = false;
        };
        Refresh();
    }

    private void Watch(DownloadManager? before, DownloadManager? after)
    {
        if (before is not null) before.Items.CollectionChanged -= OnItemsChanged;
        if (after is not null && _onScreen)
        {
            after.Items.CollectionChanged -= OnItemsChanged;
            after.Items.CollectionChanged += OnItemsChanged;
        }
        Refresh();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OfflineDownload.Status) or nameof(OfflineDownload.DownloadedBytes) or nameof(OfflineDownload.TotalBytes)) Update();
    }

    private void Refresh()
    {
        var item = _onScreen && EpisodeId != 0 ? Downloads?.FindEpisode(EpisodeId) : null;
        if (!ReferenceEquals(item, _item))
        {
            if (_item is not null) _item.PropertyChanged -= OnItemChanged;
            _item = item;
            if (_item is not null) _item.PropertyChanged += OnItemChanged;
        }
        Update();
    }

    private void Update()
    {
        var resources = Application.Current!.Resources;
        var (glyph, colorKey, text) = _item?.Status switch
        {
            null => ("download", "TextPrimary", "Download"),
            OfflineStatus.Completed => ("download_done", "PrimaryLight", "Downloaded"),
            OfflineStatus.Paused => ("pause_circle", "TextSecondary", "Paused"),
            OfflineStatus.Failed => ("error", "Danger", "Failed"),
            OfflineStatus.Downloading when _item!.TotalBytes > 0 => ("downloading", "PrimaryLight", $"{_item.Progress:0%}"),
            _ => ("downloading", "PrimaryLight", "Waiting"),
        };
        // The icon only changes with the state, not with each step of progress.
        if ((glyph, colorKey) != _look)
        {
            _look = (glyph, colorKey);
            var color = (Color)resources[colorKey];
            ImageSource = IconExtension.Create(glyph, color, size: 18);
            TextColor = color;
        }
        if (Text != text) Text = text;
        SemanticProperties.SetDescription(this, $"Download to this device: {text}");
        ShowProgress(this, _item is { CanPause: true } running ? running.Progress : null, ref _running);
    }

    private bool _running;

    /// <summary>
    /// A button's progress look (see ProgressLook): filled to <paramref name="progress"/> with a pulsing border while
    /// something runs, its usual look once it's done (null).
    /// </summary>
    internal static void ShowProgress(Button button, double? progress, ref bool running)
    {
        var resources = Application.Current!.Resources;
        if (progress is { } p)
        {
            // A pill's rounded ends hide the first and last few percent of a fill drawn edge to edge (5% of a wide
            // episode button is all inside its curve): the fill runs between the curves, so any progress shows.
            if (button.Width > 0 && p > 0 && p < 1)
            {
                var curve = Math.Min(button.CornerRadius, button.Height / 2) / button.Width;
                p = curve + p * (1 - 2 * curve);
            }
            button.Background = ProgressLook.Fill(p, (Color)resources["SurfaceRaised"]);
            button.BorderWidth = 1.5;
            if (!running) ProgressLook.Pulse(button, c => button.BorderColor = c, on: true);
            running = true;
        }
        else if (running)
        {
            button.ClearValue(BackgroundProperty);
            button.BorderWidth = 0;
            ProgressLook.Pulse(button, _ => { }, on: false);
            running = false;
        }
    }
}
