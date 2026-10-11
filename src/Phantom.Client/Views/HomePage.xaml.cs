using Phantom.ViewModels;

namespace Phantom.Views;

public partial class HomePage : TabView
{
    private readonly HomeViewModel _vm;

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>
    /// Loads the first time Home is shown (or again if that didn't get anything, e.g. offline). After that it's only
    /// reloaded by pulling down: reloading on every return (from a movie, the player...) kept redrawing it.
    /// </summary>
    public override void Load()
    {
        if (_vm.Hubs.Count == 0 && !_vm.IsBusy) _vm.RefreshCommand.Execute(null);
    }

    /// <summary>Clears the selection so tapping the same poster again still navigates.</summary>
    internal static void ClearSelection(object? sender)
    {
        if (sender is CollectionView { SelectedItem: not null } cv) cv.SelectedItem = null;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Dispatcher.Dispatch(() => ClearSelection(sender));
}
