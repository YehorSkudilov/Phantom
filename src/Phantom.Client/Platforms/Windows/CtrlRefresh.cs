using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Phantom.WinUI;

/// <summary>
/// Pull-to-refresh for a mouse and keyboard: Ctrl + wheel down over a refreshable screen, or Ctrl + Down arrow, refreshes
/// it, like pulling the list down on a phone. With Ctrl held the wheel doesn't scroll the screen. (Shift + wheel is the
/// sideways rows', see ShiftWheelScrolling.)
/// </summary>
public static class CtrlRefresh
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);
    private static DateTime _last = DateTime.MinValue;

    public static void Attach(FrameworkElement platformView, IRefreshView view)
    {
        platformView.Loaded += (_, _) => Hook(platformView, view);
        if (platformView.IsLoaded) Hook(platformView, view);

        // Ctrl + Down: refreshes whichever refreshable screen is showing (hidden sub-tabs keep theirs, but aren't shown).
        var shortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.Down, Modifiers = VirtualKeyModifiers.Control };
        shortcut.Invoked += (_, e) =>
        {
            if (view is RefreshView { IsVisible: true } refresh && IsOnScreen(refresh))
            {
                e.Handled = true;
                Refresh(platformView, refresh);
            }
        };
        platformView.KeyboardAccelerators.Add(shortcut);
        platformView.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // Visible itself and through every parent (a sub-tab's list sits in the tree while another sub-tab shows).
    private static bool IsOnScreen(VisualElement element)
    {
        for (Element? e = element; e is not null; e = e.Parent)
            if (e is VisualElement { IsVisible: false }) return false;
        return true;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> Hooked = new();

    // The screens' scrollers. Ctrl + wheel is the zoom gesture, which a ScrollViewer acts on before any wheel handler
    // sees it, so while Ctrl is held their up/down scrolling is switched off instead.
    private static readonly List<WeakReference<ScrollViewer>> Scrollers = [];
    private static bool _keysHooked;

    private static void Hook(FrameworkElement platformView, IRefreshView view)
    {
        // The wheel is caught below the screen's scroller (it bubbles up from the content), as the rows do.
        if (FindDescendant<ScrollViewer>(platformView) is not { Content: UIElement content } scroller) return;
        if (Hooked.TryGetValue(content, out _)) return;
        Hooked.Add(content, new object());
        content.PointerWheelChanged += (_, e) => OnWheel(platformView, view, scroller, e);
        Scrollers.Add(new WeakReference<ScrollViewer>(scroller));

        if (!_keysHooked && platformView.XamlRoot?.Content is UIElement root)
        {
            _keysHooked = true;
            root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (IsCtrl(e.Key)) SetCtrlHeld(true); }), handledEventsToo: true);
            root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) => { if (IsCtrl(e.Key)) SetCtrlHeld(false); }), handledEventsToo: true);
            // Released while another window had focus: don't leave scrolling switched off.
            root.LostFocus += (_, _) => SetCtrlHeld(false);
            if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
                window.Activated += (_, a) => { if (a.WindowActivationState == WindowActivationState.Deactivated) SetCtrlHeld(false); };
        }
    }

    // Backstop for when the Ctrl key went unseen (nothing in the window had keyboard focus): any scroll the zoom gesture
    // started is undone, back to where the screen was.
    private static void PinScroll(ScrollViewer scroller)
    {
        var offset = scroller.VerticalOffset;
        foreach (var delay in new[] { 0, 60, 250 })
        {
            var timer = scroller.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(delay);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (Math.Abs(scroller.VerticalOffset - offset) > 0.5) scroller.ChangeView(null, offset, null, disableAnimation: true);
            };
            timer.Start();
        }
    }

    private static bool IsCtrl(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl;

    private static void SetCtrlHeld(bool held)
    {
        Scrollers.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in Scrollers)
            if (weak.TryGetTarget(out var s))
                s.VerticalScrollMode = held ? Microsoft.UI.Xaml.Controls.ScrollMode.Disabled : Microsoft.UI.Xaml.Controls.ScrollMode.Enabled;
    }

    private static void OnWheel(FrameworkElement platformView, IRefreshView view, ScrollViewer scroller, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        var wheel = e.GetCurrentPoint(null).Properties;
        if (wheel.IsHorizontalMouseWheel) return;

        e.Handled = true; // Ctrl + wheel never scrolls the screen
        PinScroll(scroller);
        if (wheel.MouseWheelDelta >= 0) return; // only wheel down refreshes, like pulling down
        if (view is RefreshView refresh) Refresh(platformView, refresh);
    }

    private static void Refresh(FrameworkElement platformView, RefreshView refresh)
    {
        if (!refresh.IsEnabled || refresh.IsRefreshing) return;
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
