using Microsoft.Maui.Controls.PlatformConfiguration;
using iOSPage = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page;
using UIModalPresentationStyle = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.UIModalPresentationStyle;
using Microsoft.Maui.Controls.Shapes;

namespace Phantom.Controls;

/// <summary>
/// The app's own pop-ups, styled like the rest of it, used everywhere in place of the system's alerts, action sheets
/// and prompts: <see cref="AlertAsync"/>, <see cref="ConfirmAsync"/>, <see cref="ChooseAsync"/> (a list of choices,
/// from the bottom of the screen) and <see cref="PromptAsync"/>. Tapping outside one, or Back, cancels it.
/// </summary>
public static class Dialog
{
    /// <summary>A message with one button to close it.</summary>
    public static Task AlertAsync(string title, string message, string ok = "OK") =>
        ShowAsync(title, message, sheet: false, cancelResult: null, (close, body) =>
        {
            body.Add(Buttons([PrimaryButton(ok, () => close(null))]));
        });

    private static string? _errorShowing;

    /// <summary>
    /// Something that went wrong, said plainly. The same error again while it's still on screen isn't shown twice
    /// (a page that fails to load and is retried, say).
    /// </summary>
    public static async Task ErrorAsync(string message, string title = "Something went wrong")
    {
        if (_errorShowing == message) return;
        _errorShowing = message;
        try
        {
            await AlertAsync(title, message);
        }
        finally
        {
            _errorShowing = null;
        }
    }

    /// <summary>
    /// Asks before going ahead: true for <paramref name="accept"/>. A <paramref name="destructive"/> one (deleting,
    /// or something that changes a lot at once) shows its button in red.
    /// </summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel", bool destructive = false)
    {
        var result = await ShowAsync(title, message, sheet: false, cancelResult: null, (close, body) =>
        {
            var yes = destructive ? DangerButton(accept, () => close(accept)) : PrimaryButton(accept, () => close(accept));
            body.Add(Buttons([SecondaryButton(cancel, () => close(null)), yes]));
        });
        return result is not null;
    }

    /// <summary>
    /// A list of choices to pick from, with <paramref name="destructive"/> (if any) in red at the bottom. The choice
    /// picked, or null when cancelled.
    /// </summary>
    public static Task<string?> ChooseAsync(string title, string? destructive, params string[] options) =>
        ShowAsync(title, null, sheet: true, cancelResult: null, (close, body) =>
        {
            var list = new VerticalStackLayout { Spacing = 2 };
            foreach (var option in options) list.Add(OptionButton(option, "TextPrimary", () => close(option)));
            if (destructive is not null) list.Add(OptionButton(destructive, "Danger", () => close(destructive)));
            // A long list (subtitle tracks, say) scrolls rather than running off the screen.
            body.Add(new ScrollView { Content = list, MaximumHeightRequest = 420 });
            body.Add(SecondaryButton("Cancel", () => close(null)));
        });

