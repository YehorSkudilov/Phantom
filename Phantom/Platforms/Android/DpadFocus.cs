using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using Phantom.Controls;
using AView = Android.Views.View;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using View = Microsoft.Maui.Controls.View;

namespace Phantom.Droid;

/// <summary>
/// Makes the app usable with a TV remote's D-pad (or a keyboard's arrows). Android only moves focus between views that
/// can take it, and MAUI's tap gestures (the nav bar, posters, tiles, menu rows...) leave their views unfocusable, so
/// the remote could reach little more than the buttons. Every view with a tap is made focusable here, with OK (or
/// Enter) doing what a tap does, and whatever has focus gets a ring round it so it's clear where the remote is.
/// </summary>
public static class DpadFocus
{
    private static readonly ConditionalWeakTable<View, object> Watched = new();

    public static void Register()
    {
        Microsoft.Maui.Handlers.ViewHandler.ViewMapper.AppendToMapping("DpadFocus", (handler, view) =>
        {
            if (view is not View control) return;
            Update(control);
            // A tap added after the view is on screen (LibraryPage's search rows) is picked up too.
            if (control.GestureRecognizers is INotifyCollectionChanged list && !Watched.TryGetValue(control, out _))
            {
                Watched.Add(control, list);
                list.CollectionChanged += (_, _) => Update(control);
            }
        });
        // A list's rows that can be selected take focus themselves (MAUI makes them clickable): they get the ring too.
        Microsoft.Maui.Controls.Handlers.Items.CollectionViewHandler.Mapper.AppendToMapping("DpadFocus", (handler, _) =>
            handler.PlatformView.AddOnChildAttachStateChangeListener(new RingRows()));
    }

    private static void Update(View view)
    {
        if (view.Handler is not IPlatformViewHandler { PlatformView: { } platform } handler) return;
        var tap = RemoteFocus.GetSkip(view) ? null : view.GestureRecognizers.OfType<TapGestureRecognizer>()
            .FirstOrDefault(t => t.NumberOfTapsRequired == 1 && t.Buttons.HasFlag(ButtonsMask.Primary));
        if (tap is not null)
        {
            var target = handler.ContainerView ?? platform;
            target.Focusable = true;
            target.FocusableInTouchMode = false; // a finger still just taps it
            target.SetOnKeyListener(new PressOnOk(view, tap));
            AddRing(target);
        }
        // Buttons, switches, sliders... take focus on their own; a focusable layout (a scroll view, a list, the video)
        // only passes it on to what's inside, so it's left without a ring.
        else if (platform.Focusable && platform is not ViewGroup) AddRing(platform);
    }

    /// <summary>
    /// A ring on whatever the remote has just focused, if it hasn't one: the views above are ringed as they're made,
    /// but some take focus that aren't seen then (a search box's text field inside it, a picker...). Lists and scroll
    /// views are left alone: they only hold focus on the way to what's in them. So is anything near the size of the
    /// screen (the video): a ring round the edge of it says nothing.
    /// </summary>
    internal static void EnsureRing(AView view)
    {
        if (view is RecyclerView or Android.Widget.ScrollView or AndroidX.Core.Widget.NestedScrollView or Android.Widget.HorizontalScrollView) return;
        if (view.RootView is { Width: > 0 } root && view.Width > root.Width * 0.9 && view.Height > root.Height * 0.9) return;
        AddRing(view);
    }

    private static WeakReference<AView>? _adjusting;

    /// <summary>
    /// The slider being adjusted with the remote (see TvNavigation.HandleSlider), or null: its ring turns the app's
    /// accent colour, so it's clear the arrows now move it rather than the focus.
    /// </summary>
    public static void SetAdjusting(AView? slider)
    {
        var before = _adjusting?.TryGetTarget(out var was) == true ? was : null;
        _adjusting = slider is null ? null : new(slider);
        before?.Invalidate();
        slider?.Invalidate();
    }

    public static bool IsAdjusting(AView view) => _adjusting?.TryGetTarget(out var slider) == true && slider == view;

    /// <summary>
    /// Whether the view's foreground has the ring. Looked at each time rather than remembered: a list reuses its rows,
    /// and MAUI can give a reused row its own foreground again, which took the ring with it (the remote's ring then
    /// went missing on some posters).
    /// </summary>
    private static bool HasRing(Drawable? drawable) => drawable switch
    {
        FocusRing => true,
        LayerDrawable layers => Enumerable.Range(0, layers.NumberOfLayers).Any(i => layers.GetDrawable(i) is FocusRing),
        _ => false,
    };

    private static void AddRing(AView view)
    {
        if (HasRing(view.Foreground)) return;
        // A list's row on a TV: only the ring (see RingRows), whatever foreground the row was given since.
        if (TvNavigation.IsTv && view.Parent is RecyclerView) view.Foreground = null;
        var ring = new FocusRing(view);
        view.DefaultFocusHighlightEnabled = false; // Android's own grey wash, under the ring
        view.Foreground = view.Foreground is { } existing ? new LayerDrawable([existing, ring]) : ring;
    }

