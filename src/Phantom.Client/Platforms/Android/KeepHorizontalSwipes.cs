using Android.Views;
using AndroidX.RecyclerView.Widget;

namespace Phantom.Droid;

/// <summary>
/// Lets a sideways row of posters (Discover's rows, a show's cast...) scroll sideways. The tabs' host (AppSkeleton's
/// CView) takes any sideways drag to swipe to the next tab, before the row ever gets it. So a touch on a row that can
/// scroll tells its parents not to take it, from the start; once the drag shows it's up or down, or sideways past the
/// row's end, they get it back: the page scrolls, or the swipe moves to the next tab as before.
/// </summary>
public sealed class KeepHorizontalSwipes : RecyclerView.SimpleOnItemTouchListener
{
    private readonly int _slop;
    private float _downX, _downY;
    private bool _decided;

    private KeepHorizontalSwipes(RecyclerView recycler) =>
        _slop = ViewConfiguration.Get(recycler.Context!)?.ScaledTouchSlop ?? 16;

    public static void Attach(RecyclerView recycler) => recycler.AddOnItemTouchListener(new KeepHorizontalSwipes(recycler));

    public override bool OnInterceptTouchEvent(RecyclerView recycler, MotionEvent e)
    {
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = e.GetX();
                _downY = e.GetY();
                _decided = false;
                // Claimed straight away (waiting for the drag to show its direction lost quick flicks to the tab host,
                // which decides on the same move), and given back below once it's clear the row shouldn't have it.
                if (recycler.CanScrollHorizontally(1) || recycler.CanScrollHorizontally(-1))
                    recycler.Parent?.RequestDisallowInterceptTouchEvent(true);
                break;
            case MotionEventActions.Move when !_decided:
                var dx = e.GetX() - _downX;
                var dy = e.GetY() - _downY;
                if (Math.Abs(dx) < _slop / 2f && Math.Abs(dy) < _slop / 2f) break;
                _decided = true;
                // Up or down (the page scrolls), or sideways past the row's end (on to the next tab): let them have it.
                var sideways = Math.Abs(dx) > Math.Abs(dy);
                if (!sideways || !recycler.CanScrollHorizontally(dx < 0 ? 1 : -1))
                    recycler.Parent?.RequestDisallowInterceptTouchEvent(false);
                break;
        }
        return false;
    }
}
