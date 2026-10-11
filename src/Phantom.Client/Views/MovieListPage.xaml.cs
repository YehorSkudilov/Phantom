using Phantom.ViewModels;
using Phantom.Shared;

namespace Phantom.Views;

public partial class MovieListPage : ContentPage
{
    private readonly MovieListViewModel _vm;

    public MovieListPage(MovieListViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        SizeChanged += (_, _) => GridLayout.Span = Math.Max(3, (int)(Width / 140));
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is MovieSummaryDto movie) _vm.OpenMovieCommand.Execute(movie);
        Dispatcher.Dispatch(() => HomePage.ClearSelection(sender));
    }
}
