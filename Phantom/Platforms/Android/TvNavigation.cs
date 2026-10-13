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
/// from the bar goes back into the page where it was left. Whatever takes focus gets the ring (see DpadFocus).
/// </summary>
public static class TvNavigation
{
    public static bool IsTv => DeviceInfo.Idiom == DeviceIdiom.TV;

    /// <summary>The tabs' nav bar (set by MainPage); only counts while it's on screen.</summary>
    public static WeakReference<View>? NavBar { get; set; }

    private static WeakReference<AView>? _lastInPage;
    private static readonly Rect Visible = new();

    public static void Attach(Activity activity)
    {
        if (!IsTv) return;
        activity.Window?.DecorView.ViewTreeObserver?.AddOnGlobalFocusChangeListener(new FocusWatcher());
        activity.Window?.DecorView.ViewTreeObserver?.AddOnScrollChangedListener(new ScrollWatcher());
    }

    // ---------- Pop-ups drawn on the page ----------

    private static WeakReference<AView>? _trap;

    /// <summary>
    /// A pop-up drawn on the page (not a window of its own, which Android keeps focus in by itself), such as the
    /// Search tab's filters: focus goes into it and the arrows stay inside it until it's closed (null), when focus goes
    /// to <paramref name="returnTo"/>. Otherwise the remote went on moving through the page behind it.
    /// </summary>
    public static void Trap(View? popup, View? returnTo = null)
    {
        if (!IsTv) return;
        if (popup?.Handler?.PlatformView is AView inside)
        {
            _trap = new(inside);
            inside.Post(() => FocusFirstIn(inside)); // once it's shown and laid out
            return;
        }
        _trap = null;
        if (returnTo?.Handler is IPlatformViewHandler handler && (handler.ContainerView ?? handler.PlatformView) is { } back)
            back.Post(() => back.RequestFocus());
    }

    private static bool FocusFirstIn(AView container)
    {
        if (container is not ViewGroup group) return container.RequestFocus();
        var first = group.GetFocusables(FocusSearchDirection.Forward)?
            .Where(v => v.IsShown && v.GetGlobalVisibleRect(Visible))
            .OrderBy(v => { v.GetGlobalVisibleRect(Visible); return Visible.Top; })
            .ThenBy(v => { v.GetGlobalVisibleRect(Visible); return Visible.Left; })
            .FirstOrDefault();
        return first?.RequestFocus() == true;
    }

    /// <summary>The arrows inside an open pop-up: true (used) when they'd have left it.</summary>
    private static bool KeepInTrap(Activity activity, KeyEvent e)
    {
        if (_trap?.TryGetTarget(out var trap) != true || !trap.IsAttachedToWindow || !trap.IsShown) return false;
        var direction = e.KeyCode switch
        {
            Keycode.DpadUp => FocusSearchDirection.Up,
            Keycode.DpadDown => FocusSearchDirection.Down,
            Keycode.DpadLeft => FocusSearchDirection.Left,
            Keycode.DpadRight => FocusSearchDirection.Right,
            _ => (FocusSearchDirection?)null,
        };
        if (direction is not { } d) return false;
        if (activity.CurrentFocus is not { } focused || !IsWithin(focused, trap)) return FocusFirstIn(trap) || true;
        return focused.FocusSearch(d) is not { } next || !IsWithin(next, trap);
    }

    // ---------- Back to the top ----------

    /// <summary>The button beside the bar that takes the tab back to its top (set by MainPage), and the tab shown.</summary>
    public static WeakReference<View>? TopButton { get; set; }
    public static Func<View?>? CurrentTab { get; set; }

    /// <summary>How far down the tab has to be for the button to show.</summary>
    private const int TopButtonFromDp = 240;

    private static WeakReference<AView>? _scroller;

    /// <summary>Shows the button while the tab's list is scrolled down a way (on a TV only), hides it otherwise.</summary>
    public static void UpdateTopButton()
    {
        if (!IsTv || TopButton?.TryGetTarget(out var button) != true) return;
        var scroller = TabScroller();
        var density = scroller?.Resources?.DisplayMetrics?.Density ?? 2;
        var show = scroller is not null && Offset(scroller) > TopButtonFromDp * density;
        if (button.IsVisible == show) return;
        // Leaving with focus on it: back to the bar first, so focus isn't left on nothing.
        if (!show && button.Handler?.PlatformView is AView { HasFocus: true }) FocusSelectedTab?.Invoke();
        button.IsVisible = show;
    }

    /// <summary>Scrolls the tab's list back to its start.</summary>
    public static void ScrollTabToTop()
    {
        switch (TabScroller())
        {
            case RecyclerView list: list.ScrollToPosition(0); break;
            case NestedScrollView scroll: scroll.ScrollTo(0, 0); break;
            case Android.Widget.ScrollView scroll: scroll.ScrollTo(0, 0); break;
        }
        UpdateTopButton();
    }

    private static int Offset(AView scroller) => scroller is RecyclerView list ? list.ComputeVerticalScrollOffset() : scroller.ScrollY;

