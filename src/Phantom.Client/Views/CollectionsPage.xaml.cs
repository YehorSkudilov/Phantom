using Phantom.ViewModels;

namespace Phantom.Views;

public partial class CollectionsPage : ContentPage
{
    public CollectionsPage(CollectionsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        vm.LoadCommand.Execute(null);
    }
}
