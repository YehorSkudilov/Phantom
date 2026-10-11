using Phantom.Client.ViewModels;
using Phantom.Shared;

namespace Phantom.Client.Views;

public partial class MovieDetailPage : ContentPage
{
    private readonly MovieDetailViewModel _vm;
    private bool _loadedOnce;

    public MovieDetailPage(MovieDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Coming back from the player: refresh resume point / watched state.
        if (_loadedOnce) _vm.LoadCommand.Execute(null);
        _loadedOnce = true;
    }

    private void OnRelatedSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is MovieSummaryDto movie) _vm.OpenMovieCommand.Execute(movie);
        Dispatcher.Dispatch(() => HomePage.ClearSelection(sender));
    }
}
