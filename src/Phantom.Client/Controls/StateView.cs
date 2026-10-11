using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace Phantom.Controls;

/// <summary>The placeholder a <see cref="StateView"/> shows while loading: the shape of what's coming.</summary>
public enum SkeletonKind
{
    /// <summary>Discover: a featured banner, then rows of posters.</summary>
    Rows,
    /// <summary>A grid of posters (Search, the watchlist...).</summary>
    Grid,
    /// <summary>A list of rows, a picture and two lines each (downloads, playlists, episodes).</summary>
    List,
    /// <summary>A movie's or show's page: backdrop, title, buttons, text.</summary>
    Detail,
    /// <summary>Just the spinner-free blank: for places too small for a skeleton.</summary>
    None,
}

/// <summary>
/// What a page shows when it has nothing to show yet, the same everywhere in the app. Put it over the page's content and
/// bind <see cref="HasContent"/>, <see cref="IsLoading"/>, <see cref="HasLoaded"/> and <see cref="IsOffline"/>:
/// <list type="bullet">
/// <item>loading (or not loaded yet): a skeleton of the content, softly pulsing;</item>
/// <item>offline: a cloud with a line through it, Try again, and a way to the downloads that still play;</item>
/// <item>loaded but empty: an animated icon, a title, a line of help and, if given, a button to do something about it.</item>
/// </list>
/// It hides itself once there's content.
/// </summary>
public class StateView : ContentView
{
    public static readonly BindableProperty HasContentProperty = Create(nameof(HasContent), typeof(bool), false);
    public static readonly BindableProperty IsLoadingProperty = Create(nameof(IsLoading), typeof(bool), false);
    public static readonly BindableProperty HasLoadedProperty = Create(nameof(HasLoaded), typeof(bool), true);
    public static readonly BindableProperty IsOfflineProperty = Create(nameof(IsOffline), typeof(bool), false);
    public static readonly BindableProperty SkeletonProperty = Create(nameof(Skeleton), typeof(SkeletonKind), SkeletonKind.Grid);
    public static readonly BindableProperty GlyphProperty = Create(nameof(Glyph), typeof(string), "movie");
    public static readonly BindableProperty TitleProperty = Create(nameof(Title), typeof(string), "Nothing here yet");
    public static readonly BindableProperty MessageProperty = Create(nameof(Message), typeof(string), null);
    public static readonly BindableProperty ActionTextProperty = Create(nameof(ActionText), typeof(string), null);
    public static readonly BindableProperty ActionCommandProperty = Create(nameof(ActionCommand), typeof(ICommand), null);
    public static readonly BindableProperty RetryCommandProperty = Create(nameof(RetryCommand), typeof(ICommand), null);