    /// <summary>The visible tab's main list or scroll view: the first one on screen that scrolls up and down.</summary>
    private static AView? TabScroller()
    {
        if (CurrentTab?.Invoke()?.Handler?.PlatformView is not AView tab || !tab.IsShown) return null;
        if (_scroller?.TryGetTarget(out var known) == true && known.IsShown && IsWithin(known, tab)) return known;
        var found = Find(tab);
        _scroller = found is null ? null : new(found);
        return found;

        static AView? Find(AView view)
        {
            if (!view.IsShown) return null;
            if (view is RecyclerView { } list && list.GetLayoutManager() is LinearLayoutManager { Orientation: LinearLayoutManager.Vertical }
                || view is NestedScrollView or Android.Widget.ScrollView)
                return view;
            if (view is ViewGroup group)
                for (var i = 0; i < group.ChildCount; i++)
                    if (group.GetChildAt(i) is { } child && Find(child) is { } inner) return inner;
            return null;
        }
    }

    private sealed class ScrollWatcher : Java.Lang.Object, ViewTreeObserver.IOnScrollChangedListener
    {
        public void OnScrollChanged() => UpdateTopButton();
    }

    /// <summary>
    /// What MainPage does for Back (set by it): the visible tab closing something open on it first (true when it
    /// did), the tab focusing something of its own at its top (true when it did), focusing the selected tab's item in
    /// the bar, and going back to the first tab (false when already there).
    /// </summary>
    public static Func<bool>? TabHandlesBack { get; set; }
    public static Func<bool>? TabFocusesTop { get; set; }
    public static Func<bool>? FocusSelectedTab { get; set; }
    public static Func<bool>? ShowFirstTab { get; set; }

    // Back acts when it comes up, as Android's own does: the up of a Back used here isn't passed on.
    private static bool _backUsed;

    /// <summary>
    /// The arrows between the page and the bar, and Back. True when it moved focus (or scrolled) itself.
    /// </summary>
    public static bool HandleKey(Activity activity, KeyEvent e)
    {
        if (!IsTv) return false;
        if (e.KeyCode == Keycode.Back) return HandleBack(activity, e);
        if (e.Action != KeyEventActions.Down) return false;
        if (KeepInTrap(activity, e)) return true;
        if (e.KeyCode is Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight or Keycode.DpadCenter or Keycode.Enter
            && !OnScreen(activity.CurrentFocus) && activity.Window?.DecorView is ViewGroup page)
            return FocusFirst(page);
        if (e.KeyCode is Keycode.DpadLeft or Keycode.DpadRight) return SideToBar(activity, e.KeyCode == Keycode.DpadLeft);
        if (e.KeyCode is not (Keycode.DpadUp or Keycode.DpadDown)) return false;
        if (activity.CurrentFocus is not { } focused || BarView() is not { } bar) return false;

        if (InBar(focused, bar))
        {
            // Back where the remote was on this tab, if that's still showing; else Android's nearest, upwards.
            if (_lastInPage?.TryGetTarget(out var last) == true && last.IsAttachedToWindow && last.IsShown
                && last.GetGlobalVisibleRect(Visible) && last.RequestFocus())
                return true;
            if (activity.Window?.DecorView is ViewGroup root
                && FocusFinder.Instance?.FindNextFocus(root, focused, FocusSearchDirection.Up) is { } above && !InBar(above, bar))
                return above.RequestFocus();
            return false;
        }

        if (e.KeyCode != Keycode.DpadDown) return false;
        var next = focused.FocusSearch(FocusSearchDirection.Down);
        if (next is not null && !InBar(next, bar)) return false;
        // Nothing further down laid out yet: scroll on, then go to what that brought up. At the end, on to the bar.
        if (ScrollerOf(focused) is not { } scroller) return false;
        scroller.ScrollBy(0, Math.Max(1, scroller.Height / 3));
        scroller.Post(() =>
        {
            if (!focused.IsAttachedToWindow || !focused.HasFocus) return;
            if (focused.FocusSearch(FocusSearchDirection.Down) is { } below && BarView() is { } stillBar && !InBar(below, stillBar))
                below.RequestFocus();
        });
        return true;
    }

    /// <summary>
    /// Left or Right with nowhere further to go on the page (the end of a row, the edge of the screen): to the nav
    /// bar, to the tab at the same place along it as the focused view is along the screen, so the left edge comes to the
    /// first tab and the right edge to the last. True when it moved focus.
    /// </summary>
    private static bool SideToBar(Activity activity, bool left)
    {
        if (activity.CurrentFocus is not { } focused || BarView() is not { } bar || InBar(focused, bar)) return false;
        var next = focused.FocusSearch(left ? FocusSearchDirection.Left : FocusSearchDirection.Right);
        if (next is not null && !InBar(next, bar)) return false;

        var tabs = bar.GetFocusables(FocusSearchDirection.Forward)?
            .Where(v => v.IsShown && v.GetGlobalVisibleRect(Visible))
            .OrderBy(v => { v.GetGlobalVisibleRect(Visible); return Visible.Left; })
            .ToList();
        if (tabs is not { Count: > 0 }) return false;
        var screen = activity.Window?.DecorView.Width ?? 0;
        if (screen <= 0) return false;
        focused.GetGlobalVisibleRect(Visible);
        var along = Math.Clamp(Visible.CenterX() / (double)screen, 0, 1);
        var index = Math.Clamp((int)(along * tabs.Count), 0, tabs.Count - 1);
        return tabs[index].RequestFocus();
    }

