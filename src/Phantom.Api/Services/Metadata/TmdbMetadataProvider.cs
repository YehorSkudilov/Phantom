using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Metadata;

public interface IMetadataProvider
{
    bool IsConfigured { get; }
    Task EnrichAsync(PhantomDbContext db, Movie movie, CancellationToken ct);
}

/// <summary>Fills movie metadata (art, cast, crew, trailer, certification, collection) from TMDB.</summary>
public class TmdbMetadataProvider(HttpClient http, IOptions<PhantomOptions> options, ILogger<TmdbMetadataProvider> log) : IMetadataProvider
{
    private const string Api = "https://api.themoviedb.org/3";
    private const string Images = "https://image.tmdb.org/t/p/";

    private string? ApiKey => options.Value.TmdbApiKey;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task EnrichAsync(PhantomDbContext db, Movie movie, CancellationToken ct)
    {
        if (!IsConfigured) return;
        try
        {
            var tmdbId = movie.TmdbId ?? await FindIdAsync(movie, ct);
            if (tmdbId is null) return;

            using var doc = await GetAsync($"/movie/{tmdbId}?append_to_response=credits,videos,release_dates", ct);
            var r = doc.RootElement;

            movie.TmdbId = tmdbId;
            movie.Title = Str(r, "title") ?? movie.Title;
            movie.SortTitle = ReleaseNameParser.SortTitle(movie.Title);
            movie.OriginalTitle = Str(r, "original_title");
            movie.Overview = Str(r, "overview") ?? movie.Overview;
            movie.Tagline = Str(r, "tagline");
            movie.ImdbId ??= Str(r, "imdb_id");
            movie.RuntimeMinutes = r.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : movie.RuntimeMinutes;
            movie.Rating = r.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number ? Math.Round(va.GetDouble(), 1) : movie.Rating;
            if (Str(r, "release_date") is { Length: >= 4 } date && int.TryParse(date[..4], out var y)) movie.Year = y;
            if (Str(r, "poster_path") is { } poster) movie.PosterUrl = Images + "w500" + poster;
            if (Str(r, "backdrop_path") is { } backdrop) movie.BackdropUrl = Images + "w1280" + backdrop;
            if (r.TryGetProperty("production_companies", out var pc) && pc.GetArrayLength() > 0) movie.Studio = Str(pc[0], "name");

            movie.ContentRating = Certification(r) ?? movie.ContentRating;
            movie.TrailerUrl = Trailer(r) ?? movie.TrailerUrl;

            await ApplyGenresAsync(db, movie, r, ct);
            ApplyCredits(movie, r);
            await ApplyCollectionAsync(db, movie, r, ct);
            movie.MetadataRefreshedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            log.LogWarning(ex, "TMDB lookup failed for {Title}", movie.Title);
        }
    }

    private async Task<int?> FindIdAsync(Movie movie, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(movie.ImdbId))
        {
            using var find = await GetAsync($"/find/{movie.ImdbId}?external_source=imdb_id", ct);
            var hits = find.RootElement.GetProperty("movie_results");
            if (hits.GetArrayLength() > 0) return hits[0].GetProperty("id").GetInt32();
        }

        var q = $"/search/movie?query={Uri.EscapeDataString(movie.Title)}";
        if (movie.Year is { } year) q += $"&year={year}";
        using var search = await GetAsync(q, ct);
        var results = search.RootElement.GetProperty("results");
        return results.GetArrayLength() > 0 ? results[0].GetProperty("id").GetInt32() : null;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var sep = path.Contains('?') ? '&' : '?';
        var json = await http.GetStringAsync($"{Api}{path}{sep}api_key={ApiKey}", ct);
        return JsonDocument.Parse(json);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

    private static string? Certification(JsonElement r)
    {
        if (!r.TryGetProperty("release_dates", out var rd)) return null;
        foreach (var country in rd.GetProperty("results").EnumerateArray())
        {
            if (Str(country, "iso_3166_1") != "US") continue;
            foreach (var rel in country.GetProperty("release_dates").EnumerateArray())
                if (Str(rel, "certification") is { } cert) return cert;
        }
        return null;
    }

    private static string? Trailer(JsonElement r)
    {
        if (!r.TryGetProperty("videos", out var v)) return null;
        foreach (var video in v.GetProperty("results").EnumerateArray())
            if (Str(video, "site") == "YouTube" && Str(video, "type") == "Trailer")
                return $"https://www.youtube.com/watch?v={Str(video, "key")}";
        return null;
    }

    private static async Task ApplyGenresAsync(PhantomDbContext db, Movie movie, JsonElement r, CancellationToken ct)
    {
        if (!r.TryGetProperty("genres", out var genres)) return;
        foreach (var name in genres.EnumerateArray().Select(g => Str(g, "name")).OfType<string>())
        {
            if (movie.Genres.Any(g => g.Name == name)) continue;
            var genre = db.Genres.Local.FirstOrDefault(g => g.Name == name)
                        ?? await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct)
                        ?? db.Genres.Add(new Genre { Name = name }).Entity;
            movie.Genres.Add(genre);
        }
    }

    private static void ApplyCredits(Movie movie, JsonElement r)
    {
        if (!r.TryGetProperty("credits", out var credits)) return;
        movie.Credits.Clear();
        int order = 0;
        foreach (var c in credits.GetProperty("cast").EnumerateArray().Take(20))
        {
            movie.Credits.Add(new Credit
            {
                Name = Str(c, "name") ?? "",
                Kind = CreditKind.Cast,
                Character = Str(c, "character"),
                ProfileUrl = Str(c, "profile_path") is { } p ? Images + "w185" + p : null,
                Order = order++,
            });
        }
        foreach (var c in credits.GetProperty("crew").EnumerateArray())
        {
            CreditKind? kind = Str(c, "job") switch
            {
                "Director" => CreditKind.Director,
                "Screenplay" or "Writer" => CreditKind.Writer,
                "Producer" => CreditKind.Producer,
                _ => null,
            };
            if (kind is null || movie.Credits.Count(x => x.Kind == kind) >= 4) continue;
            movie.Credits.Add(new Credit { Name = Str(c, "name") ?? "", Kind = kind.Value, Order = order++ });
        }
    }

    private static async Task ApplyCollectionAsync(PhantomDbContext db, Movie movie, JsonElement r, CancellationToken ct)
    {
        if (!r.TryGetProperty("belongs_to_collection", out var bc) || bc.ValueKind != JsonValueKind.Object) return;
        var id = bc.GetProperty("id").GetInt32();
        var collection = db.Collections.Local.FirstOrDefault(c => c.TmdbCollectionId == id)
                         ?? await db.Collections.Include(c => c.Movies).FirstOrDefaultAsync(c => c.TmdbCollectionId == id, ct);
        if (collection is null)
        {
            collection = new Collection
            {
                Name = Str(bc, "name") ?? "Collection",
                TmdbCollectionId = id,
                PosterUrl = Str(bc, "poster_path") is { } p ? Images + "w500" + p : null,
            };
            db.Collections.Add(collection);
        }
        if (!collection.Movies.Contains(movie)) collection.Movies.Add(movie);
    }
}