    public bool HasContent { get => (bool)GetValue(HasContentProperty); set => SetValue(HasContentProperty, value); }
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    /// <summary>False until the first load has finished: before that it's "loading", not "empty".</summary>
    public bool HasLoaded { get => (bool)GetValue(HasLoadedProperty); set => SetValue(HasLoadedProperty, value); }
    public bool IsOffline { get => (bool)GetValue(IsOfflineProperty); set => SetValue(IsOfflineProperty, value); }
    public SkeletonKind Skeleton { get => (SkeletonKind)GetValue(SkeletonProperty); set => SetValue(SkeletonProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    /// <summary>Run by Try again when offline (usually the page's load command).</summary>
    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }

    /// <summary>Opens the Downloads tab, from the offline state (set by MainPage; this control doesn't know the tabs).</summary>
    public static Func<Task>? OpenDownloads { get; set; }

    private static BindableProperty Create(string name, Type type, object? defaultValue) =>
        BindableProperty.Create(name, type, typeof(StateView), defaultValue, propertyChanged: (b, _, _) => ((StateView)b).Update());

    private enum Mode { Hidden, Loading, Offline, Empty }

    private Mode _mode = (Mode)(-1);
    private (SkeletonKind, string, string, string?, string?) _built;
    private bool _onScreen;

    public StateView()
    {
        Loaded += (_, _) =>
        {
            _onScreen = true;
            Animate();
        };
        Unloaded += (_, _) =>
        {
            _onScreen = false;
            this.AbortAnimation(nameof(StateView));
        };
        Update();
    }

    private static Color Res(string key) => (Color)Application.Current!.Resources[key];

    private void Update()
    {
        var mode = HasContent ? Mode.Hidden
            : IsLoading || !HasLoaded ? (IsOffline ? Mode.Offline : Mode.Loading)
            : IsOffline ? Mode.Offline
            : Mode.Empty;
        // The empty state's words can change while it's showing (a search with no results, say).
        var built = (Skeleton, Glyph, Title, Message, ActionText);
        if (mode == _mode && (mode != Mode.Empty || built == _built)) return;
        _mode = mode;
        _built = built;

        IsVisible = mode != Mode.Hidden;
        this.AbortAnimation(nameof(StateView));
        Content = mode switch
        {
            Mode.Loading => BuildSkeleton(Skeleton),
            Mode.Offline => BuildMessage("cloud_off", "You're offline",
                "Phantom can't reach your server right now. Anything you've downloaded still plays.",
                ("Try again", RetryCommand), ("Go to Downloads", new Command(async () => { if (OpenDownloads is { } open) await open(); }))),
            Mode.Empty => BuildMessage(Glyph, Title, Message, (ActionText, ActionCommand), (null, null)),
            _ => null,
        };
        Animate();
    }

    // ---------- Skeletons ----------

    private View _pulse = new BoxView();
    private View? _float;
    private View? _ring;

    private static Border Box(double width, double height, double radius = 8) => new()
    {
        WidthRequest = width, HeightRequest = height, BackgroundColor = Res("SurfaceRaised"), StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = radius }, HorizontalOptions = LayoutOptions.Start,
    };

