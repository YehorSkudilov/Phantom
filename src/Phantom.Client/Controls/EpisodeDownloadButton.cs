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
    }
}
