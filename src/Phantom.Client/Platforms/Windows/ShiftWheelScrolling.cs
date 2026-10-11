using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Phantom.WinUI;

/// <summary>
/// Sideways lists (poster rows, cast, the A-Z bar) would otherwise turn the mouse wheel into sideways scrolling and
/// swallow it, so the page stops scrolling whenever the pointer is over a row. Here the wheel scrolls the page, and
/// the row itself only with Shift held. A trackpad's sideways swipe and touch still scroll the row as before.
/// </summary>
public static class ShiftWheelScrolling
{
    // Scroll targets still being animated towards, so quick wheel notches add up instead of restarting from wherever
    // the animation has got to.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, Target> Targets = new();

    private sealed class Target { public double? Horizontal, Vertical; }

    public static void Attach(FrameworkElement list)
    {
        list.Loaded -= OnLoaded;
        list.Loaded += OnLoaded;
        if (list.IsLoaded) Hook(list);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Hook((FrameworkElement)sender);

    private static void Hook(FrameworkElement list)
    {
        // Hooked on the scroller's content, below the ScrollViewer: the wheel bubbles up from the item under the
        // pointer, so handling it here means the row's ScrollViewer never sees it.
        if (FindDescendant<ScrollViewer>(list) is not { Content: UIElement content } row) return;
        content.PointerWheelChanged -= OnWheel;
        content.PointerWheelChanged += OnWheel;
        CtrlRefresh.RegisterRow(row);

        void OnWheel(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(content).Properties;
            if (properties.IsHorizontalMouseWheel) return; // a trackpad's sideways swipe: the row scrolls itself

            // Ctrl + wheel refreshes the screen the row is on.
            if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            {
                CtrlRefresh.FromRow(row, e);
                return;
            }

            var delta = properties.MouseWheelDelta;
            if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
                ScrollBy(row, horizontal: true, -delta);
            else if (FindPageScroller(row) is { } page)
                ScrollBy(page, horizontal: false, -delta);
            e.Handled = true;
        }
    }

    private static void ScrollBy(ScrollViewer scroller, bool horizontal, double delta)
    {
        var target = Targets.GetValue(scroller, s =>
        {
            var t = new Target();
            s.ViewChanged += (_, args) => { if (!args.IsIntermediate) t.Horizontal = t.Vertical = null; };
            return t;
        });

        if (horizontal)
        {
            var to = Math.Clamp((target.Horizontal ?? scroller.HorizontalOffset) + delta, 0, scroller.ScrollableWidth);
            target.Horizontal = to;
            scroller.ChangeView(to, null, null, disableAnimation: false);
        }
        else
        {
            var to = Math.Clamp((target.Vertical ?? scroller.VerticalOffset) + delta, 0, scroller.ScrollableHeight);
            target.Vertical = to;
            scroller.ChangeView(null, to, null, disableAnimation: false);
        }
    }

    /// <summary>The nearest scroller above the row that scrolls up and down.</summary>
    private static ScrollViewer? FindPageScroller(DependencyObject row)
    {
        for (var element = VisualTreeHelper.GetParent(row); element != null; element = VisualTreeHelper.GetParent(element))
            if (element is ScrollViewer { VerticalScrollMode: not Microsoft.UI.Xaml.Controls.ScrollMode.Disabled, ScrollableHeight: > 0 } scroller)
                return scroller;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