    private static Border Bar(double height, double radius = 6) => new()
    {
        HeightRequest = height, BackgroundColor = Res("SurfaceRaised"), StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = radius },
    };

    private View BuildSkeleton(SkeletonKind kind)
    {
        var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(16, 8) };
        switch (kind)
        {
            case SkeletonKind.Rows:
                stack.Add(Bar(200, 16));
                for (var row = 0; row < 3; row++)
                {
                    stack.Add(Box(150, 18, 5));
                    var posters = new HorizontalStackLayout { Spacing = 12 };
                    for (var i = 0; i < 6; i++) posters.Add(Box(120, 180, 10));
                    stack.Add(posters);
                }
                break;
            case SkeletonKind.Grid:
                var grid = new Grid { ColumnSpacing = 10, RowSpacing = 14 };
                for (var c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                for (var i = 0; i < 12; i++)
                {
                    // A poster's shape at a phone's three-across width (sized up front: sizing it from the column's width once
                    // laid out left it a thin line), with its title under it, as on Discover.
                    var poster = new VerticalStackLayout { Spacing = 8 };
                    poster.Add(Bar(170, 10));
                    var caption = Bar(10, 4);
                    caption.WidthRequest = 70;
                    caption.HorizontalOptions = LayoutOptions.Start;
                    poster.Add(caption);
                    grid.Add(poster, i % 3, i / 3);
                    if (i % 3 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                }
                stack.Add(grid);
                break;
            case SkeletonKind.List:
                for (var i = 0; i < 6; i++)
                {
                    var line = new Grid { ColumnSpacing = 12 };
                    line.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(112)));
                    line.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    line.Add(Box(112, 66, 8));
                    var text = new VerticalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
                    text.Add(Bar(14, 4));
                    var shorter = Bar(10, 4);
                    shorter.WidthRequest = 120;
                    shorter.HorizontalOptions = LayoutOptions.Start;
                    text.Add(shorter);
                    line.Add(text, 1);
                    stack.Add(line);
                }
                break;
            case SkeletonKind.Detail:
                stack.Padding = new Thickness(0, 0, 0, 16);
                stack.Add(Bar(260, 0));
                var body = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(16, 0) };
                var title = Bar(28, 6);
                title.WidthRequest = 220;
                title.HorizontalOptions = LayoutOptions.Start;
                body.Add(title);
                body.Add(Bar(52, 10));
                var tiles = new Grid { ColumnSpacing = 8 };
                for (var c = 0; c < 3; c++)
                {
                    tiles.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    tiles.Add(Bar(64, 12), c);
                }
                body.Add(tiles);
                for (var i = 0; i < 4; i++) body.Add(Bar(12, 4));
                stack.Add(body);
                break;
            default:
                break;
        }
        _pulse = stack;
        _float = _ring = null;
        // The skeleton fills the space it's given rather than scrolling: it's only a hint of the layout.
        return new ScrollView { Content = stack, InputTransparent = true, VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Orientation = ScrollOrientation.Neither };
    }

    // ---------- Offline / empty ----------

    private View BuildMessage(string glyph, string title, string? message, (string? Text, ICommand? Command) primary, (string? Text, ICommand? Command) secondary)
    {
        // The icon in a soft disc, a ring pulsing out from behind it.
        var ring = new Ellipse
        {
            WidthRequest = 112, HeightRequest = 112, Stroke = new SolidColorBrush(Res("Primary")), StrokeThickness = 2,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, Opacity = 0,
        };
        var disc = new Border
        {
            WidthRequest = 112, HeightRequest = 112, StrokeThickness = 0, StrokeShape = new Ellipse(),
            Background = new RadialGradientBrush(
                [new GradientStop(Res("SurfaceRaised"), 0.2f), new GradientStop(Res("Surface"), 1f)]),
            Content = new Image { Source = IconExtension.Create(glyph, Res("PrimaryLight"), size: 52), WidthRequest = 52, HeightRequest = 52,
                HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
        };
        var art = new Grid { WidthRequest = 160, HeightRequest = 160, HorizontalOptions = LayoutOptions.Center, Children = { ring, disc } };

        var stack = new VerticalStackLayout
        {
            Spacing = 10, Padding = new Thickness(32, 24), VerticalOptions = LayoutOptions.Center, MaximumWidthRequest = 420,
            Children =
            {
                art,
                new Label { Text = title, FontFamily = "OpenSansSemibold", FontSize = 20, TextColor = Res("TextPrimary"),
                    HorizontalTextAlignment = TextAlignment.Center },
            },
        };
        if (!string.IsNullOrEmpty(message))
            stack.Add(new Label { Text = message, FontSize = 14, LineHeight = 1.3, TextColor = Res("TextSecondary"), HorizontalTextAlignment = TextAlignment.Center });

        var buttons = new HorizontalStackLayout { Spacing = 10, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 8, 0, 0) };
        if (primary is { Text: { } text, Command: { } command })
            buttons.Add(new Button { Text = text, Command = command });
        if (secondary is { Text: { } text2, Command: { } command2 })
            buttons.Add(new Button { Text = text2, Command = command2, BackgroundColor = Res("SurfaceRaised"), TextColor = Res("TextPrimary") });
        if (buttons.Count > 0) stack.Add(buttons);

        _pulse = stack;
        _float = disc;
        _ring = ring;
        return new ScrollView { Content = stack, VerticalOptions = LayoutOptions.Fill };
    }

    // ---------- Motion ----------

    /// <summary>
    /// One looping animation per state: a skeleton breathes (fades between dim and bright); the empty and offline art
    /// floats gently, with a ring pulsing out from it. Only while on screen.
    /// </summary>
    private void Animate()
    {
        this.AbortAnimation(nameof(StateView));
        if (!_onScreen || _mode is Mode.Hidden) return;
        var loading = _mode == Mode.Loading;
        var pulse = _pulse;
        var floating = _float;
        var ring = _ring;
        pulse.Opacity = 1;
        new Animation(t =>
        {
            if (loading)
            {
                pulse.Opacity = 0.45 + 0.4 * (0.5 - 0.5 * Math.Cos(t * 2 * Math.PI));
                return;
            }
            if (floating is not null) floating.TranslationY = -5 * Math.Sin(t * 2 * Math.PI);
            if (ring is not null)
            {
                ring.Scale = 1 + 0.4 * t;
                ring.Opacity = 0.5 * (1 - t);
            }
        }).Commit(this, nameof(StateView), length: loading ? 1400u : 2400u, easing: Easing.Linear, repeat: () => _onScreen && _mode != Mode.Hidden);
    }
}
