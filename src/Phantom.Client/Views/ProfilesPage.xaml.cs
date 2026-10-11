using Phantom.Client.ViewModels;

namespace Phantom.Client.Views;

public partial class ProfilesPage : ContentPage
{
    private readonly ProfilesViewModel _vm;

    public ProfilesPage(ProfilesViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.LoadCommand.Execute(null);
    }
}
