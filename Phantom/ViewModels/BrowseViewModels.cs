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

    protected override bool ShowsOfflineState => true;

    /// <summary>Rows from the server since the app started (until then, the ones kept from last time may be showing).</summary>
    public bool Fetched { get; private set; }

    [RelayCommand]
    public Task Refresh() => RunAsync(async () =>
    {
        var hubs = await api.HubsAsync();
        Show(hubs);
        Fetched = true;
        Greeting = settings.User is { } u ? $"Welcome back, {u.Username}" : "Welcome";
        _ = SaveCacheAsync(hubs);
        await downloads.SyncOfflineProgressAsync();
    });

    private void Show(List<HubDto> hubs)
    {
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
    }

    // ---------- Kept on the device ----------
    // Building the rows reads much of the library, which a busy or modest server can take a long while over. The last
    // ones are kept per server and profile and shown at once when Discover opens, while fresh ones load behind them.

    private string? CacheFile => settings.User is { } user
        ? Path.Combine(FileSystem.CacheDirectory, $"discover-{new string(settings.ServerUrl.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray())}-{user.Id}.json")
        : null;

    /// <summary>Shows the rows kept from last time, if there are any and nothing is showing yet.</summary>
    public void ShowCached()
    {
        if (Hubs.Count > 0 || CacheFile is not { } file || !File.Exists(file)) return;
        try
        {
            using var stream = File.OpenRead(file);
            if (System.Text.Json.JsonSerializer.Deserialize<List<HubDto>>(stream, PhantomApi.Json) is not { Count: > 0 } hubs) return;
            Show(hubs);
            Greeting = settings.User is { } u ? $"Welcome back, {u.Username}" : "Welcome";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Discover: kept rows unreadable: {ex.Message}");
        }
    }

    private async Task SaveCacheAsync(List<HubDto> hubs)
    {
        if (CacheFile is not { } file) return;
        try
        {
            var temp = file + ".tmp";
            await using (var stream = File.Create(temp))
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, hubs, PhantomApi.Json);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Discover: couldn't keep rows: {ex.Message}");
        }
    }

    /// <summary>Forgets the rows (another profile is in use): they're loaded afresh, as the skeleton shows.</summary>
    public void Reset()
    {
        Hubs.Clear();
        Featured = null;
        HasLoaded = false;
        Fetched = false;
    }

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
    private Task GoToDownloads() => Views.MainPage.ShowDeviceDownloads();
}

/// <summary>What the Search tab lists.</summary>
public enum LibraryKind { All, Movies, Shows }

public record SortOption(string Name, MovieSort Sort, bool Descending)
{
    public override string ToString() => Name;
}

/// <summary>
/// The profile's own lists, in the My Library tab: the watchlist, what was watched lately, and playlists.
/// </summary>
public partial class MyStuffViewModel(MovieListViewModel watchlist, MovieListViewModel history, PlaylistsViewModel playlists) : BaseViewModel
{
    // What was watched lately first, and open to begin with: it's what people come back here for.
    public IReadOnlyList<string> Tabs { get; } = ["Recently watched", "Watchlist", "Playlists"];
    public MovieListViewModel Watchlist { get; } = watchlist.Use("watchlist");
    public MovieListViewModel History { get; } = history.Use("history");
    public PlaylistsViewModel Playlists => playlists;

    [ObservableProperty] private int selectedTab;

    partial void OnSelectedTabChanged(int value) => LoadSelectedTab(onlyIfEmpty: true);

    /// <summary>
    /// Loads the open list: the first time it's shown, and again each time the tab comes back into view (what was
    /// watched, or added to the watchlist, changes elsewhere in the app). Quietly, so the list stays on screen meanwhile.
    /// </summary>
    /// <summary>Forgets the lists (another profile is in use): each loads afresh when it's next shown.</summary>
    public void Reset()
    {
        Watchlist.Movies.Clear();
        History.Movies.Clear();
        Playlists.Playlists.Clear();
    }

    public void LoadSelectedTab(bool onlyIfEmpty = false)
    {
        switch (SelectedTab)
        {
            case 0 when !onlyIfEmpty || History.Movies.Count == 0: History.LoadCommand.Execute(null); break;
            case 1 when !onlyIfEmpty || Watchlist.Movies.Count == 0: Watchlist.LoadCommand.Execute(null); break;
            case 2 when !onlyIfEmpty || Playlists.Playlists.Count == 0: Playlists.LoadCommand.Execute(null); break;
        }
    }
}

