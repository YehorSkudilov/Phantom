using System.Windows.Input;

namespace Phantom.Controls;

/// <summary>A tappable settings-style row: icon, label, chevron. Stack them in a Card for a grouped list.</summary>
public class MenuRow : ContentView
{
    public static readonly BindableProperty GlyphProperty =
        BindableProperty.Create(nameof(Glyph), typeof(string), typeof(MenuRow), "", propertyChanged: (b, _, _) => ((MenuRow)b).UpdateIcon());

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(MenuRow), "");

    public static readonly BindableProperty TextColorProperty =
        BindableProperty.Create(nameof(TextColor), typeof(Color), typeof(MenuRow), null, propertyChanged: (b, _, _) => ((MenuRow)b).UpdateIcon());

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(MenuRow));

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(MenuRow));

    public static readonly BindableProperty ShowChevronProperty =
        BindableProperty.Create(nameof(ShowChevron), typeof(bool), typeof(MenuRow), true);

    private readonly Image _icon = new() { WidthRequest = 22, HeightRequest = 22, VerticalOptions = LayoutOptions.Center };
    private readonly Label _label = new() { FontSize = 15, VerticalOptions = LayoutOptions.Center };

    public MenuRow()
    {
        _label.SetBinding(Label.TextProperty, static (MenuRow r) => r.Text, source: this);

        var chevron = new Image
        {
            Source = IconExtension.Create("chevron_right", (Color)Application.Current!.Resources["TextSecondary"]),
            WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center,
        };
        chevron.SetBinding(IsVisibleProperty, static (MenuRow r) => r.ShowChevron, source: this);

        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 14,
            Padding = new Thickness(16, 0),
            MinimumHeightRequest = 52,
            BackgroundColor = Colors.Transparent,
        };
        grid.Add(_icon, 0);
        grid.Add(_label, 1);
        grid.Add(chevron, 2);
        grid.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() =>
        {
            if (Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter);
        }) });
        grid.SetBinding(SemanticProperties.DescriptionProperty, static (MenuRow r) => r.Text, source: this);
        Content = grid;
        UpdateIcon();
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Colours the label and icon, e.g. Danger for sign out. Defaults to TextPrimary.</summary>
    public Color? TextColor { get => (Color?)GetValue(TextColorProperty); set => SetValue(TextColorProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public bool ShowChevron { get => (bool)GetValue(ShowChevronProperty); set => SetValue(ShowChevronProperty, value); }

    private void UpdateIcon()
    {
        var color = TextColor ?? (Color)Application.Current!.Resources["TextPrimary"];
        _icon.Source = IconExtension.Create(Glyph, color, outlined: true);
        _label.TextColor = color;
    }
}
