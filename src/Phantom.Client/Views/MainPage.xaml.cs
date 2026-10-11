using System.Collections.ObjectModel;
using AppSkeleton;

namespace Phantom.Views;

/// <summary>The tabs, in the order they sit in the nav bar (and swipe).</summary>
public enum AppTab { Home, Library, Search, Downloads, More }

/// <summary>The signed-in root: the tabs live in a swipeable CView with a CNavBar underneath. Detail pages still push through Shell.</summary>
public partial class MainPage : ContentPage
{
    private static WeakReference<MainPage>? _instance;

    private readonly ObservableCollection<CNavItem> _tabs;
    private CNavItem? _current;
    private CNavItem? _loadAfterSlide;
    private bool _visible;

    public MainPage(HomePage home, LibraryPage library, SearchPage search, DownloadsPage downloads, MorePage more)
    {
        InitializeComponent();

        var resources = Application.Current!.Resources;
        var color = (Color)resources["TextPrimary"];
        var selected = (Color)resources["PrimaryLight"];
        // In AppTab order. Material icon names; the filled font is used for the selected tab.
        _tabs =
        [
            new() { Glyph = "home", PageName = "Home", Page = home, Color = color, SelectedColor = selected },
            new() { Glyph = "video_library", PageName = "Library", Page = library, Color = color, SelectedColor = selected },
            new() { Glyph = "search", PageName = "Search", Page = search, Color = color, SelectedColor = selected },
            new() { Glyph = "download_for_offline", PageName = "Downloads", Page = downloads, Color = color, SelectedColor = selected },
            new() { Glyph = "account_circle", PageName = "More", Page = more, Color = color, SelectedColor = selected },
        ];

        TabContent.CNavIconItems = _tabs;
        TabContent.PropertyChanged += OnTabContentChanged;
        NavBar.LoadPageCommand = new Command<CNavItem>(Show);
        NavBar.CNavIconItems = _tabs;
        NavBar.SelectItem(_tabs[(int)AppTab.Home]);

        _instance = new(this);
    }

    /// <summary>Pops back to the tabs and switches to <paramref name="tab"/>.</summary>
    public static async Task ShowTab(AppTab tab)
    {
        await Shell.Current.GoToAsync("//main");
        if (_instance?.TryGetTarget(out var page) == true)
            page.NavBar.SelectItem(page._tabs[(int)tab]);
    }

    /// <summary>
    /// Opens the Library tab with filters, e.g. <c>genre=Drama</c> or <c>actor=...&amp;sort=Year</c> - the keys
    /// LibraryViewModel.ApplyQueryAttributes reads, as Shell used to pass them in a <c>//main/library?...</c> route.
    /// </summary>
    public static async Task ShowLibrary(string query)
    {
        if (_instance?.TryGetTarget(out var page) == true && page._tabs[(int)AppTab.Library].Page.BindingContext is IQueryAttributable library)
        {
            var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => (object)(p.Length > 1 ? p[1] : ""));
            library.ApplyQueryAttributes(parameters);
        }
        await ShowTab(AppTab.Library);
    }

    private void Show(CNavItem item)
    {
        if (item == _current) return;
        if (_visible) _current?.Page.Unload();
        _current = item;
        TabContent.SetContent(item);
        if (!_visible) return;
        // Load once the tab has slid in, not while it slides, so the switch doesn't stutter.
        if (TabContent.IsNavigating) _loadAfterSlide = item;
        else item.Page.Load();
    }

    private void OnTabContentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CView.IsNavigating) || TabContent.IsNavigating || _loadAfterSlide is not { } item) return;
        _loadAfterSlide = null;
        if (_visible && item == _current) item.Page.Load();
    }

    // Tabs only count as visible while this page is on top, as they did when each tab was its own Shell page:
    // coming back from a detail page or the player reloads the current tab.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        _loadAfterSlide = null;
        _current?.Page.Load();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visible = false;
        _current?.Page.Unload();
    }
}
