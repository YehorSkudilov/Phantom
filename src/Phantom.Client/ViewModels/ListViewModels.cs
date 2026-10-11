using Phantom.Controls;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

/// <summary>Shared list screen for Watchlist, History and a single Collection.</summary>
public partial class MovieListViewModel(PhantomApi api) : BaseViewModel, IQueryAttributable
{
    private string _kind = "watchlist";
    private int _id;

    public ObservableList<MovieSummaryDto> Movies { get; } = [];

    [ObservableProperty] private string? subtitle;
    [ObservableProperty] private bool isEmpty;

    public string EmptyText => _kind switch
    {
        "history" => "Nothing watched yet.",
        "collection" => "This collection is empty.",
        _ => "Nothing on your watchlist yet. Tap Watchlist on a movie to save it for later.",
    };

    /// <summary>Picks what this list shows without loading it yet (the Library tab loads it when its sub-tab opens).</summary>
    public MovieListViewModel Use(string kind)
    {
        _kind = kind;
        OnPropertyChanged(nameof(EmptyText));
        return this;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _kind = query.TryGetValue("kind", out var k) ? k.ToString()! : "watchlist";
        _id = query.TryGetValue("id", out var id) && int.TryParse(id.ToString(), out var i) ? i : 0;
        OnPropertyChanged(nameof(EmptyText));
        LoadCommand.Execute(null);
    }

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        IReadOnlyList<MovieSummaryDto> items;
        switch (_kind)
        {
            case "history":
                Title = "Recently Watched";
                items = await api.HistoryAsync();
                break;
            case "collection":
                var c = await api.CollectionAsync(_id);
                Title = c.Name;
                Subtitle = c.Description;
                items = c.Movies;
                break;
            default:
                Title = "Watchlist";
                items = await api.WatchlistAsync();
                break;
        }
        Movies.Sync(items);
        IsEmpty = Movies.Count == 0;
    });

    [RelayCommand]
    private Task PlayAll() => Movies.Count == 0
        ? Task.CompletedTask
        : GoToAsync($"{nameof(Views.PlayerPage)}?id={Movies[0].Id}&queue={string.Join(',', Movies.Select(m => m.Id))}");
}

public partial class CollectionsViewModel(PhantomApi api) : BaseViewModel
{
    public ObservableList<CollectionDto> Collections { get; } = [];

    [ObservableProperty] private bool isEmpty;

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        Collections.Sync(await api.CollectionsAsync());
        IsEmpty = Collections.Count == 0;
    });

    [RelayCommand]
    private Task Open(CollectionDto c) => GoToAsync($"{nameof(Views.MovieListPage)}?kind=collection&id={c.Id}");
}

public partial class PlaylistsViewModel(PhantomApi api) : BaseViewModel
{
    public ObservableList<PlaylistDto> Playlists { get; } = [];

    [ObservableProperty] private bool isEmpty;

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        Playlists.Sync(await api.PlaylistsAsync());
        IsEmpty = Playlists.Count == 0;
    });

    [RelayCommand]
    private Task Create() => RunAsync(async () =>
    {
        var name = await Dialog.PromptAsync("New playlist", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        Playlists.Add(await api.CreatePlaylistAsync(name));
        IsEmpty = false;
    });

    [RelayCommand]
    private Task Delete(PlaylistDto p) => RunAsync(async () =>
    {
        if (!await Dialog.ConfirmAsync("Delete playlist", $"Delete \"{p.Name}\"?", "Delete", destructive: true)) return;
        await api.DeletePlaylistAsync(p.Id);
        Playlists.Remove(p);
        IsEmpty = Playlists.Count == 0;
    });

    [RelayCommand]
    private Task Open(PlaylistDto p) => GoToAsync($"{nameof(Views.PlaylistPage)}?id={p.Id}");
}

public partial class PlaylistViewModel(PhantomApi api) : BaseViewModel, IQueryAttributable
{
    private int _id;

    public ObservableList<MovieSummaryDto> Items { get; } = [];