    /// <summary>Asks for some text: what was typed, or null when cancelled.</summary>
    public static Task<string?> PromptAsync(string title, string? message, string accept = "OK", string cancel = "Cancel",
        string? placeholder = null, int maxLength = -1, Keyboard? keyboard = null, string? initialValue = null, bool password = false) =>
        ShowAsync(title, message, sheet: false, cancelResult: null, (close, body) =>
        {
            var entry = new Entry
            {
                Placeholder = placeholder, MaxLength = maxLength < 0 ? int.MaxValue : maxLength, Keyboard = keyboard ?? Keyboard.Default,
                Text = initialValue, IsPassword = password, ReturnType = ReturnType.Done,
            };
            entry.Completed += (_, _) => close(entry.Text ?? "");
            body.Add(new Border
            {
                Content = entry, Padding = new Thickness(8, 0), Stroke = Res("Outline"), StrokeThickness = 1,
                BackgroundColor = Res("Background"), StrokeShape = new RoundRectangle { CornerRadius = 10 },
            });
            body.Add(Buttons([SecondaryButton(cancel, () => close(null)), PrimaryButton(accept, () => close(entry.Text ?? ""))]));
            // The keyboard comes up straight away.
            entry.Loaded += (_, _) => entry.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(150), () => entry.Focus());
        });

    private static Color Res(string key) => (Color)Application.Current!.Resources[key];

    private static Button PrimaryButton(string text, Action tapped) => Make(new Button { Text = text }, tapped);

    private static Button SecondaryButton(string text, Action tapped) =>
        Make(new Button { Text = text, BackgroundColor = Res("SurfaceRaised"), TextColor = Res("TextPrimary") }, tapped);

    private static Button DangerButton(string text, Action tapped) =>
        Make(new Button { Text = text, BackgroundColor = Res("Danger"), TextColor = Res("Background") }, tapped);

    /// <summary>One row of a choice list: the whole width, its text on the left.</summary>
    private static Button OptionButton(string text, string colorKey, Action tapped) => Make(new Button
    {
        Text = text, BackgroundColor = Colors.Transparent, TextColor = Res(colorKey), FontFamily = "OpenSansRegular", FontSize = 15,
        HorizontalOptions = LayoutOptions.Fill, Padding = new Thickness(12, 12), CornerRadius = 10,
        LineBreakMode = LineBreakMode.TailTruncation,
    }, tapped);

    private static Button Make(Button button, Action tapped)
    {
        button.Clicked += (_, _) => tapped();
        return button;
    }

    /// <summary>Buttons side by side, sharing the width.</summary>
    private static Grid Buttons(Button[] buttons)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.Add(buttons[i], i);
        }
        return grid;
    }

    private static async Task<string?> ShowAsync(string title, string? message, bool sheet, string? cancelResult,
        Action<Action<string?>, VerticalStackLayout> build)
    {
        var navigation = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (navigation is null) return cancelResult;

        var body = new VerticalStackLayout { Spacing = 12 };
        body.Add(new Label { Text = title, FontFamily = "OpenSansSemibold", FontSize = 19, TextColor = Res("TextPrimary") });
        // A CLabel, so the text (an error, say) can be selected and copied.
        if (!string.IsNullOrEmpty(message))
            body.Add(new AppSkeleton.CLabel { Text = message, FontSize = 14, LineHeight = 1.3, TextColor = Res("TextSecondary") });

        var page = new DialogPage(body, sheet, cancelResult, navigation);
        build(page.Close, body);
        await navigation.PushModalAsync(page, animated: false);
        return await page.Result;
    }

    /// <summary>A see-through page over the app: a dimmed backdrop and the dialog's card.</summary>
    private sealed class DialogPage : ContentPage
    {
        private readonly TaskCompletionSource<string?> _result = new();
        private readonly BoxView _scrim;
        private readonly Border _card;
        private readonly bool _sheet;
        private readonly string? _cancelResult;
        private bool _closing;
        private readonly INavigation _host;

        public Task<string?> Result => _result.Task;

        public DialogPage(View body, bool sheet, string? cancelResult, INavigation host)
        {
            _host = host;
            _sheet = sheet;
            _cancelResult = cancelResult;
            BackgroundColor = Colors.Transparent;
            iOSPage.SetModalPresentationStyle(On<iOS>(), UIModalPresentationStyle.OverFullScreen);

            _scrim = new BoxView { Color = Res("Scrim"), Opacity = 0 };
            _scrim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Close(_cancelResult)) });
            _card = new Border
            {
                Content = body,
                Padding = new Thickness(20),
                BackgroundColor = Res("Surface"),
                Stroke = Res("Outline"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 18 },
                MaximumWidthRequest = 460,
                Margin = sheet ? new Thickness(8, 0, 8, 12) : new Thickness(24),
                VerticalOptions = sheet ? LayoutOptions.End : LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                Opacity = 0,
                TranslationY = sheet ? 60 : 16,
            };
            Content = new Grid { Children = { _scrim, _card } };
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await Task.WhenAll(
                _scrim.FadeToAsync(1, 150),
                _card.FadeToAsync(1, 150),
                _card.TranslateToAsync(0, 0, 200, Easing.CubicOut));
        }

        protected override bool OnBackButtonPressed()
        {
            Close(_cancelResult);
            return true;
        }

        /// <summary>Closes with <paramref name="result"/>; the caller carries on once it's gone, so it can open another.</summary>
        public async void Close(string? result)
        {
            if (_closing) return;
            _closing = true;
            // From here on it takes no taps, so even a pop-up that fails to go away can't block the page under it.
            InputTransparent = true;
            if (Content is { } content) content.InputTransparent = true;
            try
            {
                // The fade, but not waited on for ever (an animation that can't run never finishes).
                await Task.WhenAny(Task.WhenAll(
                    _scrim.FadeToAsync(0, 120),
                    _card.FadeToAsync(0, 120),
                    _card.TranslateToAsync(0, _sheet ? 60 : 16, 120, Easing.CubicIn)), Task.Delay(400));
                // Popped from the navigation it was pushed on: the page's own can be a different one under Shell, whose
                // modal stack didn't list it, which left an invisible pop-up over the app swallowing every tap.
                if (_host.ModalStack.Contains(this)) await _host.PopModalAsync(animated: false);
                else if (Navigation.ModalStack.Contains(this)) await Navigation.PopModalAsync(animated: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine($"Dialog: couldn't close: {ex}");
            }
            finally
            {
                _result.TrySetResult(result);
            }
        }
    }
}
