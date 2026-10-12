using Phantom.ViewModels;

namespace Phantom.Views;

public partial class MyStuffPage : TabView
{
    private readonly MyStuffViewModel _vm;

    public MyStuffPage(MyStuffViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    // Each time the tab comes into view: the lists change elsewhere (a movie watched, added to the watchlist...).
    public override void Load() => _vm.LoadSelectedTab();
}
