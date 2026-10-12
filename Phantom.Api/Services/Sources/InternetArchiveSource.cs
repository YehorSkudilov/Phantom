using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MonoTorrent;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Pulls public-domain feature films from the Internet Archive. Every archive.org item
/// publishes a .torrent with HTTP web seeds, so these stream even with no peers.
/// </summary>
public partial class InternetArchiveSource(HttpClient http, ILogger<InternetArchiveSource> log) : IMovieSource
{
    public const string DefaultQuery = "collection:(feature_films) AND mediatype:(movies)";

    public SourceType Type => SourceType.InternetArchive;

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(source.BaseUrl) ? "https://archive.org" : source.BaseUrl.TrimEnd('/');
        var query = string.IsNullOrWhiteSpace(source.Query) ? DefaultQuery : source.Query;
        var rows = int.TryParse(source.Categories, out var n) && n > 0 ? n : 100;

        var url = $"{baseUrl}/advancedsearch.php?q={Uri.EscapeDataString(query)}" +
                  "&fl[]=identifier&fl[]=title&fl[]=year&fl[]=description&fl[]=subject&fl[]=creator&fl[]=runtime" +
                  $"&sort[]=downloads+desc&rows={rows}&page=1&output=json";

        using var doc = JsonDocument.Parse(await SourceRequests.RetryAsync(token => http.GetStringAsync(url, token), log, "Internet Archive search", ct));
        var docs = doc.RootElement.GetProperty("response").GetProperty("docs");

        var results = new List<SourceRelease>();
        foreach (var item in docs.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var id = item.GetProperty("identifier").GetString();
            if (string.IsNullOrEmpty(id)) continue;
            try
            {
                var release = await BuildReleaseAsync(baseUrl, id, item, ct);
                if (release is not null) results.Add(release);
            }
            catch (Exception ex) when (ex is HttpRequestException or TorrentException or TaskCanceledException)
            {
                log.LogDebug(ex, "Skipping archive item {Id}", id);
            }
        }
        return results;
    }

    private async Task<SourceRelease?> BuildReleaseAsync(string baseUrl, string id, JsonElement item, CancellationToken ct)
    {
        var torrentUrl = $"{baseUrl}/download/{id}/{id}_archive.torrent";
        var bytes = await http.GetByteArrayAsync(torrentUrl, ct);
        var torrent = Torrent.Load(bytes);
        var video = torrent.Files
            .Where(f => VideoFiles.IsVideo(f.Path))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (video is null) return null;

        var title = ReadString(item, "title") ?? id;
        var year = ReadYear(item);
        var parsed = ReleaseNameParser.Parse(video.Path);

        return new SourceRelease
        {
            Title = title,
            Year = year,
            ReleaseName = $"{title} ({Path.GetFileName(video.Path)})",
            InfoHash = torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant(),
            TorrentUrl = torrentUrl,
            SizeBytes = video.Length,
            Quality = parsed.Quality ?? "SD",
            Codec = parsed.Codec,
            Seeders = 1, // web seed
            Overview = StripHtml(ReadString(item, "description")),
            PosterUrl = $"{baseUrl}/services/img/{id}",
            Genres = ReadStrings(item, "subject").Where(LooksLikeGenre).Select(Capitalize).Distinct().Take(4).ToList(),
            Director = ReadString(item, "creator"),
            RuntimeMinutes = ParseRuntime(ReadString(item, "runtime")),
        };
    }

    private static readonly HashSet<string> KnownGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "action", "adventure", "animation", "comedy", "crime", "documentary", "drama", "family", "fantasy",
        "film noir", "noir", "history", "horror", "music", "musical", "mystery", "romance", "science fiction",
        "sci-fi", "thriller", "war", "western",
    };

    private static bool LooksLikeGenre(string s) => KnownGenres.Contains(s.Trim());

    private static string Capitalize(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());

    private static string? ReadString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Array => p.EnumerateArray().Select(x => x.ToString()).FirstOrDefault(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        };
    }

    private static IEnumerable<string> ReadStrings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return [];
        return p.ValueKind switch
        {
            JsonValueKind.Array => p.EnumerateArray().Select(x => x.ToString()).ToList(),
            JsonValueKind.String => (p.GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [],
        };
    }

    private static int? ReadYear(JsonElement e)
    {
        var s = ReadString(e, "year");
        if (s is null) return null;
        var m = YearRegex().Match(s);
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    internal static int? ParseRuntime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Formats seen: "90 min", "1:29:45", "89:12".
        var parts = s.Split(':');
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)) return h * 60 + m;
        if (parts.Length == 2 && int.TryParse(parts[0], out var mm)) return mm;
        var digits = DigitsRegex().Match(s);
        return digits.Success ? int.Parse(digits.Value, CultureInfo.InvariantCulture) : null;
    }

    private static string? StripHtml(string? s) =>
        s is null ? null : TagRegex().Replace(s, " ").Replace("&nbsp;", " ").Trim();

    [GeneratedRegex(@"(18|19|20)\d{2}")] private static partial Regex YearRegex();
    [GeneratedRegex(@"\d+")] private static partial Regex DigitsRegex();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex TagRegex();
}

public static class VideoFiles
{
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".m4v", ".mov", ".webm", ".wmv", ".mpg", ".mpeg", ".ts", ".ogv" };

    private static readonly HashSet<string> SubtitleExt = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt" };

    public static bool IsVideo(string path) =>
        VideoExt.Contains(Path.GetExtension(path)) && !path.Contains("sample", StringComparison.OrdinalIgnoreCase);

    public static bool IsSubtitle(string path) => SubtitleExt.Contains(Path.GetExtension(path));

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".ogv" => "video/ogg",
        ".ts" => "video/mp2t",
        ".mpg" or ".mpeg" => "video/mpeg",
        _ => "application/octet-stream",
    };
}
