using Phantom.ViewModels;

namespace Phantom.Views;

public partial class PlaylistPage : ContentPage
{
    public PlaylistPage(PlaylistViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
