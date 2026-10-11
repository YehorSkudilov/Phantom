namespace Phantom.Controls;

/// <summary>
/// A Material icon (the fonts come from AppSkeleton, see MauiProgram) as an image source:
/// <c>Source="{controls:Icon close}"</c> on an Image/ImageButton, or <c>ImageSource="{controls:Icon download}"</c> on
/// a Button. Glyphs are Material icon names, e.g. play_arrow, bookmark_add. Code that swaps icons calls
/// <see cref="Create"/>.
/// </summary>
[ContentProperty(nameof(Glyph))]
public class IconExtension : IMarkupExtension<ImageSource>
{
    public string Glyph { get; set; } = "";

    /// <summary>Defaults to TextPrimary.</summary>
    public Color? Color { get; set; }

    /// <summary>Outlined instead of filled.</summary>
    public bool Outlined { get; set; }

    /// <summary>The glyph's drawn size. Images scale it to their own size, so this only needs to be large enough to
    /// stay sharp - on Windows a glyph left small is drawn as a small bitmap and scaled up, blurry.</summary>
    public double Size { get; set; } = 48;

    public ImageSource ProvideValue(IServiceProvider serviceProvider) => Create(Glyph, Color, Outlined, Size);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);

    public static FontImageSource Create(string glyph, Color? color = null, bool outlined = false, double size = 48) => new()
    {
        Glyph = glyph,
        FontFamily = outlined ? "OutlinedIcons" : "RegularIcons",
        Color = color ?? (Color)Application.Current!.Resources["TextPrimary"],
        Size = size,
    };
}
