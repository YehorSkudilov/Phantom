using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Phantom.Api.Services;

public record ParsedRelease(string Title, int? Year, string? Quality, string? Codec);

/// <summary>
/// Which part of a show a release or file holds: one episode (<see cref="Episode"/> set, possibly up to
/// <see cref="LastEpisode"/>), a season pack (no episode), or several seasons / the complete series (no season).
/// </summary>
public record ParsedEpisode(string Title, int? Year, int? Season, int? Episode, int? LastEpisode, string? Quality, string? Codec)
{
    public bool IsPack => Episode is null;
}

/// <summary>
/// Extracts title / year / quality / codec from scene-style release names such as
/// "The.Movie.Title.2010.1080p.BluRay.x264-GROUP".
/// </summary>
public static partial class ReleaseNameParser
{
    [GeneratedRegex(@"(?<!\d)(19[0-9]{2}|20[0-9]{2})(?!\d)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b(2160p|4k|uhd|1080p|1080i|720p|576p|480p|360p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"\b(x ?265|h ?265|hevc|x ?264|h ?264|avc|av1|xvid|divx|vp9|mpeg-?2|mpeg-?4)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"\b(bluray|blu-ray|bdrip|brrip|web-?dl|webrip|web|hdtv|dvdrip|dvd|hdrip|remux|proper|repack|extended|unrated|imax|hdr|10bit|dts|aac|ac3|ddp?5\.1|atmos)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JunkRegex();

    [GeneratedRegex(@"[\[\(\{].*?[\]\)\}]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    // S01E02, S01E02E03, S01E02-E03, S01 E02, s1e2
    [GeneratedRegex(@"\bS(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,3})(?:(?:[ ._-]?-?[ ._-]?E|-)(?<last>\d{1,3}))?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex();

    // 1x02, 01x02-03
    [GeneratedRegex(@"(?<![\dx])(?<s>\d{1,2})x(?<e>\d{2,3})(?:-(?<last>\d{2,3}))?(?![\dp])", RegexOptions.IgnoreCase)]
    private static partial Regex CrossEpisodeRegex();

    // S01 / S01-S03 / Season 1 / Season 1-3 / Seasons 1 to 3
    [GeneratedRegex(@"\b(?:S(?<s>\d{1,2})(?:[ ._-]*-[ ._-]*S?(?<last>\d{1,2}))?|Seasons?[ ._-]*(?<s>\d{1,2})(?:[ ._-]*(?:-|to|&)[ ._-]*(?<last>\d{1,2}))?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex();

    [GeneratedRegex(@"\b(complete[ ._-]*series|the[ ._-]*complete|all[ ._-]*seasons|complete)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompleteRegex();

    // A bare episode number in a pack's file name: "E05", "Episode 5", "Ep.05", or a leading "05 - Title".
    [GeneratedRegex(@"(?:\b(?:E|Ep|Episode)[ ._-]?(?<e>\d{1,3})\b|^(?<e>\d{1,3})(?=[ ._-]))", RegexOptions.IgnoreCase)]
    private static partial Regex BareEpisodeRegex();

    /// <summary>
    /// Reads a TV release name ("Show.Name.S02E05.1080p.WEB.h264-GRP", "Show Name Season 2 Complete 720p",
    /// "Show.Name.2019.S01-S03"), or null when it isn't one.
    /// </summary>
    public static ParsedEpisode? ParseEpisode(string releaseName)
    {
        var name = Regex.Replace(releaseName.Trim(), @"\.(mkv|mp4|avi|m4v|mov|wmv|webm|ts)$", "", RegexOptions.IgnoreCase);
        var spaced = name.Replace('.', ' ').Replace('_', ' ');

        int? season = null, episode = null, last = null;
        var marker = SeasonEpisodeRegex().Match(spaced);
        if (!marker.Success) marker = CrossEpisodeRegex().Match(spaced);
        if (marker.Success)
        {
            season = int.Parse(marker.Groups["s"].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(marker.Groups["e"].Value, CultureInfo.InvariantCulture);
            if (marker.Groups["last"].Success && int.Parse(marker.Groups["last"].Value, CultureInfo.InvariantCulture) is var l && l > episode) last = l;
        }
        else
        {
            marker = SeasonRegex().Match(spaced);
            if (marker.Success)
            {
                var first = int.Parse(marker.Groups["s"].Value, CultureInfo.InvariantCulture);
                // A span of seasons is one torrent of several, like a complete series.
                season = marker.Groups["last"].Success && int.Parse(marker.Groups["last"].Value, CultureInfo.InvariantCulture) > first ? null : first;
            }
            else
            {
                marker = CompleteRegex().Match(spaced);
                if (!marker.Success) return null;
            }
        }
        if (marker.Index == 0) return null; // no show name in front of it

        var parsed = Parse(spaced[..marker.Index]);
        var tags = Parse(spaced);
        return new ParsedEpisode(parsed.Title, parsed.Year, season, episode, last, tags.Quality, tags.Codec);
    }

    /// <summary>
    /// Season and episode of a video file inside a show's torrent, from its path ("Season 2/Show.S02E05.mkv",
    /// "S02/05 - Title.mkv"). <paramref name="packSeason"/> is the season the whole torrent is for, if it's one season.
    /// </summary>
    public static (int? Season, int? Episode) EpisodeOfFile(string path, int? packSeason)
    {
        var file = Path.GetFileNameWithoutExtension(path).Replace('.', ' ').Replace('_', ' ');
        var m = SeasonEpisodeRegex().Match(file);
        if (!m.Success) m = CrossEpisodeRegex().Match(file);
        if (m.Success)
            return (int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture));

        // Only an episode number in the name: the season is the folder's, or the pack's.
        var bare = BareEpisodeRegex().Match(file);
        if (!bare.Success) return (null, null);
        int? season = packSeason;
        foreach (var folder in (Path.GetDirectoryName(path) ?? "").Split('/', '\\').Reverse())
        {
            var s = SeasonRegex().Match(folder.Replace('.', ' ').Replace('_', ' '));
            if (s.Success && !s.Groups["last"].Success)
            {
                season = int.Parse(s.Groups["s"].Value, CultureInfo.InvariantCulture);
                break;
            }
        }
        return (season, int.Parse(bare.Groups["e"].Value, CultureInfo.InvariantCulture));
    }

    public static ParsedRelease Parse(string releaseName)
    {
        var name = releaseName.Trim();
        // Strip a trailing file extension.
        name = Regex.Replace(name, @"\.(mkv|mp4|avi|m4v|mov|wmv|webm|ts)$", "", RegexOptions.IgnoreCase);

        var spaced = name.Replace('.', ' ').Replace('_', ' ');
        var quality = NormalizeQuality(QualityRegex().Match(spaced).Value);
        var codec = NormalizeCodec(CodecRegex().Match(spaced).Value);

        // Technical tags (quality, codec, source, ...) mark the end of the title.
        int cut = spaced.Length;
        foreach (var rx in new[] { QualityRegex(), CodecRegex(), JunkRegex() })
        {
            var m = rx.Match(spaced);
            if (m.Success && m.Index > 0) cut = Math.Min(cut, m.Index);
        }

        // The release year is the last year before those tags; a year at the very start is the
        // title itself ("2012"), and earlier years can be part of it ("Blade Runner 2049 (2017)").
        int? year = null;
        var yearMatch = YearRegex().Matches(spaced).LastOrDefault(m => m.Index > 0 && m.Index < cut);
        if (yearMatch is not null)
        {
            year = int.Parse(yearMatch.Value, CultureInfo.InvariantCulture);
            cut = yearMatch.Index;
        }

        var title = spaced[..cut];
        title = BracketRegex().Replace(title, " ");
        title = title.Trim(' ', '-', '(', '[', '{');
        title = MultiSpaceRegex().Replace(title, " ").Trim();
        if (title.Length == 0) title = spaced.Trim();

        return new ParsedRelease(title, year, quality, codec);
    }

    public static string? NormalizeQuality(string? q) => q?.ToLowerInvariant() switch
    {
        "2160p" or "4k" or "uhd" => "2160p",
        "1080p" or "1080i" => "1080p",
        "720p" => "720p",
        "576p" or "480p" or "360p" => "SD",
        _ => null,
    };

    private static string? NormalizeCodec(string? c)
    {
        if (string.IsNullOrEmpty(c)) return null;
        var k = c.ToLowerInvariant().Replace(".", "").Replace("-", "").Replace(" ", "");
        return k switch
        {
            "x265" or "h265" or "hevc" => "HEVC",
            "x264" or "h264" or "avc" => "H.264",
            "av1" => "AV1",
            "vp9" => "VP9",
            "xvid" or "divx" or "mpeg4" => "MPEG-4",
            "mpeg2" => "MPEG-2",
            _ => c.ToUpperInvariant(),
        };
    }

    /// <summary>Lowercase alphanumeric key used to de-duplicate titles across sources.</summary>
    public static string NormalizeKey(string title, int? year)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return year is null ? sb.ToString() : $"{sb}-{year}";
    }

    /// <summary>"The Matrix" → "Matrix, The" style sort key, like Plex.</summary>
    public static string SortTitle(string title)
    {
        foreach (var article in new[] { "The ", "A ", "An " })
        {
            if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase) && title.Length > article.Length)
                return title[article.Length..].Trim().ToLowerInvariant();
        }
        return title.ToLowerInvariant();
    }

    public static int QualityRank(string? quality) => quality switch
    {
        "2160p" => 4,
        "1080p" => 3,
        "720p" => 2,
        "SD" => 1,
        _ => 0,
    };
}

public static class ContentRatings
{
    // Ordered from least to most restrictive audience.
    private static readonly string[] Ladder = ["G", "TV-Y", "TV-G", "PG", "TV-PG", "PG-13", "TV-14", "R", "TV-MA", "NC-17"];

    public static int Rank(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating)) return -1;
        var i = Array.FindIndex(Ladder, r => r.Equals(rating.Trim(), StringComparison.OrdinalIgnoreCase));
        return i < 0 ? -1 : i;
    }

    /// <summary>All ratings at or below <paramref name="max"/>; used for parental controls.</summary>
    public static IReadOnlyList<string> AllowedUpTo(string max)
    {
        var r = Rank(max);
        return r < 0 ? [] : Ladder[..(r + 1)];
    }
}