/// <summary>The Search tab: the whole library to browse (sorted and filtered) and a search box across it.</summary>
public partial class LibraryViewModel(PhantomApi api, SettingsService settings, SearchViewModel search)
    : BaseViewModel, IQueryAttributable
{
    /// <summary>The search box at the top of the Search tab.</summary>
    public SearchViewModel Search => search;

    protected override bool ShowsOfflineState => true;

    // What the list says when it has nothing to show: filtered down to nothing, or nothing in the library at all.
    public string EmptyTitle => HasActiveFilters ? "Nothing matches" : "Your library is empty";
    public string EmptyMessage => HasActiveFilters
        ? "Try fewer filters, or another letter or decade."
        : "Movies and shows show up here as soon as your server finds them.";
    public string? EmptyActionText => HasActiveFilters ? "Clear filters" : null;
    public string EmptyGlyph => HasActiveFilters ? "search_off" : "movie_filter";

    /// <summary>Forgets the library (another profile is in use, which may see other titles): it's loaded afresh when next shown.</summary>
    public void Reset()
    {
        Movies.Clear();
        _filtersLoaded = false;
        HasLoaded = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(HasActiveFilters)) return;
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(EmptyActionText));
        OnPropertyChanged(nameof(EmptyGlyph));
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string selectedGenre = "All genres";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string selectedDecade = "Any decade";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string selectedQuality = "Any quality";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string selectedRating = "Any rating";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string selectedWatchState = "All";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string selectedLetter = "All";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string? actor;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ClearFiltersText))] private string? director;

    /// <summary>The filters popup (behind the Filters button) is open.</summary>
    [ObservableProperty] private bool showFilters;

    /// <summary>How many filters narrow the library (the sort isn't one): the Filters button shows a dot while any do.</summary>
    public int ActiveFilterCount => new[]
    {
        !SelectedGenre.StartsWith("All"), !SelectedDecade.StartsWith("Any"), !SelectedQuality.StartsWith("Any"),
        !SelectedRating.StartsWith("Any"), SelectedWatchState != "All", SelectedLetter != "All", Actor is not null, Director is not null,
    }.Count(on => on);

    public bool HasActiveFilters => ActiveFilterCount > 0;

    /// <summary>"Clear filters (2)" in the popup, or just "Clear filters".</summary>
    public string ClearFiltersText => ActiveFilterCount > 0 ? $"Clear filters ({ActiveFilterCount})" : "Clear filters";

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
        // The choices made stay chosen (a picker can drop its choice while its list changes), without reloading the list.
        var (genre, decade, quality, rating) = (SelectedGenre, SelectedDecade, SelectedQuality, SelectedRating);
        var suspended = _suspendReload;
        _suspendReload = true;
        try
        {
            Genres.Sync(["All genres", .. filters.Genres]);
            Decades.Sync(["Any decade", .. filters.Years.Select(y => $"{y / 10 * 10}s").Distinct()]);
            Qualities.Sync(["Any quality", .. filters.Qualities]);
            Ratings.Sync(["Any rating", .. filters.ContentRatings]);
            SelectedGenre = Genres.Contains(genre) ? genre : Genres[0];
            SelectedDecade = Decades.Contains(decade) ? decade : Decades[0];
            SelectedQuality = Qualities.Contains(quality) ? quality : Qualities[0];
            SelectedRating = Ratings.Contains(rating) ? rating : Ratings[0];
        }
        finally
        {
            _suspendReload = suspended;
        }
        _filtersLoaded = true;
    }

    /// <summary>
    /// Opens (or closes) the filters. Opening fetches their choices again: genres and the rest change as the library
    /// grows and its details are refreshed (a new genre used to show only after restarting the app).
    /// </summary>
    [RelayCommand]
    private void ToggleFilters()
    {
        ShowFilters = !ShowFilters;
        if (ShowFilters) _ = RunAsync(LoadFiltersAsync, showBusy: false);
    }

    [RelayCommand]
    private void CloseFilters() => ShowFilters = false;

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

}

public partial class SearchViewModel(PhantomApi api) : BaseViewModel
{
    protected override bool ShowsOfflineState => true;

    private CancellationTokenSource? _debounce;
    // The search in flight: a newer one cancels it, so the server stops working on words that were typed over.
    private CancellationTokenSource? _request;

    public ObservableList<MovieSummaryDto> Movies { get; } = [];
    public ObservableList<string> People { get; } = [];
    public ObservableList<string> Genres { get; } = [];

    [ObservableProperty] private string query = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSkeleton))] private bool hasResults;
    [ObservableProperty] private bool noResults;

    /// <summary>From typing until the results for what's in the box are in (the bar under the box sweeps meanwhile).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSkeleton))] private bool isSearching;

    /// <summary>Searching with nothing on screen yet: rows' outlines pulse where the results will be.</summary>
    public bool ShowSkeleton => IsSearching && !HasResults;

    /// <summary>The People section is folded away until opened: names can push the titles off screen.</summary>
    [ObservableProperty] private bool showPeople;

    [RelayCommand]
    private void TogglePeople() => ShowPeople = !ShowPeople;

    partial void OnQueryChanged(string value)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var ct = _debounce.Token;
        // Searching shows straight away, not after the pause; "No matches" for the old words goes with it.
        IsSearching = value.Trim().Length >= 2;
        if (IsSearching) NoResults = false;
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
        _request?.Cancel();
        if (q.Length < 2)
        {
            Movies.Clear();
            People.Clear();
            Genres.Clear();
            HasResults = NoResults = IsSearching = false;
            return;
        }
        var request = _request = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var result = await api.SearchAsync(q, request.Token);
            if (q != Query.Trim()) return; // stale
            // Typing one more letter usually keeps most results: those rows (and their posters) stay put.
            Movies.Sync(result.Movies);
            People.Sync(result.People);
            Genres.Sync(result.Genres);
            HasResults = Movies.Count + People.Count + Genres.Count > 0;
            NoResults = !HasResults;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A newer search took over: it ends the searching state.
        }
        finally
        {
            // Words typed meanwhile have their own search coming (after the pause): it's still searching till then.
            if (request == _request && q == Query.Trim()) IsSearching = false;
        }
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
