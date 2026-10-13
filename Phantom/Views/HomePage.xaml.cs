using Phantom.ViewModels;

namespace Phantom.Views;

public partial class HomePage : TabView
{
    private readonly HomeViewModel _vm;

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        // The banner's TV limit is half the page's height, which may only be known after the banner's width.
        SizeChanged += (_, _) => OnHeroSizeChanged(this, EventArgs.Empty);
    }

    /// <summary>
    /// Loads the first time Home is shown (or again if that didn't get anything, e.g. offline). After that it's only
    /// reloaded by pulling down: reloading on every return (from a movie, the player...) kept redrawing it.
    /// </summary>
    public override void Load()
    {
        if (_vm.Hubs.Count == 0 && !_vm.IsBusy) _vm.RefreshCommand.Execute(null);
    }

    // The featured banner keeps about a 16:9 backdrop's shape as the window widens, within reason, rather than a fixed
    // 220 that left a wide screen with a thin, heavily cropped strip of it. On a TV it stops at half the screen: at
    // 16:9 it filled it, so the first row wasn't even made yet and the remote went from the banner to the nav bar.
    private void OnHeroSizeChanged(object? sender, EventArgs e)
    {
        if (Hero.Width <= 0) return;
        var max = DeviceInfo.Idiom == DeviceIdiom.TV && Height > 0 ? Math.Max(200, Height / 2) : 520;
        var height = Math.Clamp(Hero.Width * 9 / 16, 200, max);
        if (Math.Abs(Hero.HeightRequest - height) > 1) Hero.HeightRequest = height;
    }

    /// <summary>Clears the selection so tapping the same poster again still navigates.</summary>
    internal static void ClearSelection(object? sender)
    {
        if (sender is CollectionView { SelectedItem: not null } cv) cv.SelectedItem = null;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Dispatcher.Dispatch(() => ClearSelection(sender));
}
