using Phantom.Client.ViewModels;

namespace Phantom.Client.Views;

public partial class CollectionsPage : ContentPage
{
    public CollectionsPage(CollectionsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        vm.LoadCommand.Execute(null);
    }
}
