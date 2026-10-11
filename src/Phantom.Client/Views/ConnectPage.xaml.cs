using Phantom.Client.ViewModels;

namespace Phantom.Client.Views;

public partial class ConnectPage : ContentPage
{
    public ConnectPage(ConnectViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
