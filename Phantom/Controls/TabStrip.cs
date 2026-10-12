using System.Collections;
using Microsoft.Maui.Controls.Shapes;

namespace Phantom.Controls;

/// <summary>
/// Sub-tabs inside a tab, drawn as tabs: a row of labels over a thin line, the selected one bright with a bar under it.
/// Bind <see cref="SelectedIndex"/> two-way and show content by it. Buttons (rather than tap areas) so a TV remote can
/// move between them; the row scrolls sideways when the tabs don't fit.
/// </summary>
public class TabStrip : ContentView
{
    public static readonly BindableProperty TabsProperty =
        BindableProperty.Create(nameof(Tabs), typeof(IList), typeof(TabStrip), propertyChanged: (b, _, _) => ((TabStrip)b).Build());

    public static readonly BindableProperty SelectedIndexProperty =
        BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(TabStrip), 0, BindingMode.TwoWay,
            propertyChanged: (b, _, _) => ((TabStrip)b).Highlight());

    private readonly HorizontalStackLayout _row = new() { Spacing = 4, Padding = new Thickness(8, 0) };

    public TabStrip()
    {
        var line = new BoxView { HeightRequest = 1, VerticalOptions = LayoutOptions.End, Color = Res("Outline") };
        var scroll = new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = _row };
        Content = new Grid { Children = { line, scroll } };
    }

    public IList? Tabs { get => (IList?)GetValue(TabsProperty); set => SetValue(TabsProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    private static Color Res(string key) => (Color)Application.Current!.Resources[key];

    private void Build()
    {
        _row.Clear();
        if (Tabs is null) return;
        for (var i = 0; i < Tabs.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Text = Tabs[i]?.ToString(),
                FontSize = 14,
                FontFamily = "OpenSansSemibold",
                Padding = new Thickness(10, 10),
                MinimumHeightRequest = 44,
                CornerRadius = 0,
                BackgroundColor = Colors.Transparent,
                Command = new Command(() => SelectedIndex = index),
            };
            // The bar under the selected tab, as wide as the tab.
            var bar = new RoundRectangle { HeightRequest = 3, CornerRadius = new CornerRadius(3, 3, 0, 0), Margin = new Thickness(10, 0),
                VerticalOptions = LayoutOptions.End, Fill = Brush.Transparent, StrokeThickness = 0, InputTransparent = true };
            _row.Add(new Grid { Children = { button, bar } });
        }
        Highlight();
    }

    private void Highlight()
    {
        for (var i = 0; i < _row.Count; i++)
        {
            if (_row[i] is not Grid { Children: [Button button, RoundRectangle bar] }) continue;
            var selected = i == SelectedIndex;
            button.TextColor = Res(selected ? "TextPrimary" : "TextSecondary");
            bar.Fill = selected ? new SolidColorBrush(Res("Primary")) : Brush.Transparent;
        }
    }
}
