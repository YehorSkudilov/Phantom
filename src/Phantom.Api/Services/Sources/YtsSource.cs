using System.Text.Json;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Reads the YTS movie API: small, well-seeded encodes with IMDb ids, posters and genres, so movies
/// come in with metadata even without a TMDB key. <see cref="Source.Categories"/> is the number of
/// 50-movie pages to pull (newest first); <see cref="Source.Query"/> optionally searches instead.
/// </summary>
public class YtsSource(HttpClient http, ILogger<YtsSource> log) : IMovieSource
{
    // The API moved off the yts.* site domains, which keep changing; override with the source's base URL.
    public const string DefaultBaseUrl = "https://movies-api.accel.li";
    private const int PageSize = 50;

    public SourceType Type => SourceType.Yts;

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(source.BaseUrl) ? DefaultBaseUrl : source.BaseUrl.TrimEnd('/');
        var pages = int.TryParse(source.Categories, out var n) && n > 0 ? Math.Min(n, 20) : 4;

        var results = new List<SourceRelease>();
        for (var page = 1; page <= pages; page++)
        {
            var url = $"{baseUrl}/api/v2/list_movies.json?limit={PageSize}&page={page}&sort_by=date_added";
            if (!string.IsNullOrWhiteSpace(source.Query)) url += $"&query_term={Uri.EscapeDataString(source.Query)}";

            var batch = Parse(await http.GetStringAsync(url, ct));
            results.AddRange(batch.Releases);
            if (batch.MovieCount < PageSize) break;
        }
        log.LogDebug("YTS returned {Count} releases", results.Count);
        return results;
    }

    public static (IReadOnlyList<SourceRelease> Releases, int MovieCount) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("status", out var status) && status.GetString() != "ok")
            throw new InvalidOperationException($"YTS error: {Str(root, "status_message") ?? status.GetString()}");

        var data = root.GetProperty("data");
        if (!data.TryGetProperty("movies", out var movies) || movies.ValueKind != JsonValueKind.Array) return ([], 0);

        var results = new List<SourceRelease>();
        var count = 0;
        foreach (var m in movies.EnumerateArray())
        {
            count++;
            if (!m.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array) continue;

            var title = Str(m, "title_english") ?? Str(m, "title") ?? "";
            if (title.Length == 0) continue;
            int? year = m.TryGetProperty("year", out var y) && y.TryGetInt32(out var yv) && yv > 0 ? yv : null;
            int? runtime = m.TryGetProperty("runtime", out var r) && r.TryGetInt32(out var rv) && rv > 0 ? rv : null;
            var genres = m.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                : [];
            var imdb = Str(m, "imdb_code");
            var overview = Str(m, "description_full") is { Length: > 0 } full ? full : Str(m, "summary");
            var poster = Str(m, "large_cover_image") ?? Str(m, "medium_cover_image");

            foreach (var t in torrents.EnumerateArray())
            {
                var hash = Str(t, "hash");
                if (string.IsNullOrWhiteSpace(hash)) continue;
                var quality = Str(t, "quality");
                var kind = Str(t, "type");
                var codec = Str(t, "video_codec");
                var name = $"{title} ({year}) [{quality}] [{kind}] [YTS]";

                results.Add(new SourceRelease
                {
                    Title = title,
                    Year = year,
                    ReleaseName = name,
                    InfoHash = hash.ToLowerInvariant(),
                    MagnetUri = Magnets.Build(hash, name),
                    TorrentUrl = Str(t, "url"),
                    Quality = quality,
                    Codec = codec,
                    SizeBytes = t.TryGetProperty("size_bytes", out var s) && s.TryGetInt64(out var sv) ? sv : 0,
                    Seeders = t.TryGetProperty("seeds", out var se) && se.TryGetInt32(out var sev) ? sev : 0,
                    Leechers = t.TryGetProperty("peers", out var pe) && pe.TryGetInt32(out var pev) ? pev : 0,
                    ImdbId = string.IsNullOrWhiteSpace(imdb) ? null : imdb,
                    Overview = overview,
                    PosterUrl = poster,
                    Genres = genres,
                    RuntimeMinutes = runtime,
                });
            }
        }
        return (results, count);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
