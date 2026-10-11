using Phantom.Client.ViewModels;

namespace Phantom.Client.Views;

public partial class DownloadsPage : ContentPage
{
    private readonly DownloadsViewModel _vm;

    public DownloadsPage(DownloadsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshCommand.Execute(null);
    }
}
