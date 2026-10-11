using Phantom.ViewModels;

namespace Phantom.Views;

public partial class AccountPage : ContentPage
{
    public AccountPage(AccountViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
