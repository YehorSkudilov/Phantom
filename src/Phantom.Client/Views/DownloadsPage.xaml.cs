using Phantom.ViewModels;

namespace Phantom.Views;

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
