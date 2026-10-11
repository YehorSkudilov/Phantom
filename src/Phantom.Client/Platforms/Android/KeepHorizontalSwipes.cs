using Android.Views;
using AndroidX.RecyclerView.Widget;

namespace Phantom.Droid;

/// <summary>
/// Lets a sideways row of posters (Discover's rows, a show's cast...) scroll sideways. The tabs' host (AppSkeleton's
/// CView) takes any sideways drag to swipe to the next tab, before the row ever gets it. So once a drag on the row turns
/// out to be sideways, and the row can still scroll that way, the row tells its parents not to take it. At the row's
/// end it can't, and the swipe moves to the next tab as before.
/// </summary>
public sealed class KeepHorizontalSwipes : RecyclerView.SimpleOnItemTouchListener
{
    private readonly int _slop;
    private float _downX, _downY;
    private bool _claimed;

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
                _claimed = false;
                break;
            case MotionEventActions.Move when !_claimed:
                var dx = e.GetX() - _downX;
                var dy = e.GetY() - _downY;
                // Decided a little before the tab host would (it waits for the full touch slop), and only for a drag
                // that's more sideways than up or down, so scrolling the page itself over a row still works.
                if (Math.Abs(dx) > _slop / 2f && Math.Abs(dx) > Math.Abs(dy) * 1.2f && recycler.CanScrollHorizontally(dx < 0 ? 1 : -1))
                {
                    _claimed = true;
                    recycler.Parent?.RequestDisallowInterceptTouchEvent(true);
                }
                break;
        }
        return false;
    }
}
