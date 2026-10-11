using Phantom.ViewModels;

namespace Phantom.Views;

public partial class AdminPage : ContentPage
{
    public AdminPage(AdminViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        vm.LoadCommand.Execute(null);
    }
}
