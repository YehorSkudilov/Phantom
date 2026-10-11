using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Reads EZTV's API: TV episodes (and some season packs), newest first, with the show's IMDb id and the season and
/// episode numbers, so shows come in matched even without a TMDB key. Each sync pulls the newest
/// <see cref="NewestPages"/> 100-torrent pages, plus <see cref="BackfillPages"/> pages of the back catalogue until all
/// of it is in (where it got to is kept in the data folder).
/// </summary>
public class EztvSource(HttpClient http, IOptions<PhantomOptions> options, ILogger<EztvSource> log) : IMovieSource, IBackfillingSource
{
    // The site moves between domains now and then; override with the source's base URL.
    public const string DefaultBaseUrl = "https://eztvx.to";
    public const int NewestPages = 5;
    public const int BackfillPages = 20;
    private const int PageSize = 100;

    public SourceType Type => SourceType.Eztv;

    private CatalogueCrawl Crawl(Source source) => new(options.Value.DataDirectory, $"eztv-{source.Id}");

    public bool HasMore(Source source) => Crawl(source).HasMore;

    // Pages in the whole catalogue, from the last response's torrents_count.
    private static int _totalPages;

    public double CatalogueProgress(Source source) => Crawl(source).Progress(_totalPages);

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(source.BaseUrl) ? DefaultBaseUrl : source.BaseUrl.TrimEnd('/');
        var results = new List<SourceRelease>();

        for (var page = 1; page <= NewestPages; page++)
        {
            var batch = await PageAsync(baseUrl, page, ct);
            results.AddRange(batch.Releases);
            if (batch.Count < PageSize) return results;
        }

        var crawl = Crawl(source);
        if (crawl.Next() is var (step, _))
        {
            var next = Math.Max(step, NewestPages + 1);
            var finished = false;
            for (var i = 0; i < BackfillPages; i++, next++)
            {
                await Task.Delay(300, ct); // be polite
                var batch = await PageAsync(baseUrl, next, ct);
                results.AddRange(batch.Releases);
                if (batch.Count < PageSize)
                {
                    finished = true;
                    break;
                }
            }
            if (finished) crawl.Finish();
            else crawl.Save(next, 0);
            log.LogDebug("EZTV catalogue at page {Page}", next);
        }
        return results.DistinctBy(r => r.InfoHash).ToList();
    }

    private async Task<(IReadOnlyList<SourceRelease> Releases, int Count)> PageAsync(string baseUrl, int page, CancellationToken ct)
    {
        var json = await http.GetStringAsync($"{baseUrl}/api/get-torrents?limit={PageSize}&page={page}", ct);
        using (var doc = JsonDocument.Parse(json))
            if (doc.RootElement.TryGetProperty("torrents_count", out var tc) && tc.TryGetInt32(out var total))
                _totalPages = (total + PageSize - 1) / PageSize;
        return Parse(json);
    }

    public static (IReadOnlyList<SourceRelease> Releases, int Count) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array) return ([], 0);

        var results = new List<SourceRelease>();
        foreach (var t in torrents.EnumerateArray())
        {
            var hash = Str(t, "hash");
            var name = Str(t, "title") ?? Str(t, "filename");
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 40 || string.IsNullOrWhiteSpace(name)) continue;
            name = name.Replace(" EZTV", "", StringComparison.Ordinal).Trim();

            // The name says which show; the API's numbers, when it has them, say which episode ("0" means a pack).
            if (ReleaseNameParser.ParseEpisode(name) is not { } parsed) continue;
            var season = Int(t, "season") is > 0 and var s ? s : parsed.Season;
            var episode = Int(t, "episode") is > 0 and var e ? e : parsed.Episode;
            var imdb = Str(t, "imdb_id");

            results.Add(new SourceRelease
            {
                Title = parsed.Title,
                Year = parsed.Year,
                ReleaseName = name,
                InfoHash = hash.ToLowerInvariant(),
                MagnetUri = Str(t, "magnet_url") ?? Magnets.Build(hash, name),
                TorrentUrl = Str(t, "torrent_url"),
                Quality = parsed.Quality,
                Codec = parsed.Codec,
                SizeBytes = Long(t, "size_bytes"),
                Seeders = Int(t, "seeds"),
                Leechers = Int(t, "peers"),
                ImdbId = string.IsNullOrWhiteSpace(imdb) || imdb.Trim('0').Length == 0 ? null : imdb.StartsWith("tt") ? imdb : $"tt{imdb.PadLeft(7, '0')}",
                IsShow = true,
                Season = season,
                Episode = season is null ? null : episode,
            });
        }
        return (results, torrents.GetArrayLength());
    }

    // EZTV sends most numbers as strings.
    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        } : null;

    private static long Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Int(JsonElement e, string name) => (int)Math.Clamp(Long(e, name), 0, int.MaxValue);
}
