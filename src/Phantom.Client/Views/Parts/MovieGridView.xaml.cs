using Phantom.Shared;
using Phantom.ViewModels;

namespace Phantom.Views.Parts;

public partial class MovieGridView : ContentView
{
    public MovieGridView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => GridLayout.Span = Math.Max(3, (int)(Width / 140));
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is MovieSummaryDto movie && BindingContext is MovieListViewModel vm)
            vm.OpenMovieCommand.Execute(movie);
        Dispatcher.Dispatch(() => HomePage.ClearSelection(sender));
    }
}
