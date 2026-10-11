using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Reads The Pirate Bay through its JSON API (apibay.org), which has no full listing: each sync takes the all-time and
/// last-48-hours top 100 of every movie category, then searches the next <see cref="SearchesPerSync"/> of
/// <see cref="CrawlTerms"/> (100 results each) until the walk is done. <see cref="Source.Categories"/> lists the movie
/// categories: 201 Movies, 202 DVDR, 207 HD, 209 3D, 211 UHD/4K.
/// </summary>
public class PirateBaySource(HttpClient http, IOptions<PhantomOptions> options, ILogger<PirateBaySource> log) : IMovieSource, IBackfillingSource
{
    public const string DefaultBaseUrl = "https://apibay.org";
    public const string DefaultCategories = "201,202,207,209,211";
    private const int SearchesPerSync = 25;

    private CatalogueCrawl Crawl(Source source) => new(options.Value.DataDirectory, $"tpb-{source.Id}");

    public bool HasMore(Source source) => string.IsNullOrWhiteSpace(source.Query) && Crawl(source).HasMore;

    public SourceType Type => SourceType.PirateBay;

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(source.BaseUrl) ? DefaultBaseUrl : source.BaseUrl.TrimEnd('/');
        var cats = (string.IsNullOrWhiteSpace(source.Categories) ? DefaultCategories : source.Categories)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<SourceRelease>();
        foreach (var cat in cats)
        {
            // apibay has no full listing: the all-time top 100 and the last 48 hours' top 100 per category, or a search.
            string[] urls = string.IsNullOrWhiteSpace(source.Query)
                ? [$"{baseUrl}/precompiled/data_top100_{Uri.EscapeDataString(cat)}.json", $"{baseUrl}/precompiled/data_top100_48h_{Uri.EscapeDataString(cat)}.json"]
                : [$"{baseUrl}/q.php?q={Uri.EscapeDataString(source.Query)}&cat={Uri.EscapeDataString(cat)}"];
            foreach (var url in urls)
            {
                try
                {
                    results.AddRange(Parse(await http.GetStringAsync(url, ct)));
                }
                catch (HttpRequestException ex)
                {
                    log.LogWarning(ex, "Pirate Bay list {Url} failed", url);
                }
            }
        }
        // The keyword walk: apibay's search ignores its category filter for some queries, so filter here instead.
        var crawl = Crawl(source);
        if (string.IsNullOrWhiteSpace(source.Query) && crawl.Next() is var (step, _))
        {
            var movieCategories = cats.ToHashSet();
            var terms = CrawlTerms.All;
            for (var i = 0; i < SearchesPerSync && step < terms.Count; i++, step++)
            {
                await Task.Delay(300, ct); // be polite
                try
                {
                    results.AddRange(Parse(await http.GetStringAsync($"{baseUrl}/q.php?q={Uri.EscapeDataString(terms[step])}", ct), movieCategories));
                }
                catch (HttpRequestException ex)
                {
                    log.LogWarning(ex, "Pirate Bay search {Term} failed", terms[step]);
                }
            }
            if (step >= terms.Count) crawl.Finish();
            else crawl.Save(step, 0);
        }

        // The same torrent can sit in several lists.
        return results.DistinctBy(r => r.InfoHash).ToList();
    }

    /// <param name="categories">When set, only items in these categories (search results mix in TV, games...).</param>
    public static IReadOnlyList<SourceRelease> Parse(string json, IReadOnlySet<string>? categories = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var results = new List<SourceRelease>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var hash = Str(item, "info_hash");
            var name = Str(item, "name");
            // An empty search returns one placeholder row with a zero hash.
            if (string.IsNullOrWhiteSpace(hash) || hash.Trim('0').Length == 0 || string.IsNullOrWhiteSpace(name)) continue;
            if (categories is not null && !categories.Contains(Str(item, "category") ?? "")) continue;

            var parsed = ReleaseNameParser.Parse(name);
            var imdb = Str(item, "imdb");
            results.Add(new SourceRelease
            {
                Title = parsed.Title,
                Year = parsed.Year,
                ReleaseName = name,
                InfoHash = hash.ToLowerInvariant(),
                MagnetUri = Magnets.Build(hash, name),
                Quality = parsed.Quality,
                Codec = parsed.Codec,
                SizeBytes = Long(item, "size"),
                Seeders = (int)Long(item, "seeders"),
                Leechers = (int)Long(item, "leechers"),
                ImdbId = string.IsNullOrWhiteSpace(imdb) || !imdb.StartsWith("tt") ? null : imdb,
            });
        }
        return results;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        } : null;

    // The search endpoint sends numbers as strings, the top-100 lists as numbers.
    private static long Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
