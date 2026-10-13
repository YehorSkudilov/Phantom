using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phantom.Controls;

namespace Phantom.ViewModels;

/// <summary>What a list shows by kind: everything, movies, or shows.</summary>
public enum KindFilter { All, Movies, Shows }

/// <summary>
/// The toolbar over My Library's and Downloads' lists (see Controls/ListToolbar): a search, All / Movies / TV shows,
/// a sort and a "show" filter (each list names its own), and grid or list. The lists filter what they have loaded by
/// it, as it changes (<see cref="Changed"/>).
/// </summary>
public partial class ListFilter : ObservableObject
{
    private readonly Action<bool>? _saveView;

    /// <param name="sorts">The sorts on offer, the list's own order first.</param>
    /// <param name="shows">What it can be narrowed to, "everything" first.</param>
    public ListFilter(string placeholder, IReadOnlyList<string> sorts, IReadOnlyList<string> shows, bool isGrid, Action<bool>? saveView = null)
    {
        Placeholder = placeholder;
        Sorts = sorts;
        Shows = shows;
        sort = sorts[0];
        show = shows[0];
        this.isGrid = isGrid;
        _saveView = saveView;
    }

    public string Placeholder { get; }
    public IReadOnlyList<string> Sorts { get; }
    public IReadOnlyList<string> Shows { get; }

    /// <summary>Anything changed that the list should filter again by.</summary>
    public event EventHandler? Changed;

    [ObservableProperty] private string query = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsMovies), nameof(IsShows))] private KindFilter kind;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string sort;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasActiveFilters))] private string show;
    [ObservableProperty] private bool isGrid;

    public bool IsAll => Kind == KindFilter.All;
    public bool IsMovies => Kind == KindFilter.Movies;
    public bool IsShows => Kind == KindFilter.Shows;

    /// <summary>A sort or "show" other than the list's own: the Filters button gets a dot.</summary>
    public bool HasActiveFilters => Sort != Sorts[0] || Show != Shows[0];

    partial void OnQueryChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnKindChanged(KindFilter value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnSortChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnShowChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnIsGridChanged(bool value) => _saveView?.Invoke(value);

    [RelayCommand] private void ShowAll() => Kind = KindFilter.All;
    [RelayCommand] private void ShowMovies() => Kind = KindFilter.Movies;
    [RelayCommand] private void ShowShows() => Kind = KindFilter.Shows;
    [RelayCommand] private void ToggleView() => IsGrid = !IsGrid;

    private const string Tick = "✓  ";

    /// <summary>Filters: one list of the sorts and the "show" choices, the ones in use ticked; picking one uses it.</summary>
    [RelayCommand]
    private async Task ChooseFilters()
    {
        var options = Sorts.Select(s => (s == Sort ? Tick : "") + "Sort: " + s)
            .Concat(Shows.Select(s => (s == Show ? Tick : "") + "Show: " + s)).ToArray();
        var choice = await Dialog.ChooseAsync("Sort and filter", null, options);
        if (choice is null) return;
        choice = choice.StartsWith(Tick) ? choice[Tick.Length..] : choice;
        if (choice.StartsWith("Sort: ")) Sort = choice["Sort: ".Length..];
        else if (choice.StartsWith("Show: ")) Show = choice["Show: ".Length..];
    }

    /// <summary>Whether the text matches the search (any part of it, any case).</summary>
    public bool Matches(params string?[] texts) =>
        string.IsNullOrWhiteSpace(Query) || texts.Any(t => t?.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Whether a movie (false) or a show (true) is in the kind picked.</summary>
    public bool KindMatches(bool isShow) => Kind switch
    {
        KindFilter.Movies => !isShow,
        KindFilter.Shows => isShow,
        _ => true,
    };
}
