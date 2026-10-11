using System.Collections.Specialized;
using System.ComponentModel;
using Phantom.Services;

namespace Phantom.Controls;

/// <summary>
/// An episode row's download button: its icon says whether the episode is on this device (downloaded, downloading,
/// paused or failed), and follows the download as it goes.
/// </summary>
public class EpisodeDownloadButton : ImageButton
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
        if (e.PropertyName is nameof(OfflineDownload.Status)) Update();
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
            null => ("download", "TextSecondary", "Download"),
            OfflineStatus.Completed => ("download_done", "PrimaryLight", "Downloaded"),
            OfflineStatus.Paused => ("pause_circle", "TextSecondary", "Download paused"),
            OfflineStatus.Failed => ("error", "Danger", "Download failed"),
            _ => ("downloading", "PrimaryLight", "Downloading"),
        };
        Source = IconExtension.Create(glyph, (Color)resources[colorKey]);
        SemanticProperties.SetDescription(this, text);
        ToolTipProperties.SetText(this, text);
    }
}
