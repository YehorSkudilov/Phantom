using System.Globalization;

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
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } s ? ImageSource.FromUri(new Uri(s)) : ImageSource.FromFile("logo_mark.png");

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

/// <summary>A source's kind as people say it: YTS, The Pirate Bay, Internet Archive, Torznab indexer.</summary>
public class SourceTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Phantom.Shared.SourceType.InternetArchive => "Internet Archive",
        Phantom.Shared.SourceType.Torznab => "Torznab indexer",
        Phantom.Shared.SourceType.Yts => "YTS",
        Phantom.Shared.SourceType.PirateBay => "The Pirate Bay",
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
