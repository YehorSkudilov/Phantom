using Phantom.Client.ViewModels;

namespace Phantom.Client.Views;

public partial class PlaylistPage : ContentPage
{
    public PlaylistPage(PlaylistViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
