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
        "history" => "What you watch shows up here, newest first, so it's easy to find again.",
        "collection" => "None of this collection's movies are in the library yet.",
        _ => "Tap Watchlist on a movie or show to save it for later. It'll be waiting here.",
    };

    public string EmptyTitle => _kind switch
    {
        "history" => "Nothing watched yet",
        "collection" => "This collection is empty",
        _ => "Your watchlist is empty",
    };

    public string EmptyGlyph => _kind switch
    {
        "history" => "history",
        "collection" => "collections_bookmark",
        _ => "bookmark_add",
    };

    protected override bool ShowsOfflineState => true;

    /// <summary>Picks what this list shows without loading it yet (the My Stuff tab loads it when its tab opens).</summary>
    public MovieListViewModel Use(string kind)
    {
        _kind = kind;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyGlyph));
        return this;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _kind = query.TryGetValue("kind", out var k) ? k.ToString()! : "watchlist";
        _id = query.TryGetValue("id", out var id) && int.TryParse(id.ToString(), out var i) ? i : 0;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyGlyph));
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
    protected override bool ShowsOfflineState => true;

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
    protected override bool ShowsOfflineState => true;

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

    // Each entry's key ("m12" movie, "s4" show, "e99" episode), in the same order as Items.
    private List<string> _keys = [];

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
        _keys = p.Keys?.ToList() ?? p.Items.Select(i => $"m{i.Id}").ToList();
        Items.ReplaceAll(p.Items);
        UpdateSummary();
    });

    private void UpdateSummary() =>
        Summary = $"{Items.Count} {(Items.Count == 1 ? "title" : "titles")} · {MinutesConverter.Format(Items.Sum(i => i.RuntimeMinutes ?? 0))}";

    private string KeyOf(MovieSummaryDto item) => Items.IndexOf(item) is var i and >= 0 && i < _keys.Count ? _keys[i] : $"m{item.Id}";

    /// <summary>Plays the playlist's movies in order (shows and episodes open from their own rows).</summary>
    [RelayCommand]
    private Task PlayAll() => PlayFrom(MovieIds().ToList());

    [RelayCommand]
    private Task Shuffle() => PlayFrom(MovieIds().OrderBy(_ => Random.Shared.Next()).ToList());

    private IEnumerable<int> MovieIds() => _keys.Where(k => k.StartsWith('m')).Select(k => int.Parse(k[1..]));

    private async Task PlayFrom(List<int> ids)
    {
        if (ids.Count > 0)
        {
            await GoToAsync($"{nameof(Views.PlayerPage)}?id={ids[0]}&queue={string.Join(',', ids)}");
            return;
        }
        // Only episodes and shows: the first episode plays, or the first show opens.
        if (_keys.FirstOrDefault(k => k.StartsWith('e')) is { } episode)
            await GoToAsync($"{nameof(Views.PlayerPage)}?episode={episode[1..]}");
        else if (Items.FirstOrDefault() is { } first)
            await OpenMovieCommand.ExecuteAsync(first);
    }

    /// <summary>An entry tapped: an episode plays; a movie or show opens its page.</summary>
    [RelayCommand]
    private Task OpenEntry(MovieSummaryDto? item) => item is null ? Task.CompletedTask
        : KeyOf(item) is ['e', ..] key ? GoToAsync($"{nameof(Views.PlayerPage)}?episode={key[1..]}")
        : OpenMovieCommand.ExecuteAsync(item);

    [RelayCommand]
    private Task Remove(MovieSummaryDto m) => RunAsync(async () =>
    {
        var i = Items.IndexOf(m);
        if (i < 0) return;
        await api.RemovePlaylistEntriesAsync(_id, [KeyOf(m)]);
        Items.RemoveAt(i);
        if (i < _keys.Count) _keys.RemoveAt(i);
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
        if (i < _keys.Count && j < _keys.Count) (_keys[i], _keys[j]) = (_keys[j], _keys[i]);
        await api.ReorderPlaylistEntriesAsync(_id, _keys);
    }, showBusy: false);
}

/// <summary>Movies saved on this device.</summary>
public partial class DownloadsViewModel(DownloadManager manager, SettingsService settings, ServerDownloadsViewModel server) : BaseViewModel
{
    public IReadOnlyList<string> Tabs { get; } = ["On the server", "On this device"];

    /// <summary>0 (the default): what the server is downloading or keeps; 1: downloads on this device.</summary>
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

    /// <summary>From the empty state: off to find something to download.</summary>
    [RelayCommand]
    private Task Browse() => Views.MainPage.ShowTab(Views.AppTab.Discover);

    [RelayCommand]
    private Task Open(OfflineDownload item) => GoToAsync(item.ShowId is { } show
        ? $"{nameof(Views.ShowDetailPage)}?id={show}"
        : $"{nameof(Views.MovieDetailPage)}?id={item.MovieId}");
}
