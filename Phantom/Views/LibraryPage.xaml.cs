using Phantom.ViewModels;
using Phantom.Shared;

namespace Phantom.Views;

public partial class LibraryPage : TabView
{
    private readonly LibraryViewModel _vm;

    public LibraryPage(LibraryViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        SizeChanged += (_, _) => GridLayout.Span = Math.Max(3, (int)(Width / 140));
        // BindableLayout rows have no selection; open a movie on tap instead.
        SearchMovies.ChildAdded += (_, e) =>
        {
            if (e.Element is View view)
                view.GestureRecognizers.Add(new TapGestureRecognizer
                {
                    Command = new Command(() =>
                    {
                        HideKeyboard();
                        _vm.Search.OpenMovieCommand.Execute(view.BindingContext as MovieSummaryDto);
                    }),
                });
        };
        // The keyboard's Search key closes it (on iOS there's no other key to), as does opening the filters.
        Box.SearchButtonPressed += (_, _) => HideKeyboard();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LibraryViewModel.ShowFilters)) return;
            if (_vm.ShowFilters) HideKeyboard();
#if ANDROID
            // A TV remote goes into the popup and stays there while it's open, then back to the Filters button.
            Phantom.Droid.TvNavigation.Trap(_vm.ShowFilters ? FiltersCard : null, FiltersButton);
#endif
        };
    }

    /// <summary>
    /// Puts the keyboard away. Neither iOS nor Android closes it for a tap outside the search box, which left it covering
    /// the results.
    /// </summary>
    private void HideKeyboard()
    {
        if (!Box.IsFocused && !Box.IsSoftInputShowing()) return;
        Box.Unfocus();
        _ = Box.HideSoftInputAsync(CancellationToken.None);
    }

    // Scrolling the results or the library means reading them: the keyboard goes.
    private void OnResultsScrolled(object? sender, ScrolledEventArgs e) => HideKeyboard();

    public override void Unload()
    {
        base.Unload();
        HideKeyboard();
    }

    /// <summary>Back closes the filters popup when it's open.</summary>
    public override bool HandleBack()
    {
        if (!_vm.ShowFilters) return false;
        _vm.ShowFilters = false;
        return true;
    }

    public override void Load()
    {
        if (_vm.Movies.Count == 0) _vm.ReloadCommand.Execute(null);
    }

    // Loads the next page once the list is halfway into the last one, so it's in before the user gets there. The list's own "remaining items" event
    // doesn't always fire (grids on Windows and iOS), which left the library stuck at its first page.
    private void OnListScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (Math.Abs(e.VerticalDelta) > 4) HideKeyboard();
        if (e.VerticalDelta > 0 && e.LastVisibleItemIndex >= _vm.Movies.Count - LibraryViewModel.LoadAhead && _vm.HasMore)
            _vm.LoadMoreCommand.Execute(null);
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        HideKeyboard();
        if (e.CurrentSelection.FirstOrDefault() is MovieSummaryDto movie) _vm.OpenMovieCommand.Execute(movie);
        Dispatcher.Dispatch(() => HomePage.ClearSelection(sender));
    }
}
