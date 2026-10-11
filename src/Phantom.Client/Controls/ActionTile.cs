using System.Windows.Input;

namespace Phantom.Controls;

/// <summary>An icon over a short label, for a row of secondary actions (watchlist, watched, download...).</summary>
public class ActionTile : ContentView
{
    public static readonly BindableProperty GlyphProperty =
        BindableProperty.Create(nameof(Glyph), typeof(string), typeof(ActionTile), "", propertyChanged: (b, _, _) => ((ActionTile)b).Update());

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(ActionTile), "");

    /// <summary>Highlights the tile, e.g. when the movie is already on the watchlist.</summary>
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(ActionTile), false, propertyChanged: (b, _, _) => ((ActionTile)b).Update());

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(ActionTile));

    private readonly Image _icon = new() { WidthRequest = 24, HeightRequest = 24, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _label = new() { FontSize = 12, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };

    public ActionTile()
    {
        _label.SetBinding(Label.TextProperty, static (ActionTile t) => t.Text, source: this);
        var stack = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(4, 10), Children = { _icon, _label } };
        var border = new Border
        {
            Content = stack,
            BackgroundColor = (Color)Application.Current!.Resources["Surface"],
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
        };
        border.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() =>
        {
            if (Command?.CanExecute(null) == true) Command.Execute(null);
        }) });
        border.SetBinding(SemanticProperties.DescriptionProperty, static (ActionTile t) => t.Text, source: this);
        Content = border;
        Update();
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    private void Update()
    {
        var resources = Application.Current!.Resources;
        var color = (Color)resources[IsActive ? "PrimaryLight" : "TextPrimary"];
        _icon.Source = IconExtension.Create(Glyph, color, outlined: !IsActive);
        _label.TextColor = IsActive ? color : (Color)resources["TextSecondary"];
    }
}
