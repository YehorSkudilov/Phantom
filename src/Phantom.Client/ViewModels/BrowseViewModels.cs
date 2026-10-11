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
            // Only rows that changed are replaced: clearing and refilling redrew (and reloaded the posters of) them all.
            for (var i = 0; i < hubs.Count; i++)
            {
                if (i >= Hubs.Count) Hubs.Add(hubs[i]);
                else if (!SameRow(Hubs[i], hubs[i])) Hubs[i] = hubs[i];
            }
            while (Hubs.Count > hubs.Count) Hubs.RemoveAt(Hubs.Count - 1);
            var featured = hubs.FirstOrDefault(h => h.Key == "continue")?.Items.FirstOrDefault()
                           ?? hubs.FirstOrDefault(h => h.Key == "recent")?.Items.FirstOrDefault();
            if (featured != Featured) Featured = featured;
            Greeting = settings.User is { } u ? $"Welcome back, {u.Username}" : "Welcome";
            await downloads.SyncOfflineProgressAsync();
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            throw;
        }
    });

    private static bool SameRow(HubDto a, HubDto b) => a.Key == b.Key && a.Title == b.Title && a.Items.SequenceEqual(b.Items);

    [RelayCommand]
    private Task SeeAll(HubDto hub)
    {
        var query = hub.Key switch
        {
            "continue" => "inProgress=true",
            "recent" => "kind=Movie&sort=AddedAt",
            "released" => "kind=Movie&sort=Year",
            "top" or "recommended" => "kind=Movie&sort=Rating",
            "shows-recent" => "kind=Show&sort=AddedAt",
            "shows-top" => "kind=Show&sort=Rating",
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

/// <summary>What the Library lists.</summary>
public enum LibraryKind { All, Movies, Shows }

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
    public IReadOnlyList<string> Tabs { get; } = ["All", "Watchlist", "Recently watched", "Collections", "Playlists"];
    public MovieListViewModel Watchlist { get; } = watchlist.Use("watchlist");
    public MovieListViewModel History { get; } = history.Use("history");
    public CollectionsViewModel Collections => collections;
    public PlaylistsViewModel Playlists => playlists;

    [ObservableProperty] private int selectedTab;

    partial void OnSelectedTabChanged(int value) => LoadSelectedTab();

    /// <summary>
    /// Loads the open sub-tab's list the first time it's shown (the whole library loads itself as it scrolls). After
    /// that it's reloaded by pulling down, not every time the tab appears.
    /// </summary>
    public void LoadSelectedTab()
    {
        switch (SelectedTab)
        {
            case 1 when Watchlist.Movies.Count == 0: Watchlist.LoadCommand.Execute(null); break;
            case 2 when History.Movies.Count == 0: History.LoadCommand.Execute(null); break;
            case 3 when Collections.Collections.Count == 0: Collections.LoadCommand.Execute(null); break;
            case 4 when Playlists.Playlists.Count == 0: Playlists.LoadCommand.Execute(null); break;
        }
    }

    private const int PageSize = 90;

    /// <summary>The next page is fetched once the list is scrolled halfway into the last one, so it's there before the end is.</summary>
    public const int LoadAhead = PageSize / 2;
    private int _page;
    private int _total;
    private CancellationTokenSource? _loadCts;

    public ObservableList<MovieSummaryDto> Movies { get; } = [];
    public ObservableList<string> Genres { get; } = ["All genres"];
    public ObservableList<string> Decades { get; } = ["Any decade"];
    public ObservableList<string> Qualities { get; } = ["Any quality"];
    public ObservableList<string> Ratings { get; } = ["Any rating"];
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

    /// <summary>The whole library's All / Movies / TV shows switch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsMovies), nameof(IsShows))]
    private LibraryKind kind = LibraryKind.All;

    public bool IsAll => Kind == LibraryKind.All;
    public bool IsMovies => Kind == LibraryKind.Movies;
    public bool IsShows => Kind == LibraryKind.Shows;
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
        SetKind(query.TryGetValue("kind", out var k) ? k.ToString() switch
        {
            nameof(MediaKind.Show) => LibraryKind.Shows,
            nameof(MediaKind.Movie) => LibraryKind.Movies,
            _ => LibraryKind.All,
        } : LibraryKind.All);
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

    [RelayCommand]
    private void ShowAll() => SwitchKind(LibraryKind.All);

    [RelayCommand]
    private void ShowMovies() => SwitchKind(LibraryKind.Movies);

    [RelayCommand]
    private void ShowTv() => SwitchKind(LibraryKind.Shows);

    private void SwitchKind(LibraryKind value)
    {
        if (Kind == value) return;
        _suspendReload = true;
        SetKind(value);
        _suspendReload = false;
        ReloadCommand.Execute(null);
    }

    /// <summary>Movies and shows have their own genres, years and ratings, so the filters start over.</summary>
    private void SetKind(LibraryKind value)
    {
        if (Kind == value) return;
        Kind = value;
        SelectedGenre = "All genres";
        SelectedDecade = "Any decade";
        SelectedQuality = "Any quality";
        SelectedRating = "Any rating";
        _filtersLoaded = false;
    }

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
        Kind = Kind == LibraryKind.Shows ? MediaKind.Show : MediaKind.Movie,
        AllKinds = Kind == LibraryKind.All,
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
            // Pulling to refresh mostly gets the same titles back: only the rows that changed are redrawn.
            Movies.Sync(result.Items);
            _page = 1;
            _total = result.Total;
            HasMore = Movies.Count < _total;
            Summary = $"{result.Total} {Kind switch { LibraryKind.Shows => result.Total == 1 ? "show" : "shows", LibraryKind.Movies => result.Total == 1 ? "movie" : "movies", _ => result.Total == 1 ? "title" : "titles" }}"
                      + (Actor is not null ? $" with {Actor}" : Director is not null ? $" by {Director}" : "");
        }, showBusy: false);
        _ = EndRefreshAsync();
    }

    /// <summary>More of the library to load as the list is scrolled.</summary>
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private bool isLoadingMore;

    /// <summary>
    /// The next page, when the list nears its end (the page watches the scroll position, as the list's own "nearly at
    /// the end" event doesn't always fire).
    /// </summary>
    [RelayCommand]
    private async Task LoadMore()
    {
        if (IsLoadingMore || Movies.Count >= _total || _loadCts is not { } cts) return;
        IsLoadingMore = true;
        try
        {
            await RunAsync(async () =>
            {
                var result = await api.MoviesAsync(BuildQuery(_page + 1), cts.Token);
                if (cts.IsCancellationRequested) return; // the filters changed meanwhile: this page is of the old list
                Movies.AddRange(result.Items);
                _page++;
                _total = result.Total;
            }, showBusy: false);
        }
        finally
        {
            IsLoadingMore = false;
            HasMore = Movies.Count < _total;
        }
    }

    private async Task LoadFiltersAsync()
    {
        var filters = await api.FiltersAsync(Kind == LibraryKind.Shows ? MediaKind.Show : MediaKind.Movie, all: Kind == LibraryKind.All);
        Genres.Sync(["All genres", .. filters.Genres]);
        Decades.Sync(["Any decade", .. filters.Years.Select(y => $"{y / 10 * 10}s").Distinct()]);
        Qualities.Sync(["Any quality", .. filters.Qualities]);
        Ratings.Sync(["Any rating", .. filters.ContentRatings]);
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

    public ObservableList<MovieSummaryDto> Movies { get; } = [];
    public ObservableList<string> People { get; } = [];
    public ObservableList<string> Genres { get; } = [];

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
        // Typing one more letter usually keeps most results: those rows (and their posters) stay put.
        Movies.Sync(result.Movies);
        People.Sync(result.People);
        Genres.Sync(result.Genres);
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
