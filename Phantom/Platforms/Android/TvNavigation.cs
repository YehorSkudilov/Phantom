using Android.App;
using Android.Views;
using AndroidX.Core.Widget;
using AndroidX.RecyclerView.Widget;
using AView = Android.Views.View;
using Rect = Android.Graphics.Rect;
using View = Microsoft.Maui.Controls.View;

namespace Phantom.Droid;

/// <summary>
/// Moving round the tabs with a TV remote. The nav bar floats over the bottom of the tab, so on its own Android:
/// - focused a row's poster sitting behind the bar (the list counts it as on screen), where neither it nor its ring
///   could be seen;
/// - went from the page down to the bar as soon as nothing further down was laid out yet, instead of scrolling on;
/// - went up from the bar to whatever was nearest, not back to where the remote had been.
/// Here the page scrolls the focused view clear of the bar, Down scrolls on while there's more page, and Up (or Down)
/// from the bar goes back into the page where it was left. Whatever takes focus gets the ring (see DpadFocus), and
/// the bar's live blur, which redraws what's behind it every frame and was what made a TV box crawl, is left off.
/// </summary>
public static class TvNavigation
{
    public static bool IsTv => DeviceInfo.Idiom == DeviceIdiom.TV;

    /// <summary>The tabs' nav bar (set by MainPage); only counts while it's on screen.</summary>
    public static WeakReference<View>? NavBar { get; set; }

    private static WeakReference<AView>? _lastInPage;
    private static readonly Rect Visible = new();

    public static void Register()
    {
        if (!IsTv) return;
        // The tint alone (made near solid by MainPage) reads the same from across the room.
        Microsoft.Maui.Handlers.ViewHandler.ViewMapper.AppendToMapping("TvNoBlur", (handler, view) =>
        {
            if (view is not AppSkeleton.AcrylicView || handler.PlatformView is not ViewGroup frame) return;
            for (var i = 0; i < frame.ChildCount; i++)
                if (frame.GetChildAt(i) is AppSkeleton.RealtimeBlurView blur) blur.Visibility = ViewStates.Gone;
        });
    }

    public static void Attach(Activity activity)
    {
        if (!IsTv) return;
        activity.Window?.DecorView.ViewTreeObserver?.AddOnGlobalFocusChangeListener(new FocusWatcher());
    }

    /// <summary>The arrows between the page and the bar. True when it moved focus (or scrolled) itself.</summary>
    public static bool HandleKey(Activity activity, KeyEvent e)
    {
        if (!IsTv || e.Action != KeyEventActions.Down || e.KeyCode is not (Keycode.DpadUp or Keycode.DpadDown)) return false;
        if (activity.CurrentFocus is not { } focused || BarView() is not { } bar) return false;

        if (IsWithin(focused, bar))
        {
            // Back where the remote was on this tab, if that's still showing; else Android's nearest, upwards.
            if (_lastInPage?.TryGetTarget(out var last) == true && last.IsAttachedToWindow && last.IsShown
                && last.GetGlobalVisibleRect(Visible) && last.RequestFocus())
                return true;
            if (activity.Window?.DecorView is ViewGroup root
                && FocusFinder.Instance?.FindNextFocus(root, focused, FocusSearchDirection.Up) is { } above && !IsWithin(above, bar))
                return above.RequestFocus();
            return false;
        }

        if (e.KeyCode != Keycode.DpadDown) return false;
        var next = focused.FocusSearch(FocusSearchDirection.Down);
        if (next is not null && !IsWithin(next, bar)) return false;
        // Nothing further down laid out yet: scroll on, then go to what that brought up. At the end, on to the bar.
        if (ScrollerOf(focused) is not { } scroller) return false;
        scroller.ScrollBy(0, Math.Max(1, scroller.Height / 3));
        scroller.Post(() =>
        {
            if (!focused.IsAttachedToWindow || !focused.HasFocus) return;
            if (focused.FocusSearch(FocusSearchDirection.Down) is { } below && BarView() is { } stillBar && !IsWithin(below, stillBar))
                below.RequestFocus();
        });
        return true;
    }

    private sealed class FocusWatcher : Java.Lang.Object, ViewTreeObserver.IOnGlobalFocusChangeListener
    {
        public void OnGlobalFocusChanged(AView? oldFocus, AView? newFocus)
        {
            if (newFocus is null) return;
            DpadFocus.EnsureRing(newFocus);
            if (BarView() is not { } bar || IsWithin(newFocus, bar)) return;
            _lastInPage = new(newFocus);
            KeepClearOfBar(newFocus, bar);
        }
    }

    /// <summary>Scrolls the page so the focused view is above the bar, with a little room to spare.</summary>
    private static void KeepClearOfBar(AView view, AView bar)
    {
        int[] at = new int[2], barAt = new int[2];
        view.GetLocationOnScreen(at);
        bar.GetLocationOnScreen(barAt);
        var room = (int)(16 * (view.Resources?.DisplayMetrics?.Density ?? 2));
        var overlap = at[1] + view.Height + room - barAt[1];
        if (overlap <= 0 || ScrollerOf(view) is not { } scroller) return;
        // A view taller than the room above the bar keeps its top on screen.
        overlap = Math.Min(overlap, Math.Max(0, at[1] - room));
        if (overlap <= 0) return;
        switch (scroller)
        {
            case RecyclerView list: list.SmoothScrollBy(0, overlap); break;
            case NestedScrollView scroll: scroll.SmoothScrollBy(0, overlap); break;
            default: scroller.ScrollBy(0, overlap); break;
        }
    }

    /// <summary>The nearest list or scroll view round the view that can scroll further down.</summary>
    private static AView? ScrollerOf(AView view)
    {
        for (var parent = view.Parent; parent is AView ancestor; parent = ancestor.Parent)
            if (ancestor.CanScrollVertically(1)) return ancestor;
        return null;
    }

    private static AView? BarView() =>
        NavBar?.TryGetTarget(out var bar) == true && bar.Handler?.PlatformView is AView view
            && view.IsAttachedToWindow && view.IsShown ? view : null;

    private static bool IsWithin(AView view, AView ancestor)
    {
        for (var current = view; current is not null; current = current.Parent as AView)
            if (current == ancestor) return true;
        return false;
    }
}
