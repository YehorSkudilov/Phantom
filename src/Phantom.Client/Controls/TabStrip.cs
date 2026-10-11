using System.Collections;

namespace Phantom.Controls;

/// <summary>
/// Sub-tabs inside a tab: a scrollable row of pill buttons, one highlighted. Bind <see cref="SelectedIndex"/> two-way
/// and show content by it. Buttons (rather than tap areas) so a TV remote can move between them.
/// </summary>
public class TabStrip : ContentView
{
    public static readonly BindableProperty TabsProperty =
        BindableProperty.Create(nameof(Tabs), typeof(IList), typeof(TabStrip), propertyChanged: (b, _, _) => ((TabStrip)b).Build());

    public static readonly BindableProperty SelectedIndexProperty =
        BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(TabStrip), 0, BindingMode.TwoWay,
            propertyChanged: (b, _, _) => ((TabStrip)b).Highlight());

    private readonly HorizontalStackLayout _row = new() { Spacing = 8, Padding = new Thickness(16, 4) };

    public TabStrip()
    {
        Content = new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = _row };
    }

    public IList? Tabs { get => (IList?)GetValue(TabsProperty); set => SetValue(TabsProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    private void Build()
    {
        _row.Clear();
        if (Tabs is null) return;
        for (var i = 0; i < Tabs.Count; i++)
        {
            var index = i;
            _row.Add(new Button
            {
                Text = Tabs[i]?.ToString(),
                FontSize = 13,
                Padding = new Thickness(14, 6),
                MinimumHeightRequest = 36,
                CornerRadius = 18,
                Command = new Command(() => SelectedIndex = index),
            });
        }
        Highlight();
    }

    private void Highlight()
    {
        var resources = Application.Current!.Resources;
        for (var i = 0; i < _row.Count; i++)
        {
            if (_row[i] is not Button button) continue;
            var selected = i == SelectedIndex;
            button.BackgroundColor = (Color)resources[selected ? "Primary" : "SurfaceRaised"];
            button.TextColor = (Color)resources[selected ? "White" : "TextSecondary"];
        }
    }
}
