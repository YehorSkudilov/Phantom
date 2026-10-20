using System.Globalization;
using Phantom.Shared;
using Phantom.Services;

namespace Phantom;

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// True when the value is non-null (and non-empty for strings / non-zero for numbers).
/// Pass ConverterParameter="invert" for the opposite.
/// </summary>
public class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            long l => l != 0,
            double d => d > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        return parameter as string == "invert" ? !has : has;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class BytesConverter : IValueConverter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    /// <summary>"about 12 min left" at the current speed; empty while nothing is coming in.</summary>
    public static string TimeLeft(long remainingBytes, long bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond <= 0) return "";
        var left = TimeSpan.FromSeconds((double)remainingBytes / bytesPerSecond);
        return left.TotalMinutes < 1 ? "under a minute left"
            : left.TotalHours < 1 ? $"about {Math.Ceiling(left.TotalMinutes):0} min left"
            : left.TotalDays < 1 ? $"about {(int)left.TotalHours} h {left.Minutes} min left"
            : $"about {Math.Ceiling(left.TotalDays):0} days left";
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = System.Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture);
        return parameter as string == "rate" ? $"{Format(bytes)}/s" : Format(bytes);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Converts 0–100 percentages to the 0–1 range ProgressBar expects.</summary>
public class PercentToProgressConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture) / 100.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class MinutesConverter : IValueConverter
{
    public static string Format(int? minutes) => minutes is null or 0 ? "" : minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Format(value as int?);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class TimeSpanConverter : IValueConverter
{
    public static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TimeSpan t => Format(t),
        long ms => Format(TimeSpan.FromMilliseconds(ms)),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Poster fallback: shows the brand mark when a movie has no artwork.</summary>
public class PosterConverter : IValueConverter
{
    /// <summary>
    /// Posters are shown card-sized (about 120 wide), yet stored as TMDB's 500-wide image. On a phone the 342-wide one
    /// is still sharp there and a fraction of the download, which adds up across a screen full of them. A TV's cards
    /// are no bigger, and a TV box has the least memory and CPU to decode them with.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } url) return ImageSource.FromFile("logo_mark.png");
        if (DeviceInfo.Idiom == DeviceIdiom.Phone || DeviceInfo.Idiom == DeviceIdiom.TV)
            url = url.Replace("image.tmdb.org/t/p/w500/", "image.tmdb.org/t/p/w342/", StringComparison.Ordinal);
        return ImageSource.FromUri(new Uri(url));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the value's text equals the converter parameter (case-insensitive).</summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } s ? s[..1].ToUpperInvariant() : "?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A profile's picture (see ProfileAvatars): an emoji, in its own colours, or one of the first set's Material icons in
/// white; null for none.
/// </summary>
public class AvatarIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Create(value as string);

    public static ImageSource? Create(string? avatar, double size = 64) => avatar switch
    {
        null or "" => null,
        _ when ProfileAvatars.Legacy.Contains(avatar) => Controls.IconExtension.Create(avatar, Colors.White, size: size),
        // The system's font: its emoji are drawn in colour.
        _ => new FontImageSource { Glyph = avatar, Size = size, Color = Colors.White },
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// What's left of a download (1 - progress, 0 to 1), for the grey over a poster's part not downloaded yet (its ScaleX,
/// anchored right). With the parameter "percent" the progress is 0 to 100.
/// </summary>
public class RemainingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value switch { double d => d, float f => f, _ => 0 };
        if (parameter as string == "percent") progress /= 100;
        return Math.Clamp(1 - progress, 0, 1);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A server copy's status over its poster: "42% · 3.1 MB/s" while saving, else what it's doing.</summary>
public class ServerStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not ServerDownloadDto d ? "" : d.Status switch
    {
        DownloadStatus.Downloading => $"{d.Progress / 100:0%}" + (d.DownloadRate > 0 ? $" · {BytesConverter.Format(d.DownloadRate)}/s" : "")
                                      + (ServerDownloadTracker.TimeLeft(d) is { Length: > 0 } left ? $" · {left}" : ""),
        DownloadStatus.Queued => "Waiting to start",
        DownloadStatus.FetchingMetadata => "Finding it…",
        DownloadStatus.Paused => $"Paused · {d.Progress / 100:0%}",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Completed => d.ExpiresAt is { } until ? $"Saved · until {until:d MMM}" : "Saved",
        _ => d.Status.ToString(),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"  ·  about 12 min left" after a server copy's details while it's saving, else nothing.</summary>
public class ServerTimeLeftConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ServerDownloadDto d && ServerDownloadTracker.TimeLeft(d) is { Length: > 0 } left ? $"  ·  {left}" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A source's kind as people say it: YTS, The Pirate Bay, Knaben, Internet Archive.</summary>
public class SourceTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Phantom.Shared.SourceType.InternetArchive => "Internet Archive",
        Phantom.Shared.SourceType.Yts => "YTS",
        Phantom.Shared.SourceType.PirateBay => "The Pirate Bay",
        Phantom.Shared.SourceType.Knaben => "Knaben (many sites)",
        Phantom.Shared.SourceType.Eztv => "EZTV (TV)",
        Phantom.Shared.SourceType.WorldCinema => "World cinema",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A sync interval in minutes as "every 3 hours" / "every 45 minutes".</summary>
public class IntervalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int m
        ? m % 60 == 0 ? (m == 60 ? "every hour" : $"every {m / 60} hours") : $"every {m} minutes"
        : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when every bound value is true (for IsVisible on several conditions).</summary>
public class AllTrueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) => values.All(v => v is true);
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True for a percentage still under 100 (a progress bar that's still going).</summary>
public class Below100Converter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double d && d < 100;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A backdrop for a big banner. TMDB's are served 1280 wide, soft once stretched across a desktop or TV screen, so
/// there it asks for TMDB's full-size image instead; phones keep the lighter one.
/// </summary>
public class BackdropConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } url) return ImageSource.FromFile("logo_mark.png");
        // Not on a TV: the full-size original (up to 4K) is a big decode for a TV box's memory and CPU, and the banner
        // that shows it is only half the screen there.
        if (DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet)
            url = url.Replace("image.tmdb.org/t/p/w1280/", "image.tmdb.org/t/p/original/", StringComparison.Ordinal);
        return ImageSource.FromUri(new Uri(url));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A poster's width (2:3) for a banner of the given height, less its padding.</summary>
public class PosterWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double height && height > 36 ? (height - 36) * 2 / 3 : 120d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Whether a Discover row (by its key) has a fuller list to open: not the rows picked for the profile.</summary>
public class HasSeeAllConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is string key && key is not ("foryou" or "popular") && !key.StartsWith("because:");

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
