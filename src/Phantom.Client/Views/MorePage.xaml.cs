using Phantom.ViewModels;

namespace Phantom.Views;

public partial class MorePage : TabView
{
    private readonly MoreViewModel _vm;

    public MorePage(MoreViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    public override void Load()
    {
        _vm.LoadCommand.Execute(null);
    }
}