    private static bool IsOk(Keycode key) =>
        key is Keycode.DpadCenter or Keycode.Enter or Keycode.NumpadEnter or Keycode.ButtonA;

    /// <summary>OK on a view with a tap: the tap, when the key comes back up (as a button clicks).</summary>
    private sealed class PressOnOk(View view, TapGestureRecognizer tap) : Java.Lang.Object, AView.IOnKeyListener
    {
        private readonly WeakReference<View> _view = new(view);
        private readonly WeakReference<TapGestureRecognizer> _tap = new(tap);

        public bool OnKey(AView? v, Keycode keyCode, KeyEvent? e)
        {
            if (v is null || e is null || !IsOk(keyCode)) return false;
            if (e.Action == KeyEventActions.Up && e.RepeatCount == 0 && _view.TryGetTarget(out var view) && view.IsEnabled)
            {
                if (!_tap.TryGetTarget(out var tap) || !SendTapped(tap, view)) Touch(v);
            }
            return true;
        }
    }

    // TapGestureRecognizer.SendTapped is how MAUI itself fires a tap (Tapped and Command together), but it's internal.
    private static readonly MethodInfo? SendTappedMethod = typeof(TapGestureRecognizer)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .FirstOrDefault(m => m.Name == "SendTapped" && m.GetParameters() is [{ } first, ..] && first.ParameterType.IsAssignableFrom(typeof(View))
            && m.GetParameters().Skip(1).All(p => p.IsOptional || !p.ParameterType.IsValueType));

    private static bool SendTapped(TapGestureRecognizer tap, View view)
    {
        if (SendTappedMethod is null) return false;
        try
        {
            var parameters = SendTappedMethod.GetParameters();
            SendTappedMethod.Invoke(tap, [view, .. parameters.Skip(1).Select(_ => (object?)null)]);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DpadFocus: SendTapped failed, touching instead: {ex}");
            return false;
        }
    }

    /// <summary>If MAUI's own way ever goes: a finger's tap in the middle of the view, which its gesture code picks up.</summary>
    private static void Touch(AView view)
    {
        var time = SystemClock.UptimeMillis();
        float x = view.Width / 2f, y = view.Height / 2f;
        using var down = MotionEvent.Obtain(time, time, MotionEventActions.Down, x, y, 0);
        using var up = MotionEvent.Obtain(time, time + 50, MotionEventActions.Up, x, y, 0);
        if (down is null || up is null) return;
        view.DispatchTouchEvent(down);
        view.DispatchTouchEvent(up);
    }

    private sealed class RingRows : Java.Lang.Object, RecyclerView.IOnChildAttachStateChangeListener
    {
        public void OnChildViewAttachedToWindow(AView view)
        {
            // MAUI gives a selectable row Android's own highlight, which a TV draws as a square tinted block when the
            // remote is on it: there the row has the app's ring instead (round the card, in the room the poster
            // templates leave it on a TV). A phone keeps the ripple a tap makes.
            AddRing(view);
        }

        public void OnChildViewDetachedFromWindow(AView view) { }
    }

    /// <summary>
    /// A white outline drawn over a view while it has focus, and only from a remote or keyboard (a phone's focused
    /// text box doesn't get one). Round on a round button, rounded on everything else.
    /// </summary>
    private sealed class FocusRing : Drawable
    {
        private static readonly Android.Graphics.Color Accent = Android.Graphics.Color.ParseColor("#A78BFA");

        private readonly Paint _paint = new(PaintFlags.AntiAlias);
        private readonly WeakReference<AView> _view;
        private readonly float _radius;
        private bool _focused;

        public FocusRing(AView view)
        {
            _view = new WeakReference<AView>(view);
            var density = view.Resources?.DisplayMetrics?.Density ?? 2;
            _paint.SetStyle(Paint.Style.Stroke);
            _paint.StrokeWidth = 3 * density;
            _paint.Color = Android.Graphics.Color.White;
            _radius = 12 * density;
        }

        public override bool IsStateful => true;

        protected override bool OnStateChange(int[] state)
        {
            var focused = state.Contains(Android.Resource.Attribute.StateFocused);
            if (focused == _focused) return false;
            _focused = focused;
            InvalidateSelf();
            return true;
        }

        public override void Draw(Canvas canvas)
        {
            if (!_focused || !_view.TryGetTarget(out var view) || view.IsInTouchMode) return;
            _paint.Color = IsAdjusting(view) ? Accent : Android.Graphics.Color.White;
            var bounds = Bounds;
            var inset = _paint.StrokeWidth / 2;
            var rect = new RectF(bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);
            var radius = Math.Abs(bounds.Width() - bounds.Height()) <= 4 ? bounds.Width() / 2f : _radius;
            canvas.DrawRoundRect(rect, radius, radius, _paint);
        }

        public override void SetAlpha(int alpha) => _paint.Alpha = alpha;

        public override void SetColorFilter(ColorFilter? colorFilter) => _paint.SetColorFilter(colorFilter);

        public override int Opacity => (int)Format.Translucent;
    }
}
