using Phantom.Shared;
using Phantom.ViewModels;

namespace Phantom.Views;

public partial class ProfilesPage : ContentPage
{
    private readonly ProfilesViewModel _vm;

    public ProfilesPage(ProfilesViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        // On a TV the remote starts on a profile (the one in use) as soon as they're there: with nothing focused, it
        // looked as if none could be picked.
        _vm.Profiles.CollectionChanged += (_, _) => FocusFirstSoon();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.LoadCommand.Execute(null);
        FocusFirstSoon();
    }

    private void FocusFirstSoon()
    {
        if (DeviceInfo.Idiom != DeviceIdiom.TV) return;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), () =>
        {
            var buttons = Tiles.Children.OfType<Grid>().Select(g => g.Children.OfType<Button>().FirstOrDefault()).OfType<Button>().ToList();
            if (buttons.Count == 0 || buttons.Any(b => b.IsFocused)) return;
            var current = buttons.FirstOrDefault(b => b.CommandParameter is UserDto u && u.Id == _vm.CurrentProfileId) ?? buttons[0];
            current.Focus();
        });
    }

    /// <summary>The remote is on a profile: its tile grows a little and its avatar gets a bright ring, to see from across the room.</summary>
    private void OnTileFocused(object? sender, FocusEventArgs e) => ShowFocus(sender, on: true);

    private void OnTileUnfocused(object? sender, FocusEventArgs e) => ShowFocus(sender, on: false);

    private static void ShowFocus(object? sender, bool on)
    {
        if (sender is not Button { Parent: Grid tile }) return;
        _ = tile.ScaleToAsync(on ? 1.1 : 1, 120, Easing.CubicOut);
        if (tile.Children.OfType<VerticalStackLayout>().FirstOrDefault()?.Children.OfType<Border>().FirstOrDefault() is { } avatar)
        {
            avatar.Stroke = on ? Colors.White : Colors.Transparent;
            avatar.StrokeThickness = on ? 4 : 0;
        }
    }
}
