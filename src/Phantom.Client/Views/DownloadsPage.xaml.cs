using Phantom.ViewModels;

namespace Phantom.Views;

public partial class DownloadsPage : TabView
{
    private readonly DownloadsViewModel _vm;

    public DownloadsPage(DownloadsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    public override void Load()
    {
        _vm.RefreshCommand.Execute(null);
    }
}
