using Phantom.Client.ViewModels;

namespace Phantom.Client.Views;

public partial class PlaylistsPage : ContentPage
{
    private readonly PlaylistsViewModel _vm;

    public PlaylistsPage(PlaylistsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.LoadCommand.Execute(null);
    }
}
