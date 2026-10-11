using Phantom.ViewModels;

namespace Phantom.Views;

public partial class ShowDetailPage : ContentPage
{
    private readonly ShowDetailViewModel _vm;
    private bool _loadedOnce;

    public ShowDetailPage(ShowDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Coming back from the player: refresh what's next and what's been watched.
        if (_loadedOnce) _vm.LoadCommand.Execute(null);
        _loadedOnce = true;
    }

    private void OnRelatedSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Phantom.Shared.MovieSummaryDto item) _vm.OpenMovieCommand.Execute(item);
        Dispatcher.Dispatch(() => HomePage.ClearSelection(sender));
    }
}
