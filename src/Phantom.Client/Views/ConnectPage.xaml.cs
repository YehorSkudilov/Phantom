using Phantom.ViewModels;

namespace Phantom.Views;

public partial class ConnectPage : ContentPage
{
    private readonly ConnectViewModel _vm;

    public ConnectPage(ConnectViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_vm.IsConnected) _vm.ConnectCommand.Execute(null);
    }
}
