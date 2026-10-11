using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Phantom.WinUI;

/// <summary>
/// Pull-to-refresh for a mouse: Shift + wheel down over a refreshable screen refreshes it, like pulling the list down on
/// a phone, and with Shift held the screen itself doesn't scroll. Over a sideways row, Shift + wheel scrolls the row
/// instead: the row is deeper in the tree, so it gets the event first and handles it (see ShiftWheelScrolling).
/// </summary>
public static class ShiftWheelRefresh
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);
    private static DateTime _last = DateTime.MinValue;

    public static void Attach(FrameworkElement platformView, IRefreshView view)
    {
        platformView.Loaded += (_, _) => Hook(platformView, view);
        if (platformView.IsLoaded) Hook(platformView, view);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> Hooked = new();

    private static void Hook(FrameworkElement platformView, IRefreshView view)
    {
        // Below the screen's scroller, as with rows: the wheel bubbles up from the content, so handling it there stops
        // the scroller from ever seeing it.
        if (FindDescendant<ScrollViewer>(platformView) is not { Content: UIElement content }) return;
        if (Hooked.TryGetValue(content, out _)) return;
        Hooked.Add(content, new object());
        content.PointerWheelChanged += (_, e) => OnWheel(platformView, view, e);
    }

    private static void OnWheel(FrameworkElement platformView, IRefreshView view, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)) return;
        var wheel = e.GetCurrentPoint(null).Properties;
        if (wheel.IsHorizontalMouseWheel) return;

        e.Handled = true; // Shift + wheel never scrolls the screen
        if (wheel.MouseWheelDelta >= 0) return; // only wheel down refreshes, like pulling down
        if (view is not RefreshView refresh || !refresh.IsEnabled || refresh.IsRefreshing) return;
        if (DateTime.UtcNow - _last < Cooldown) return; // one refresh per flick of the wheel

        _last = DateTime.UtcNow;
        // Asked of WinUI's own RefreshContainer, so its spinner shows just as for a pull (and the RefreshView's command
        // runs through its usual route). Setting IsRefreshing from code would refresh without showing anything.
        if (platformView is RefreshContainer container) container.RequestRefresh();
        else refresh.IsRefreshing = true;
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
