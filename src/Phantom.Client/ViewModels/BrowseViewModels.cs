using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Services;
using Phantom.Shared;

namespace Phantom.ViewModels;

public partial class HomeViewModel(PhantomApi api, SettingsService settings, DownloadManager downloads) : BaseViewModel
{
    public ObservableCollection<HubDto> Hubs { get; } = [];

    [ObservableProperty] private MovieSummaryDto? featured;
    [ObservableProperty] private string greeting = "";
    [ObservableProperty] private bool isOffline;

    [RelayCommand]
    public Task Refresh() => RunAsync(async () =>
    {
        IsOffline = false;
        try
        {
            var hubs = await api.HubsAsync();
            Hubs.Clear();
            foreach (var hub in hubs) Hubs.Add(hub);
            Featured = hubs.FirstOrDefault(h => h.Key == "continue")?.Items.FirstOrDefault()
                       ?? hubs.FirstOrDefault(h => h.Key == "recent")?.Items.FirstOrDefault();
            Greeting = settings.User is { } u ? $"Welcome back, {u.Username}" : "Welcome";
            await downloads.SyncOfflineProgressAsync();
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            throw;
        }
    });

    [RelayCommand]
    private Task SeeAll(HubDto hub)
    {
        var query = hub.Key switch
        {
            "continue" => "inProgress=true",
            "recent" => "sort=AddedAt",
            "released" => "sort=Year",
            "top" or "recommended" => "sort=Rating",
            "watchlist" => null,
            _ when hub.Key.StartsWith("genre:") => $"genre={Uri.EscapeDataString(hub.Key[6..])}",
            _ => "",
        };
        return query is null
            ? GoToAsync($"{nameof(Views.MovieListPage)}?kind=watchlist")
            : Views.MainPage.ShowLibrary(query);
    }

    [RelayCommand]
    private Task GoToDownloads() => Views.MainPage.ShowTab(Views.AppTab.Downloads);
}

public record SortOption(string Name, MovieSort Sort, bool Descending)
{
    public override string ToString() => Name;
}

