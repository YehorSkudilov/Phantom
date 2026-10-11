using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Client.Services;
using Phantom.Shared;

namespace Phantom.Client.ViewModels;

/// <summary>Shared list screen for Watchlist, History and a single Collection.</summary>
public partial class MovieListViewModel(PhantomApi api) : BaseViewModel, IQueryAttributable
{
    private string _kind = "watchlist";
    private int _id;

    public ObservableCollection<MovieSummaryDto> Movies { get; } = [];

    [ObservableProperty] private string? subtitle;
    [ObservableProperty] private bool isEmpty;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _kind = query.TryGetValue("kind", out var k) ? k.ToString()! : "watchlist";
        _id = query.TryGetValue("id", out var id) && int.TryParse(id.ToString(), out var i) ? i : 0;
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
        Movies.Clear();
        foreach (var m in items) Movies.Add(m);
        IsEmpty = Movies.Count == 0;
    });

    [RelayCommand]
    private Task PlayAll() => Movies.Count == 0
        ? Task.CompletedTask
        : GoToAsync($"{nameof(Views.PlayerPage)}?id={Movies[0].Id}&queue={string.Join(',', Movies.Select(m => m.Id))}");
}

public partial class CollectionsViewModel(PhantomApi api) : BaseViewModel
{
    public ObservableCollection<CollectionDto> Collections { get; } = [];

    [ObservableProperty] private bool isEmpty;

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        Collections.Clear();
        foreach (var c in await api.CollectionsAsync()) Collections.Add(c);
        IsEmpty = Collections.Count == 0;
    });

    [RelayCommand]
    private Task Open(CollectionDto c) => GoToAsync($"{nameof(Views.MovieListPage)}?kind=collection&id={c.Id}");
}

public partial class PlaylistsViewModel(PhantomApi api) : BaseViewModel
{
    public ObservableCollection<PlaylistDto> Playlists { get; } = [];

    [ObservableProperty] private bool isEmpty;

    [RelayCommand]
    public Task Load() => RunAsync(async () =>
    {
        Playlists.Clear();
        foreach (var p in await api.PlaylistsAsync()) Playlists.Add(p);
        IsEmpty = Playlists.Count == 0;
    });

    [RelayCommand]
    private Task Create() => RunAsync(async () =>
    {
        var name = await Shell.Current.DisplayPromptAsync("New playlist", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        Playlists.Add(await api.CreatePlaylistAsync(name));
        IsEmpty = false;
    });

    [RelayCommand]
    private Task Delete(PlaylistDto p) => RunAsync(async () =>
    {
        if (!await Shell.Current.DisplayAlertAsync("Delete playlist", $"Delete \"{p.Name}\"?", "Delete", "Cancel")) return;
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

    public ObservableCollection<MovieSummaryDto> Items { get; } = [];

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
        Items.Clear();
        foreach (var m in p.Items) Items.Add(m);
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
public partial class DownloadsViewModel(DownloadManager manager, SettingsService settings) : BaseViewModel
{
    public ObservableCollection<OfflineDownload> Items => manager.Items;

    [ObservableProperty] private string storageText = "";
    [ObservableProperty] private bool wifiOnly = settings.DownloadOnWifiOnly;

    partial void OnWifiOnlyChanged(bool value) => settings.DownloadOnWifiOnly = value;

    [RelayCommand]
    public void Refresh() => StorageText = $"{Items.Count(i => i.IsComplete)} of {Items.Count} ready · {BytesConverter.Format(manager.UsedBytes)} used";

    [RelayCommand]
    private Task Play(OfflineDownload item) => item.IsComplete
        ? GoToAsync($"{nameof(Views.PlayerPage)}?id={item.MovieId}&offline=true&resume={item.OfflinePositionMs > 0}")
        : Task.CompletedTask;

    [RelayCommand]
    private void Toggle(OfflineDownload item)
    {
        if (item.Status is OfflineStatus.Downloading or OfflineStatus.Queued or OfflineStatus.WaitingForWifi) manager.Pause(item);
        else if (item.Status is OfflineStatus.Paused or OfflineStatus.Failed) manager.Resume(item);
    }

    [RelayCommand]
    private async Task Delete(OfflineDownload item)
    {
        if (!await Shell.Current.DisplayAlertAsync("Remove download", $"Delete {item.Title} from this device?", "Delete", "Cancel")) return;
        await manager.DeleteAsync(item);
        Refresh();
    }

    [RelayCommand]
    private Task Open(OfflineDownload item) => GoToAsync($"{nameof(Views.MovieDetailPage)}?id={item.MovieId}");
}