    [ObservableProperty] private string summary = "";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _id = int.Parse(query["id"].ToString()!);
        LoadCommand.Execute(null);
    }

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        var p = await api.PlaylistAsync(_id);
        Title = p.Name;
        Items.Sync(p.Items);
        UpdateSummary();
    });

    private void UpdateSummary() =>
        Summary = $"{Items.Count} movies · {MinutesConverter.Format(Items.Sum(i => i.RuntimeMinutes ?? 0))}";

    [RelayCommand]
    private Task PlayAll() => PlayFrom(Items.Select(i => i.Id).ToList());

    [RelayCommand]
    private Task Shuffle() => PlayFrom(Items.Select(i => i.Id).OrderBy(_ => Random.Shared.Next()).ToList());

    private Task PlayFrom(List<int> ids) => ids.Count == 0
        ? Task.CompletedTask
        : GoToAsync($"{nameof(Views.PlayerPage)}?id={ids[0]}&queue={string.Join(',', ids)}");

    [RelayCommand]
    private Task Remove(MovieSummaryDto m) => RunAsync(async () =>
    {
        await api.RemoveFromPlaylistAsync(_id, m.Id);
        Items.Remove(m);
        UpdateSummary();
    }, showBusy: false);

    [RelayCommand]
    private Task MoveUp(MovieSummaryDto m) => Move(m, -1);

    [RelayCommand]
    private Task MoveDown(MovieSummaryDto m) => Move(m, +1);

    private Task Move(MovieSummaryDto m, int delta) => RunAsync(async () =>
    {
        var i = Items.IndexOf(m);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        Items.Move(i, j);
        await api.ReorderPlaylistAsync(_id, Items.Select(x => x.Id).ToList());
    }, showBusy: false);
}

/// <summary>Movies saved on this device.</summary>
public partial class DownloadsViewModel(DownloadManager manager, SettingsService settings, ServerDownloadsViewModel server) : BaseViewModel
{
    public IReadOnlyList<string> Tabs { get; } = ["On this device", "On the server"];

    /// <summary>0: downloads on this device; 1: movies the server is downloading or keeps.</summary>
    [ObservableProperty] private int selectedTab;

    public ServerDownloadsViewModel Server => server;

    public ObservableCollection<OfflineDownload> Items => manager.Items;

    [ObservableProperty] private string storageText = "";
    [ObservableProperty] private bool wifiOnly = settings.DownloadOnWifiOnly;

    partial void OnWifiOnlyChanged(bool value) => settings.DownloadOnWifiOnly = value;

    [RelayCommand]
    public async Task Refresh()
    {
        StorageText = $"{Items.Count(i => i.IsComplete)} of {Items.Count} ready · {BytesConverter.Format(manager.UsedBytes)} used";
        await EndRefreshAsync();
    }

    [RelayCommand]
    private Task Play(OfflineDownload item) => item.IsComplete
        ? GoToAsync(item.EpisodeId is { } episode
            ? $"{nameof(Views.PlayerPage)}?episode={episode}&offline=true&resume={item.OfflinePositionMs > 0}"
            : $"{nameof(Views.PlayerPage)}?id={item.MovieId}&offline=true&resume={item.OfflinePositionMs > 0}")
        : Task.CompletedTask;

    [RelayCommand]
    private void Pause(OfflineDownload item) => manager.Pause(item);

    [RelayCommand]
    private void Resume(OfflineDownload item) => manager.Resume(item);

    /// <summary>Cancels an unfinished download, or deletes a finished one from the device.</summary>
    [RelayCommand]
    private async Task Delete(OfflineDownload item)
    {
        var confirmed = item.IsComplete
            ? await Dialog.ConfirmAsync("Delete download", $"Delete {item.Title} from this device? You can still stream it.", "Delete", "Keep", destructive: true)
            : await Dialog.ConfirmAsync("Cancel download", $"Stop downloading {item.Title} and remove what's downloaded so far?", "Cancel download", "Keep downloading", destructive: true);
        if (!confirmed) return;
        await manager.DeleteAsync(item);
        Refresh();
    }

    [RelayCommand]
    private Task Open(OfflineDownload item) => GoToAsync(item.ShowId is { } show
        ? $"{nameof(Views.ShowDetailPage)}?id={show}"
        : $"{nameof(Views.MovieDetailPage)}?id={item.MovieId}");
}