public partial class LibraryViewModel(PhantomApi api, SettingsService settings, SearchViewModel search,
    MovieListViewModel watchlist, MovieListViewModel history, CollectionsViewModel collections, PlaylistsViewModel playlists)
    : BaseViewModel, IQueryAttributable
{
    /// <summary>The search box at the top of the Library tab.</summary>
    public SearchViewModel Search => search;

    // Sub-tabs: the whole library, then the profile's own lists.
    public IReadOnlyList<string> Tabs { get; } = ["All movies", "Watchlist", "Recently watched", "Collections", "Playlists"];
    public MovieListViewModel Watchlist { get; } = watchlist.Use("watchlist");
    public MovieListViewModel History { get; } = history.Use("history");
    public CollectionsViewModel Collections => collections;
    public PlaylistsViewModel Playlists => playlists;

    [ObservableProperty] private int selectedTab;

    partial void OnSelectedTabChanged(int value) => LoadSelectedTab();

    /// <summary>Refreshes the open sub-tab's list (the whole library loads itself as it scrolls).</summary>
    public void LoadSelectedTab()
    {
        switch (SelectedTab)
        {
            case 1: Watchlist.LoadCommand.Execute(null); break;
            case 2: History.LoadCommand.Execute(null); break;
            case 3: Collections.LoadCommand.Execute(null); break;
            case 4: Playlists.LoadCommand.Execute(null); break;
        }
    }

    private const int PageSize = 60;
    private int _page;
    private int _total;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<MovieSummaryDto> Movies { get; } = [];
    public ObservableCollection<string> Genres { get; } = ["All genres"];
    public ObservableCollection<string> Decades { get; } = ["Any decade"];
    public ObservableCollection<string> Qualities { get; } = ["Any quality"];
    public ObservableCollection<string> Ratings { get; } = ["Any rating"];
    public IReadOnlyList<string> Letters { get; } = ["All", "#", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];
    public IReadOnlyList<string> WatchStates { get; } = ["All", "Unwatched", "Watched", "In progress"];

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Date added", MovieSort.AddedAt, true),
        new("Title", MovieSort.Title, false),
        new("Release year", MovieSort.Year, true),
        new("Rating", MovieSort.Rating, true),
        new("Duration", MovieSort.Runtime, false),
        new("Last viewed", MovieSort.LastViewed, true),
        new("Random", MovieSort.Random, false),
    ];

    [ObservableProperty] private SortOption selectedSort = null!;
    [ObservableProperty] private string selectedGenre = "All genres";
    [ObservableProperty] private string selectedDecade = "Any decade";
    [ObservableProperty] private string selectedQuality = "Any quality";
    [ObservableProperty] private string selectedRating = "Any rating";
    [ObservableProperty] private string selectedWatchState = "All";
    [ObservableProperty] private string selectedLetter = "All";
    [ObservableProperty] private string? actor;
    [ObservableProperty] private string? director;
    [ObservableProperty] private bool showFilters;
    [ObservableProperty] private bool isGrid = settings.LibraryView == "grid";
    [ObservableProperty] private string summary = "";

    private bool _filtersLoaded;
    private bool _suspendReload;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        SelectedTab = 0;
        _suspendReload = true;
        SelectedSort = SortOptions[0];
        SelectedGenre = "All genres";
        SelectedWatchState = "All";
        Actor = Director = null;
        if (query.TryGetValue("genre", out var g)) SelectedGenre = Uri.UnescapeDataString(g.ToString()!);
        if (query.TryGetValue("actor", out var a)) Actor = Uri.UnescapeDataString(a.ToString()!);
        if (query.TryGetValue("director", out var d)) Director = Uri.UnescapeDataString(d.ToString()!);
        if (query.TryGetValue("inProgress", out _)) SelectedWatchState = "In progress";
        if (query.TryGetValue("sort", out var s) && Enum.TryParse<MovieSort>(s.ToString(), out var sort))
            SelectedSort = SortOptions.First(o => o.Sort == sort);
        _suspendReload = false;
        ReloadCommand.Execute(null);
    }

    partial void OnSelectedSortChanged(SortOption value) => AutoReload();
    partial void OnSelectedGenreChanged(string value) => AutoReload();
    partial void OnSelectedDecadeChanged(string value) => AutoReload();
    partial void OnSelectedQualityChanged(string value) => AutoReload();
    partial void OnSelectedRatingChanged(string value) => AutoReload();
    partial void OnSelectedWatchStateChanged(string value) => AutoReload();
    partial void OnSelectedLetterChanged(string value) => AutoReload();
    partial void OnIsGridChanged(bool value) => settings.LibraryView = value ? "grid" : "list";

    private void AutoReload()
    {
        if (!_suspendReload && SelectedSort is not null) ReloadCommand.Execute(null);
    }

    private MovieQuery BuildQuery(int page) => new()
    {
        Sort = SelectedSort.Sort,
        Descending = SelectedSort.Descending,
        Genre = SelectedGenre.StartsWith("All") ? null : SelectedGenre,
        Decade = int.TryParse(SelectedDecade.TrimEnd('s'), out var dec) ? dec : null,
        Quality = SelectedQuality.StartsWith("Any") ? null : SelectedQuality,
        ContentRating = SelectedRating.StartsWith("Any") ? null : SelectedRating,
        Unwatched = SelectedWatchState switch { "Unwatched" => true, "Watched" => false, _ => null },
        InProgress = SelectedWatchState == "In progress" ? true : null,
        StartsWith = SelectedLetter == "All" ? null : SelectedLetter,
        Actor = Actor,
        Director = Director,
        Page = page,
        PageSize = PageSize,
    };

    [RelayCommand]
    public async Task Reload()
    {
        SelectedSort ??= SortOptions[0];
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        await RunAsync(async () =>
        {
            if (!_filtersLoaded) await LoadFiltersAsync();
            var result = await api.MoviesAsync(BuildQuery(1), ct);
            if (ct.IsCancellationRequested) return;
            Movies.Clear();
            foreach (var m in result.Items) Movies.Add(m);
            _page = 1;
            _total = result.Total;
            Summary = $"{result.Total} movies" + (Actor is not null ? $" with {Actor}" : Director is not null ? $" by {Director}" : "");
        }, showBusy: false);
        _ = EndRefreshAsync();
    }

    [RelayCommand]
    private async Task LoadMore()
    {
        if (IsBusy || Movies.Count >= _total) return;
        await RunAsync(async () =>
        {
            var result = await api.MoviesAsync(BuildQuery(_page + 1));
            foreach (var m in result.Items) Movies.Add(m);
            _page++;
        });
    }

    private async Task LoadFiltersAsync()
    {
        var filters = await api.FiltersAsync();
        foreach (var g in filters.Genres) Genres.Add(g);
        foreach (var d in filters.Years.Select(y => y / 10 * 10).Distinct()) Decades.Add($"{d}s");
        foreach (var q in filters.Qualities) Qualities.Add(q);
        foreach (var r in filters.ContentRatings) Ratings.Add(r);
        _filtersLoaded = true;
    }

    [RelayCommand]
    private void ToggleFilters() => ShowFilters = !ShowFilters;

    [RelayCommand]
    private void ToggleView() => IsGrid = !IsGrid;

    [RelayCommand]
    private void ClearFilters()
    {
        _suspendReload = true;
        SelectedGenre = "All genres";
        SelectedDecade = "Any decade";
        SelectedQuality = "Any quality";
        SelectedRating = "Any rating";
        SelectedWatchState = "All";
        SelectedLetter = "All";
        Actor = Director = null;
        _suspendReload = false;
        ReloadCommand.Execute(null);
    }

    [RelayCommand]
    private void PickLetter(string letter) => SelectedLetter = letter;
}

