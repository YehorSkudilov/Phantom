namespace Phantom.Controls;

/// <summary>
/// How a Download or Save on server button looks while it runs: its background fills from the left as the download
/// goes (the part done in the brand colour, the rest as before), and its border pulses to say it's working.
/// </summary>
public static class ProgressLook
{
    private const string PulseName = "ProgressPulse";

    private static Color Res(string key) => (Color)Application.Current!.Resources[key];

    /// <summary>The background at <paramref name="progress"/> (0 to 1): done in a deep tint of the brand colour.</summary>
    public static Brush Fill(double progress, Color background)
    {
        var p = (float)Math.Clamp(progress, 0, 1);
        var done = Mix(background, Res("Primary"), 0.45f);
        return new LinearGradientBrush(
            [new GradientStop(done, 0), new GradientStop(done, p), new GradientStop(background, p), new GradientStop(background, 1)],
            new Point(0, 0), new Point(1, 0));
    }

    /// <summary>Starts (or stops) the border's pulse, between the outline colour and the brand's.</summary>
    public static void Pulse(VisualElement owner, Action<Color> setStroke, bool on)
    {
        owner.AbortAnimation(PulseName);
        if (!on) return;
        var calm = Res("Outline");
        var bright = Res("PrimaryLight");
        new Animation(t => setStroke(Mix(calm, bright, (float)(0.5 - 0.5 * Math.Cos(t * 2 * Math.PI)))))
            .Commit(owner, PulseName, length: 1600, easing: Easing.Linear, repeat: () => owner.IsLoaded);
    }

    private static Color Mix(Color a, Color b, float amount) =>
        new(a.Red + (b.Red - a.Red) * amount, a.Green + (b.Green - a.Green) * amount, a.Blue + (b.Blue - a.Blue) * amount);
}
