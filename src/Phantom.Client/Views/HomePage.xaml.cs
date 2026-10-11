using Phantom.ViewModels;

namespace Phantom.Views;

public partial class HomePage : TabView
{
    private readonly HomeViewModel _vm;
    private DateTime _lastLoad = DateTime.MinValue;

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    public override void Load()
    {
        // Refresh when coming back (e.g. after playback) so Continue Watching is current.
        if (DateTime.UtcNow - _lastLoad > TimeSpan.FromSeconds(5))
        {
            _lastLoad = DateTime.UtcNow;
            _vm.RefreshCommand.Execute(null);
        }
    }

    /// <summary>Clears the selection so tapping the same poster again still navigates.</summary>
    internal static void ClearSelection(object? sender)
    {
        if (sender is CollectionView { SelectedItem: not null } cv) cv.SelectedItem = null;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Dispatcher.Dispatch(() => ClearSelection(sender));
}
