using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Films from beyond the English-speaking world, which the other sources' catalogue crawls rarely reach: TMDB's
/// best-known films in each language of <see cref="DefaultLanguages"/>, most voted first, each looked for on Knaben
/// (which covers RuTracker, 1337x, The Pirate Bay and more) by its English title and year, then by its own. A torrent
/// counts when its name has the title and the year, and it comes in already matched to the TMDB film. Needs a TMDB key.
/// </summary>
public class WorldCinemaSource(HttpClient http, IOptions<PhantomOptions> options, ILogger<WorldCinemaSource> log) : IMovieSource, IBackfillingSource
{
    /// <summary>Chinese (Mandarin and Cantonese) and Russian, as TMDB's original-language codes.</summary>
    public const string DefaultLanguages = "zh,cn,ru";

    private const string Tmdb = "https://api.themoviedb.org/3";
    private const int PagesPerLanguage = 25; // TMDB's 20 a page: the 500 best-known films in each language
    private const int PagesPerSync = 1;
    private const int MinimumVotes = 30;

    public SourceType Type => SourceType.WorldCinema;

    private string? ApiKey => options.Value.TmdbApiKey;

    private static string[] Languages(Source source) =>
        (string.IsNullOrWhiteSpace(source.Query) ? DefaultLanguages : source.Query)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private CatalogueCrawl Crawl(Source source) => new(options.Value.DataDirectory, $"world-{source.Id}");

    public bool HasMore(Source source) => !string.IsNullOrWhiteSpace(ApiKey) && Crawl(source).HasMore;

    public double CatalogueProgress(Source source) => Crawl(source).Progress(Languages(source).Length);

    public async Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            log.LogInformation("World cinema needs a TMDB API key to know which films to look for");
            return [];
        }
        var crawl = Crawl(source);
        if (crawl.Next() is not var (step, page)) return [];
        var languages = Languages(source);
        var results = new List<SourceRelease>();
        Exception? failure = null;
        try
        {
            for (var fetched = 0; fetched < PagesPerSync && step < languages.Length; fetched++)
            {
                var (films, totalPages) = await DiscoverAsync(languages[step], page + 1, ct);
                foreach (var film in films)
                {
                    try
                    {
                        results.AddRange(await FindReleasesAsync(film, ct));
                    }
                    catch (Exception ex) when (SourceRequests.IsTransient(ex, ct))
                    {
                        log.LogDebug("World cinema search for {Title}: {Error}", film.Title, ex.Message);
                        failure = ex;
                    }
                }
                page++;
                if (page >= Math.Min(PagesPerLanguage, totalPages))
                {
                    step++;
                    page = 0;
                }
            }
        }
        catch (Exception ex) when (SourceRequests.IsTransient(ex, ct) || ex is JsonException)
        {
            log.LogWarning("World cinema at {Language} page {Page}: {Error}", languages[Math.Min(step, languages.Length - 1)], page + 1, ex.Message);
            failure = ex;
        }
        if (step >= languages.Length) crawl.Finish();
        else crawl.Save(step, page);
        if (results.Count == 0 && failure is not null) throw failure;
        return results.DistinctBy(r => r.InfoHash).ToList();
    }

    public sealed record Film(int TmdbId, string Title, string? OriginalTitle, int Year);

    private async Task<(List<Film> Films, int TotalPages)> DiscoverAsync(string language, int page, CancellationToken ct)
    {
        var url = $"{Tmdb}/discover/movie?api_key={ApiKey}&with_original_language={Uri.EscapeDataString(language)}" +
                  $"&sort_by=vote_count.desc&vote_count.gte={MinimumVotes}&include_adult=false&page={page}";
        var json = await SourceRequests.RetryAsync(token => http.GetStringAsync(url, token), log, $"TMDB films in {language}", ct);
        return ParseDiscover(json);
    }

    public static (List<Film> Films, int TotalPages) ParseDiscover(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var totalPages = root.TryGetProperty("total_pages", out var tp) && tp.TryGetInt(out var n) ? n : 0;
        var films = new List<Film>();
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("id", out var idElement) || !idElement.TryGetInt(out var id)) continue;
                var title = Str(r, "title");
                if (title is null || Str(r, "release_date") is not { Length: >= 4 } date
                    || !int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
                films.Add(new Film(id, title, Str(r, "original_title"), year));
            }
        return (films, totalPages);
    }

    /// <summary>The film's torrents on Knaben: by its English title, or failing that by its own.</summary>
    private async Task<List<SourceRelease>> FindReleasesAsync(Film film, CancellationToken ct)
    {
        var found = new List<SourceRelease>();
        foreach (var title in new[] { film.Title, film.OriginalTitle }.OfType<string>().Distinct())
        {
            await Task.Delay(400, ct); // be polite
            var body = new
            {
                search_type = "100%", search_field = "title", query = $"{title} {film.Year}", order_by = "seeders",
                order_direction = "desc", categories = new[] { KnabenSource.MoviesCategory }, from = 0, size = 50,
                hide_unsafe = true, hide_xxx = true,
            };
            var json = await SourceRequests.RetryAsync(async token =>
            {
                using var res = await http.PostAsJsonAsync(KnabenSource.DefaultBaseUrl, body, token);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync(token);
            }, log, $"Knaben search for {title}", ct);
            found.AddRange(Matches(json, film));
            if (found.Count > 0) break;
        }
        return found;
    }

    /// <summary>The hits whose names have the film's title (either) and its year, as releases of that film.</summary>
    public static IEnumerable<SourceRelease> Matches(string knabenJson, Film film)
    {
        using var doc = JsonDocument.Parse(knabenJson);
        if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) yield break;
        var titles = new[] { film.Title, film.OriginalTitle }.OfType<string>().Select(Words).Where(t => t.Length > 2).ToList();
        var year = film.Year.ToString(CultureInfo.InvariantCulture);
        foreach (var hit in hits.EnumerateArray())
        {
            var hash = Str(hit, "hash");
            var name = Str(hit, "title")?.TrimEnd('.', ' ');
            if (hash is not { Length: 40 } || string.IsNullOrWhiteSpace(name)) continue;
            var words = Words(name);
            if (!words.Contains($" {year} ") || !titles.Any(words.Contains)) continue;
            var parsed = ReleaseNameParser.Parse(name);
            var seeders = Int(hit, "seeders");
            yield return new SourceRelease
            {
                Title = film.Title,
                Year = film.Year,
                TmdbId = film.TmdbId,
                ReleaseName = name,
                InfoHash = hash.ToLowerInvariant(),
                MagnetUri = Str(hit, "magnetUrl") ?? Magnets.Build(hash, name),
                Quality = parsed.Quality,
                Codec = parsed.Codec,
                SizeBytes = hit.TryGetProperty("bytes", out var b) && b.TryGetLong(out var size) ? size : 0,
                Seeders = seeders,
                Leechers = Math.Max(0, Int(hit, "peers") - seeders),
            };
        }
    }

    /// <summary>Lower-case letters and digits only, words between single spaces, with a space at each end.</summary>
    private static string Words(string text)
    {
        var sb = new StringBuilder(" ");
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb[^1] != ' ') sb.Append(' ');
        }
        if (sb[^1] != ' ') sb.Append(' ');
        return sb.ToString();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt(out var v) ? v : 0;
}
