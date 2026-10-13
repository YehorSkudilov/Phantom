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
        BuildAvatarGrid();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfilesViewModel.EditAvatar)) MarkPickedAvatar();
            if (e.PropertyName != nameof(ProfilesViewModel.Editing)) return;
            MarkPickedAvatar();
#if ANDROID
            // A TV remote goes into the editor and stays there until it's closed, then back to Edit profiles.
            Phantom.Droid.TvNavigation.Trap(_vm.ShowEditor ? EditorCard : null, EditButton);
#endif
        };
    }

    // The editor's pictures: the initial first (no picture), then each of them, on the profile's colour.
    private readonly List<(string? Avatar, ImageButton Button)> _avatarButtons = [];

    private void BuildAvatarGrid()
    {
        foreach (var avatar in new string?[] { null }.Concat(ProfileAvatars.All))
        {
            var button = new ImageButton
            {
                Source = Controls.IconExtension.Create(avatar ?? "text_fields", Colors.White, size: 28),
                WidthRequest = 52, HeightRequest = 52, CornerRadius = 14, Padding = 12, Margin = 4,
                BackgroundColor = (Color)Application.Current!.Resources["SurfaceRaised"],
                Command = _vm.PickAvatarCommand, CommandParameter = avatar,
            };
            SemanticProperties.SetDescription(button, avatar is null ? "The name's initial" : avatar.Replace('_', ' '));
            AvatarGrid.Children.Add(button);
            _avatarButtons.Add((avatar, button));
        }
    }

    /// <summary>The picture picked stands out in the profile's colour.</summary>
    private void MarkPickedAvatar()
    {
        var resources = Application.Current!.Resources;
        var picked = _vm.Editing?.AvatarColor is { } color ? Color.FromArgb(color) : (Color)resources["Primary"];
        foreach (var (avatar, button) in _avatarButtons)
            button.BackgroundColor = avatar == _vm.EditAvatar ? picked : (Color)resources["SurfaceRaised"];
    }

    /// <summary>Back closes the editor, else goes back to the profile in use rather than leaving the app.</summary>
    protected override bool OnBackButtonPressed() => _vm.Back() || base.OnBackButtonPressed();

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
