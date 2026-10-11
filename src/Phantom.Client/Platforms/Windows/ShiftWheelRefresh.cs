using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Phantom.WinUI;

/// <summary>
/// Pull-to-refresh for a mouse: Shift + wheel over a refreshable screen refreshes it, like pulling the list down on a
/// phone. Over a sideways row, Shift + wheel scrolls the row instead (ShiftWheelScrolling marks the event handled).
/// </summary>
public static class ShiftWheelRefresh
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);

    public static void Attach(UIElement platformView, IRefreshView view)
    {
        // Seen even when something inside (a list's scroller) has handled it, so the check below decides.
        platformView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) => OnWheel(view, e)), handledEventsToo: true);
    }

    private static DateTime _last = DateTime.MinValue;

    private static void OnWheel(IRefreshView view, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) || e.Handled) return;
        if (e.GetCurrentPoint(null).Properties.IsHorizontalMouseWheel) return;
        if (view is not RefreshView refresh || !refresh.IsEnabled || refresh.IsRefreshing) return;
        if (DateTime.UtcNow - _last < Cooldown) return; // one refresh per flick of the wheel

        _last = DateTime.UtcNow;
        e.Handled = true;
        refresh.IsRefreshing = true; // shows the spinner and runs the RefreshView's command, as a pull does
    }
}
