using Phantom.ViewModels;

namespace Phantom.Views;

public partial class DownloadsPage : TabView
{
    private readonly DownloadsViewModel _vm;

    private IDispatcherTimer? _poll;
    private bool _visible;

    public DownloadsPage(DownloadsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadsViewModel.SelectedTab)) UpdatePolling();
        };
    }

    public override void Unload()
    {
        _visible = false;
        UpdatePolling();
    }

    // The server list shows live progress while it's on screen.
    private void UpdatePolling()
    {
        if (_visible && _vm.SelectedTab == 0)
        {
            _vm.Server.RefreshCommand.Execute(null);
            if (_poll is null)
            {
                _poll = Dispatcher.CreateTimer();
                _poll.Interval = TimeSpan.FromSeconds(2);
                _poll.Tick += (_, _) => _vm.Server.RefreshCommand.Execute(null);
            }
            _poll.Start();
        }
        else _poll?.Stop();
    }

    public override void Load()
    {
        _visible = true;
        _vm.RefreshCommand.Execute(null);
        UpdatePolling();
    }
}
