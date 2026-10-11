using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Knaben (knaben.org): one search API over many torrent sites (The Pirate Bay, YTS, 1337x, RuTracker, ...). Each sync
/// takes this year's newest movies, then works through <see cref="CrawlTerms"/> a few pages at a time, most-seeded
/// first, until the whole movie catalogue is in.
/// </summary>
public partial class KnabenSource(HttpClient http, IOptions<PhantomOptions> options, ILogger<KnabenSource> log) : IMovieSource, IBackfillingSource
{
    public const string DefaultBaseUrl = "https://api.knaben.org/v1";
    private const int MoviesCategory = 3000000;
    private const int PageSize = 300;
    private const int MaxPagesPerTerm = 10;
    private const int RequestsPerSync = 12;

    public SourceType Type => SourceType.Knaben;

    private CatalogueCrawl Crawl(Source source) => new(options.Value.DataDirectory, $"knaben-{source.Id}");

    public bool HasMore(Source source) => Crawl(source).HasMore;

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(source.BaseUrl) ? DefaultBaseUrl : source.BaseUrl;
        var results = new List<SourceRelease>();

        // The newest first, every time.
        results.AddRange((await SearchAsync(url, DateTime.UtcNow.Year.ToString(), 0, "date", ct)).Releases);

        // Then the next stretch of the catalogue.
        var crawl = Crawl(source);
        if (crawl.Next() is var (step, page))
        {
            var terms = CrawlTerms.All;
            for (var request = 0; request < RequestsPerSync && step < terms.Count; request++)
            {
                await Task.Delay(500, ct); // be polite
                var batch = await SearchAsync(url, terms[step], page * PageSize, "seeders", ct);
                results.AddRange(batch.Releases);
                page++;
                if (batch.Hits < PageSize || page >= MaxPagesPerTerm || page * PageSize >= batch.Total)
                {
                    step++;
                    page = 0;
                }
            }
            if (step >= terms.Count) crawl.Finish();
            else crawl.Save(step, page);
            log.LogDebug("Knaben crawl at term {Step}/{Count}", step, terms.Count);
        }
        return results.DistinctBy(r => r.InfoHash).ToList();
    }

    private async Task<(IReadOnlyList<SourceRelease> Releases, int Hits, int Total)> SearchAsync(string url, string query, int from, string orderBy, CancellationToken ct)
    {
        var body = new
        {
            search_type = "100%", search_field = "title", query, order_by = orderBy, order_direction = "desc",
            categories = new[] { MoviesCategory }, from, size = PageSize, hide_unsafe = true, hide_xxx = true,
        };
        using var res = await http.PostAsJsonAsync(url, body, ct);
        res.EnsureSuccessStatusCode();
        return Parse(await res.Content.ReadAsStringAsync(ct));
    }

    public static (IReadOnlyList<SourceRelease> Releases, int Hits, int Total) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.TryGetProperty("total", out var t) && t.TryGetProperty("value", out var v) && v.TryGetInt32(out var n) ? n : 0;
        if (!root.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return ([], 0, total);

        var results = new List<SourceRelease>();
        foreach (var hit in hits.EnumerateArray())
        {
            var hash = Str(hit, "hash");
            var name = Str(hit, "title")?.TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(name) || hash.Length != 40) continue;
            if (!InCategory(hit, MoviesCategory) || TvRegex().IsMatch(name)) continue;

            var parsed = ReleaseNameParser.Parse(name);
            if (parsed.Year is null || parsed.Title.Length == 0) continue; // not recognisably a movie
            var seeders = Int(hit, "seeders");
            results.Add(new SourceRelease
            {
                Title = parsed.Title,
                Year = parsed.Year,
                ReleaseName = name,
                InfoHash = hash.ToLowerInvariant(),
                MagnetUri = Str(hit, "magnetUrl") ?? Magnets.Build(hash, name),
                Quality = parsed.Quality,
                Codec = parsed.Codec,
                SizeBytes = hit.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var size) ? size : 0,
                Seeders = seeders,
                Leechers = Math.Max(0, Int(hit, "peers") - seeders),
            });
        }
        return (results, hits.GetArrayLength(), total);
    }

    private static bool InCategory(JsonElement hit, int category) =>
        hit.TryGetProperty("categoryId", out var ids) && ids.ValueKind == JsonValueKind.Array
        && ids.EnumerateArray().Any(i => i.TryGetInt32(out var id) && id == category);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;

    // Episodes and seasons that end up under Movies.
    [GeneratedRegex(@"\bS\d{1,2}(E\d{1,3})?\b|\bSeason\s?\d", RegexOptions.IgnoreCase)]
    private static partial Regex TvRegex();
}
