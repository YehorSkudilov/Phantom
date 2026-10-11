using Phantom.Shared;
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

    // The switch shows the source's state; flipping it turns the source on or off on the server.
    private void OnSourceToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is Switch { BindingContext: SourceDto source } && source.Enabled != e.Value)
            ((AdminViewModel)BindingContext).ToggleSourceCommand.Execute(source);
    }
}
