using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Reads any Torznab-compatible indexer feed (Jackett, Prowlarr, ...). The server admin
/// chooses which indexers to point this at; Phantom ships with none configured.
/// </summary>
public partial class TorznabSource(HttpClient http) : IMovieSource
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    public SourceType Type => SourceType.Torznab;

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
            throw new InvalidOperationException("Torznab source needs a base URL.");

        var cats = string.IsNullOrWhiteSpace(source.Categories) ? "2000" : source.Categories;
        var url = $"{source.BaseUrl.TrimEnd('/')}/api?t=movie&cat={Uri.EscapeDataString(cats)}&limit=100&extended=1";
        if (!string.IsNullOrWhiteSpace(source.ApiKey)) url += $"&apikey={Uri.EscapeDataString(source.ApiKey)}";
        if (!string.IsNullOrWhiteSpace(source.Query)) url += $"&q={Uri.EscapeDataString(source.Query)}";

        var xml = await http.GetStringAsync(url, ct);
        return Parse(xml);
    }

    public static IReadOnlyList<SourceRelease> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var error = doc.Root?.Name.LocalName == "error" ? doc.Root.Attribute("description")?.Value : null;
        if (error is not null) throw new InvalidOperationException($"Indexer error: {error}");

        var results = new List<SourceRelease>();
        foreach (var item in doc.Descendants("item"))
        {
            var attrs = item.Elements(Torznab + "attr")
                .GroupBy(a => (string?)a.Attribute("name") ?? "")
                .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("value"), StringComparer.OrdinalIgnoreCase);

            var name = (string?)item.Element("title");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var link = (string?)item.Element("link");
            var enclosure = (string?)item.Element("enclosure")?.Attribute("url");
            var magnet = attrs.GetValueOrDefault("magneturl")
                         ?? new[] { link, enclosure, (string?)item.Element("guid") }.FirstOrDefault(u => u?.StartsWith("magnet:") == true);
            var infoHash = attrs.GetValueOrDefault("infohash") ?? (magnet is null ? null : InfoHashFromMagnet(magnet));
            if (string.IsNullOrWhiteSpace(infoHash)) continue;

            var parsed = ReleaseNameParser.Parse(name);
            long.TryParse(attrs.GetValueOrDefault("size") ?? (string?)item.Element("size"), out var size);
            int.TryParse(attrs.GetValueOrDefault("seeders"), out var seeders);
            int.TryParse(attrs.GetValueOrDefault("peers"), out var peers);
            var imdb = attrs.GetValueOrDefault("imdbid") ?? attrs.GetValueOrDefault("imdb");
            int? tmdb = int.TryParse(attrs.GetValueOrDefault("tmdbid"), out var t) ? t : null;
            int? year = int.TryParse(attrs.GetValueOrDefault("year"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : parsed.Year;

            results.Add(new SourceRelease
            {
                Title = parsed.Title,
                Year = year,
                ReleaseName = name,
                InfoHash = infoHash.ToLowerInvariant(),
                MagnetUri = magnet,
                TorrentUrl = magnet is null ? enclosure ?? link : null,
                Quality = parsed.Quality,
                Codec = parsed.Codec,
                SizeBytes = size,
                Seeders = seeders,
                Leechers = Math.Max(0, peers - seeders),
                ImdbId = NormalizeImdb(imdb),
                TmdbId = tmdb,
            });
        }
        return results;
    }

    internal static string? InfoHashFromMagnet(string magnet)
    {
        var m = BtihRegex().Match(magnet);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string? NormalizeImdb(string? imdb)
    {
        if (string.IsNullOrWhiteSpace(imdb) || imdb == "0") return null;
        return imdb.StartsWith("tt") ? imdb : $"tt{imdb.PadLeft(7, '0')}";
    }

    [GeneratedRegex(@"xt=urn:btih:([a-fA-F0-9]{40})")] private static partial Regex BtihRegex();
}
