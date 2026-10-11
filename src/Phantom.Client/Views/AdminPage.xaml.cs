using Phantom.Shared;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class AdminPage : ContentPage
{
    private IDispatcherTimer? _poll;

    public AdminPage(AdminViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        vm.LoadCommand.Execute(null);
    }

    // Sync status and catalogue progress update live while the Sources sub-tab is on screen.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _poll = Dispatcher.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(3);
        _poll.Tick += async (_, _) =>
        {
            if (BindingContext is AdminViewModel { SelectedTab: 1 } vm) await vm.RefreshSourcesAsync();
        };
        _poll.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _poll?.Stop();
    }

    // The switch shows the source's state; flipping it turns the source on or off on the server.
    private void OnSourceToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is Switch { BindingContext: SourceDto source } && source.Enabled != e.Value)
            ((AdminViewModel)BindingContext).ToggleSourceCommand.Execute(source);
    }
}