    /// <summary>
    /// Back on the tabs, as TV apps have it: from the page, to the nav bar (the page back at its top), so the bar
    /// is a press away however far down the page is; from the bar, to the first tab; there, out of the app as usual.
    /// Something open on the tab (the Search tab's filters) is closed first, and a tab with something at its top to
    /// go to (the Search tab's box) goes there first: a long list is as far from its top as from the bar.
    /// </summary>
    private static bool HandleBack(Activity activity, KeyEvent e)
    {
        if (e.Action == KeyEventActions.Up)
        {
            var used = _backUsed;
            _backUsed = false;
            return used;
        }
        if (e.Action != KeyEventActions.Down || e.RepeatCount > 0) return _backUsed;
        _backUsed = false;
        if (BarView() is not { } bar) return false;

        if (activity.CurrentFocus is { } focused && InBar(focused, bar))
        {
            _backUsed = ShowFirstTab?.Invoke() == true;
            if (_backUsed) FocusSelectedTab?.Invoke();
            return _backUsed;
        }

        if (TabHandlesBack?.Invoke() == true) return _backUsed = true;
        if (activity.CurrentFocus is { } inPage) ScrollToTop(inPage);
        _lastInPage = null; // Up from the bar starts again from the top of the page
        if (TabFocusesTop?.Invoke() == true) return _backUsed = true;
        return _backUsed = FocusSelectedTab?.Invoke() == true;
    }

    /// <summary>Every list or scroll view round the view back at its start (sideways rows stay where they are).</summary>
    private static void ScrollToTop(AView view)
    {
        for (var parent = view.Parent; parent is AView ancestor; parent = ancestor.Parent)
        {
            if (!ancestor.CanScrollVertically(-1)) continue;
            switch (ancestor)
            {
                case RecyclerView list: list.ScrollToPosition(0); break;
                case NestedScrollView scroll: scroll.ScrollTo(0, 0); break;
                case Android.Widget.ScrollView scroll: scroll.ScrollTo(0, 0); break;
            }
        }
    }

    /// <summary>
    /// Whether the view is somewhere the remote can see it. A page closing or a tab sliding away (a cast member's
    /// search, which goes back to the tabs) can leave focus on nothing, or on a view now hidden or off to the side:
    /// no ring anywhere, and the arrows moving through what can't be seen.
    /// </summary>
    private static bool OnScreen(AView? view) =>
        view is { IsAttachedToWindow: true, IsShown: true } && view.GetGlobalVisibleRect(Visible);

    /// <summary>Focus to the first thing on screen, reading order, the page's before the nav bar's. True when it found one.</summary>
    private static bool FocusFirst(ViewGroup root)
    {
        var bar = BarView();
        var best = root.GetFocusables(FocusSearchDirection.Forward)?
            .Where(v => v.IsShown && v.GetGlobalVisibleRect(Visible) && Visible.Width() > 0 && Visible.Height() > 0)
            .Select(v => (View: v, Top: Visible.Top, Left: Visible.Left))
            .OrderBy(x => bar is not null && InBar(x.View, bar))
            .ThenBy(x => x.Top).ThenBy(x => x.Left)
            .Select(x => x.View)
            .FirstOrDefault();
        return best?.RequestFocus() == true;
    }

    private sealed class FocusWatcher : Java.Lang.Object, ViewTreeObserver.IOnGlobalFocusChangeListener
    {
        public void OnGlobalFocusChanged(AView? oldFocus, AView? newFocus)
        {
            if (newFocus is null) return;
            DpadFocus.EnsureRing(newFocus);
            // Focus that lands somewhere it can't be seen (a view hidden or slid away as it got it) and stays there:
            // on to the first thing on screen. Checked a moment later, as a list may still be scrolling the view in.
            newFocus.PostDelayed(() =>
            {
                if (newFocus.IsAttachedToWindow && newFocus.HasFocus && !OnScreen(newFocus) && newFocus.RootView is ViewGroup root)
                    FocusFirst(root);
            }, 500);
            if (BarView() is not { } bar || InBar(newFocus, bar)) return;
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

    /// <summary>Whether the view is the bar's, or the back-to-top button beside it.</summary>
    private static bool InBar(AView view, AView bar) =>
        IsWithin(view, bar) || TopButton?.TryGetTarget(out var top) == true && top.Handler?.PlatformView is AView topView && IsWithin(view, topView);

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
