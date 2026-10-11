using Phantom.Client.ViewModels;

namespace Phantom.Client.Views;

public partial class ServerDownloadsPage : ContentPage
{
    private readonly ServerDownloadsViewModel _vm;
    private IDispatcherTimer? _poll;

    public ServerDownloadsPage(ServerDownloadsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshCommand.Execute(null);
        // Live progress while the page is open.
        _poll = Dispatcher.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => _vm.RefreshCommand.Execute(null);
        _poll.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _poll?.Stop();
    }
}
