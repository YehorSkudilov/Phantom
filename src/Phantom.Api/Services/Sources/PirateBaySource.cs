using System.Globalization;
using System.Text.Json;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Reads The Pirate Bay through its JSON API (apibay.org). With no <see cref="Source.Query"/> it pulls the
/// top 100 of each category; with one it searches. <see cref="Source.Categories"/> is a comma-separated list
/// of TPB categories (201 Movies, 207 HD Movies, 211 UHD/4K Movies).
/// </summary>
public class PirateBaySource(HttpClient http, ILogger<PirateBaySource> log) : IMovieSource
{
    public const string DefaultBaseUrl = "https://apibay.org";
    public const string DefaultCategories = "201,207,211";

    public SourceType Type => SourceType.PirateBay;

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(source.BaseUrl) ? DefaultBaseUrl : source.BaseUrl.TrimEnd('/');
        var cats = (string.IsNullOrWhiteSpace(source.Categories) ? DefaultCategories : source.Categories)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<SourceRelease>();
        foreach (var cat in cats)
        {
            var url = string.IsNullOrWhiteSpace(source.Query)
                ? $"{baseUrl}/precompiled/data_top100_{Uri.EscapeDataString(cat)}.json"
                : $"{baseUrl}/q.php?q={Uri.EscapeDataString(source.Query)}&cat={Uri.EscapeDataString(cat)}";
            try
            {
                results.AddRange(Parse(await http.GetStringAsync(url, ct)));
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Pirate Bay category {Category} failed", cat);
            }
        }
        // The same torrent can sit in several top lists.
        return results.DistinctBy(r => r.InfoHash).ToList();
    }

    public static IReadOnlyList<SourceRelease> Parse(string json)
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
