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
    }

    public override void Load()
    {
        if (_vm.Movies.Count == 0) _vm.ReloadCommand.Execute(null);
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is MovieSummaryDto movie) _vm.OpenMovieCommand.Execute(movie);
        Dispatcher.Dispatch(() => HomePage.ClearSelection(sender));
    }
}
