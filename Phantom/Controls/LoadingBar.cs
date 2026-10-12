namespace Phantom.Controls;

/// <summary>
/// A thin line with a stretch of the accent colour sweeping across it while <see cref="IsRunning"/>: something's on its
/// way (a search, say). It keeps its height when idle, so what's under it doesn't jump when it starts or stops.
/// </summary>
public class LoadingBar : ContentView
{
    public static readonly BindableProperty IsRunningProperty = BindableProperty.Create(nameof(IsRunning), typeof(bool), typeof(LoadingBar), false,
        propertyChanged: (b, _, _) => ((LoadingBar)b).Animate());

    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    private readonly BoxView _sweep;
    private bool _onScreen;

    public LoadingBar()
    {
        HeightRequest = 3;
        IsClippedToBounds = true;
        InputTransparent = true;
        _sweep = new BoxView
        {
            Color = (Color)Application.Current!.Resources["Primary"], CornerRadius = 1.5,
            HorizontalOptions = LayoutOptions.Start, Opacity = 0,
        };
        Content = _sweep;
        // The stretch is a third of the bar; the animation only moves and scales it, so nothing is laid out per frame.
        SizeChanged += (_, _) => _sweep.WidthRequest = Width / 3;
        Loaded += (_, _) =>
        {
            _onScreen = true;
            Animate();
        };
        Unloaded += (_, _) =>
        {
            _onScreen = false;
            this.AbortAnimation(nameof(LoadingBar));
        };
    }

    private void Animate()
    {
        var running = _onScreen && IsRunning;
        if (!running)
        {
            // Fades out where it is rather than vanishing mid-sweep; the loop ends with the sweep it's on.
            if (_onScreen) _ = _sweep.FadeToAsync(0, 200);
            return;
        }
        _sweep.AbortAnimation("FadeTo");
        _sweep.Opacity = 1;
        if (this.AnimationIsRunning(nameof(LoadingBar))) return;
        // From just off the left edge to just off the right, stretching out in the middle and quickest there.
        new Animation(t =>
        {
            var stretch = _sweep.Width;
            if (stretch <= 0) return;
            _sweep.TranslationX = -stretch + (Width + stretch) * t;
            _sweep.ScaleX = 0.5 + 0.7 * Math.Sin(t * Math.PI);
        }).Commit(this, nameof(LoadingBar), length: 1100, easing: Easing.CubicInOut, repeat: () => _onScreen && IsRunning);
    }
}
