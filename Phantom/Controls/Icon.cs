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

    /// <summary>The glyph's drawn size. 24 fills an IconButton (44 less its padding). An ImageButton or Button on
    /// Windows draws the glyph at this size rather than fitting it, so match it to the space: 20 for a Button's
    /// icon beside its text, larger for a larger ImageButton.</summary>
    public double Size { get; set; } = 24;

    public ImageSource ProvideValue(IServiceProvider serviceProvider) => Create(Glyph, Color, Outlined, Size);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);

    public static FontImageSource Create(string glyph, Color? color = null, bool outlined = false, double size = 24) => new()
    {
        Glyph = glyph,
        FontFamily = outlined ? "OutlinedIcons" : "RegularIcons",
        Color = color ?? (Color)Application.Current!.Resources["TextPrimary"],
        Size = size,
    };
}