public partial class SearchViewModel(PhantomApi api) : BaseViewModel
{
    private CancellationTokenSource? _debounce;

    public ObservableCollection<MovieSummaryDto> Movies { get; } = [];
    public ObservableCollection<string> People { get; } = [];
    public ObservableCollection<string> Genres { get; } = [];

    [ObservableProperty] private string query = "";
    [ObservableProperty] private bool hasResults;
    [ObservableProperty] private bool noResults;

    partial void OnQueryChanged(string value)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var ct = _debounce.Token;
        _ = Task.Delay(300, ct).ContinueWith(t =>
        {
            if (!t.IsCanceled) MainThread.BeginInvokeOnMainThread(() => SearchCommand.Execute(null));
        }, TaskScheduler.Default);
    }

    // Concurrent: otherwise the command reports CanExecute=false while a search is in flight, which disables the SearchBar
    // bound to it (dropping focus and the keyboard mid-typing) and drops the search for whatever was typed meanwhile.
    // Stale results are discarded below instead.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Search() => RunAsync(async () =>
    {
        var q = Query.Trim();
        if (q.Length < 2)
        {
            Movies.Clear();
            People.Clear();
            Genres.Clear();
            HasResults = NoResults = false;
            return;
        }
        var result = await api.SearchAsync(q);
        if (q != Query.Trim()) return; // stale
        Movies.Clear();
        People.Clear();
        Genres.Clear();
        foreach (var m in result.Movies) Movies.Add(m);
        foreach (var p in result.People) People.Add(p);
        foreach (var g in result.Genres) Genres.Add(g);
        HasResults = Movies.Count + People.Count + Genres.Count > 0;
        NoResults = !HasResults;
    }, showBusy: false);

    [RelayCommand]
    private Task OpenPerson(string name)
    {
        Query = "";
        return Views.MainPage.ShowLibrary($"actor={Uri.EscapeDataString(name)}");
    }

    [RelayCommand]
    private Task OpenGenre(string genre)
    {
        Query = "";
        return Views.MainPage.ShowLibrary($"genre={Uri.EscapeDataString(genre)}");
    }
}
