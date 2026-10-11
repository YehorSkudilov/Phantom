using System.Globalization;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Where a source's walk through its whole catalogue got to, a batch per sync, kept in the data folder so it survives
/// restarts. A finished walk starts over after <see cref="RecrawlAfter"/>, to pick up what's been added since.
/// </summary>
public sealed class CatalogueCrawl(string dataDirectory, string name)
{
    public static readonly TimeSpan RecrawlAfter = TimeSpan.FromDays(7);

    private string FilePath => System.IO.Path.Combine(dataDirectory, $"crawl-{name}.txt");

    /// <summary>Where to carry on from, or null when the walk is finished and not due again yet.</summary>
    public (int Step, int Page)? Next()
    {
        if (!File.Exists(FilePath)) return (0, 0);
        var parts = File.ReadAllText(FilePath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["done", var ticks] && long.TryParse(ticks, out var t))
            return DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc) > RecrawlAfter ? (0, 0) : null;
        return parts.Length == 2 && int.TryParse(parts[0], out var step) && int.TryParse(parts[1], out var page) ? (step, page) : (0, 0);
    }

    public bool HasMore => Next() is not null;

    /// <summary>How far the walk is, as a percentage of <paramref name="totalSteps"/>: 100 once done, 0 before it starts.</summary>
    public double Progress(int totalSteps) => Next() switch
    {
        null => 100,
        var (step, _) when totalSteps > 0 => Math.Clamp(step * 100.0 / totalSteps, 0, 99.9),
        _ => 0,
    };

    public void Save(int step, int page) => File.WriteAllText(FilePath, $"{step} {page}");

    public void Finish() => File.WriteAllText(FilePath, $"done {DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)}");
}

/// <summary>Search terms that between them cover a movie catalogue: every release year, then common release tags.</summary>
public static class CrawlTerms
{
    public static IReadOnlyList<string> All { get; } =
    [
        .. Enumerable.Range(1920, DateTime.UtcNow.Year - 1920 + 2).Reverse().Select(y => y.ToString(CultureInfo.InvariantCulture)),
        "2160p", "1080p", "720p", "BluRay", "REMUX", "WEB-DL", "WEBRip", "HDR", "x265", "DVDRip",
    ];
}
