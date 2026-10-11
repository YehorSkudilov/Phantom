using System.Collections.ObjectModel;
using AppSkeleton;

namespace Phantom.Views;

/// <summary>The tabs, in the order they sit in the nav bar (and swipe).</summary>
public enum AppTab { Discover, Search, MyStuff, Downloads, More }

/// <summary>The signed-in root: the tabs live in a swipeable CView with a CNavBar underneath. Detail pages still push through Shell.</summary>
public partial class MainPage : ContentPage
{
    private static WeakReference<MainPage>? _instance;

    private readonly ObservableCollection<CNavItem> _tabs;
    private CNavItem? _current;
    private CNavItem? _loadAfterSlide;
    private bool _visible;

    public MainPage(HomePage home, LibraryPage library, MyStuffPage myStuff, DownloadsPage downloads, MorePage more)
    {
        InitializeComponent();

        var resources = Application.Current!.Resources;
        var color = (Color)resources["TextPrimary"];
        var selected = (Color)resources["PrimaryLight"];
        // In AppTab order. Material icon names; the filled font is used for the selected tab.
        _tabs =
        [
            new() { Glyph = "explore", PageName = "Discover", Page = home, Color = color, SelectedColor = selected },
            new() { Glyph = "search", PageName = "Search", Page = library, Color = color, SelectedColor = selected },
            new() { Glyph = "bookmarks", PageName = "My Stuff", Page = myStuff, Color = color, SelectedColor = selected },
            new() { Glyph = "download_for_offline", PageName = "Downloads", Page = downloads, Color = color, SelectedColor = selected },
            new() { Glyph = "account_circle", PageName = "More", Page = more, Color = color, SelectedColor = selected },
        ];

        TabContent.CNavIconItems = _tabs;
        TabContent.PropertyChanged += OnTabContentChanged;
        // Android: the CView takes the downward pull itself (as it does the sideways swipe between tabs), so the tabs'
        // RefreshViews never see it. It's handed on to the visible tab's RefreshView here. Other platforms never raise these.
        TabContent.PullProgressChanged += (_, progress) => OnPull(progress);
        TabContent.PullCommitted += (_, _) => OnPullEnd(refresh: true);
        TabContent.PullCancelled += (_, _) => OnPullEnd(refresh: false);
        NavBar.LoadPageCommand = new Command<CNavItem>(Show);
        NavBar.CNavIconItems = _tabs;
        // On a wide window the bar would stretch edge to edge; there it sits centred, only as wide as its items.
        if (DeviceInfo.Platform == DevicePlatform.WinUI)
        {
            NavBar.HorizontalOptions = LayoutOptions.Center;
            NavBar.WidthRequest = _tabs.Count * 80 + 16;
        }
        NavBar.SelectItem(_tabs[(int)AppTab.Discover]);

        _instance = new(this);
        // The offline state's "Go to Downloads", wherever it shows.
        Phantom.Controls.StateView.OpenDownloads = () => ShowTab(AppTab.Downloads);
    }

    /// <summary>Pops back to the tabs and switches to <paramref name="tab"/>.</summary>
    public static async Task ShowTab(AppTab tab)
    {
        await Shell.Current.GoToAsync("//main");
        if (_instance?.TryGetTarget(out var page) == true)
            page.NavBar.SelectItem(page._tabs[(int)tab]);
    }

    /// <summary>
    /// Opens the Search tab with filters, e.g. <c>genre=Drama</c> or <c>actor=...&amp;sort=Year</c> - the keys
    /// LibraryViewModel.ApplyQueryAttributes reads, as Shell used to pass them in a <c>//main/library?...</c> route.
    /// </summary>
    public static async Task ShowLibrary(string query)
    {
        if (_instance?.TryGetTarget(out var page) == true && page._tabs[(int)AppTab.Search].Page.BindingContext is IQueryAttributable library)
        {
            var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => (object)(p.Length > 1 ? p[1] : ""));
            library.ApplyQueryAttributes(parameters);
        }
        await ShowTab(AppTab.Search);
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

    // ---------- Pull to refresh (Android) ----------

    /// <summary>How far the tab follows the finger: a little, with resistance, so the pull is felt.</summary>
    private const double PullTravel = 56;

    private void OnPull(double progress)
    {
        if (_current?.Page is { } page) page.TranslationY = PullTravel * Math.Min(progress, 1.5) / 1.5;
    }

    private async void OnPullEnd(bool refresh)
    {
        if (_current?.Page is not { } page) return;
        if (refresh && ActiveRefreshView(page) is { IsRefreshing: false } refreshView)
            refreshView.IsRefreshing = true; // shows its spinner and runs its Command, as a native pull would
        try
        {
            await page.TranslateToAsync(0, 0, 160, Easing.CubicOut);
        }
        catch (Exception)
        {
            page.TranslationY = 0;
        }
    }

    /// <summary>The RefreshView showing on a tab (My Stuff has one per list, only one of them visible).</summary>
    private static RefreshView? ActiveRefreshView(IVisualTreeElement element)
    {
        foreach (var child in element.GetVisualChildren())
        {
            if (child is VisualElement { IsVisible: false }) continue;
            if (child is RefreshView { IsEnabled: true } refresh) return refresh;
            if (ActiveRefreshView(child) is { } found) return found;
        }
        return null;
    }

    // Android's Back button (Shell hands it to this page): the visible tab gets it first, so it can close what's open
    // on it (the Search tab's filters popup) instead of the app closing.
    protected override bool OnBackButtonPressed() =>
        _current?.Page is TabView tab && tab.HandleBack() || base.OnBackButtonPressed();

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
