using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Metadata;

public interface IMetadataProvider
{
    bool IsConfigured { get; }
    Task EnrichAsync(PhantomDbContext db, Movie movie, CancellationToken ct);

    /// <summary>Fills a show's details and its episodes (pass it with Genres, Credits and Episodes loaded).</summary>
    Task EnrichAsync(PhantomDbContext db, Show show, CancellationToken ct);
}

/// <summary>
/// Fills movie metadata (art, cast, crew, trailer, certification, collection) and show metadata (the same, plus every
/// season's episodes) from TMDB.
/// </summary>
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

            await ApplyGenresAsync(db, movie.Genres, r, ct);
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

    private static async Task ApplyGenresAsync(PhantomDbContext db, List<Genre> into, JsonElement r, CancellationToken ct)
    {
        if (!r.TryGetProperty("genres", out var genres)) return;
        // TV genres come combined ("Action & Adventure", "Sci-Fi & Fantasy"); they're split to match the movie ones.
        var names = genres.EnumerateArray().Select(g => Str(g, "name")).OfType<string>()
            .SelectMany(n => n.Split(" & ", StringSplitOptions.TrimEntries))
            .Select(n => n == "Sci-Fi" ? "Science Fiction" : n)
            .Distinct();
        foreach (var name in names)
        {
            if (into.Any(g => g.Name == name)) continue;
            var genre = db.Genres.Local.FirstOrDefault(g => g.Name == name)
                        ?? await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct)
                        ?? db.Genres.Add(new Genre { Name = name }).Entity;
            into.Add(genre);
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

    // ---------- Shows ----------

    public async Task EnrichAsync(PhantomDbContext db, Show show, CancellationToken ct)
    {
        if (!IsConfigured) return;
        try
        {
            var tmdbId = show.TmdbId ?? await FindShowIdAsync(show, ct);
            if (tmdbId is null) return;

            using var doc = await GetAsync($"/tv/{tmdbId}?append_to_response=credits,videos,content_ratings,external_ids", ct);
            var r = doc.RootElement;

            show.TmdbId = tmdbId;
            show.Title = Str(r, "name") ?? show.Title;
            show.SortTitle = ReleaseNameParser.SortTitle(show.Title);
            show.OriginalTitle = Str(r, "original_name");
            show.Overview = Str(r, "overview") ?? show.Overview;
            show.Status = Str(r, "status") ?? show.Status;
            if (r.TryGetProperty("external_ids", out var ids)) show.ImdbId ??= Str(ids, "imdb_id");
            show.Rating = r.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number ? Math.Round(va.GetDouble(), 1) : show.Rating;
            if (Str(r, "first_air_date") is { Length: >= 4 } date && int.TryParse(date[..4], out var y)) show.Year = y;
            if (Str(r, "poster_path") is { } poster) show.PosterUrl = Images + "w500" + poster;
            if (Str(r, "backdrop_path") is { } backdrop) show.BackdropUrl = Images + "w1280" + backdrop;
            if (r.TryGetProperty("networks", out var networks) && networks.GetArrayLength() > 0) show.Network = Str(networks[0], "name");
            if (r.TryGetProperty("episode_run_time", out var runtimes) && runtimes.GetArrayLength() > 0 && runtimes[0].TryGetInt32(out var runtime))
                show.EpisodeRuntimeMinutes = runtime;
            show.ContentRating = ShowCertification(r) ?? show.ContentRating;
            show.TrailerUrl = Trailer(r) ?? show.TrailerUrl;

            await ApplyGenresAsync(db, show.Genres, r, ct);
            ApplyShowCredits(show, r);
            await ApplyEpisodesAsync(show, r, ct);
            show.MetadataRefreshedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            log.LogWarning(ex, "TMDB lookup failed for show {Title}", show.Title);
        }
    }

    private async Task<int?> FindShowIdAsync(Show show, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(show.ImdbId))
        {
            using var find = await GetAsync($"/find/{show.ImdbId}?external_source=imdb_id", ct);
            var hits = find.RootElement.GetProperty("tv_results");
            if (hits.GetArrayLength() > 0) return hits[0].GetProperty("id").GetInt32();
        }

        var q = $"/search/tv?query={Uri.EscapeDataString(show.Title)}";
        if (show.Year is { } year) q += $"&first_air_date_year={year}";
        using var search = await GetAsync(q, ct);
        var results = search.RootElement.GetProperty("results");
        return results.GetArrayLength() > 0 ? results[0].GetProperty("id").GetInt32() : null;
    }

    private static string? ShowCertification(JsonElement r)
    {
        if (!r.TryGetProperty("content_ratings", out var cr)) return null;
        foreach (var country in cr.GetProperty("results").EnumerateArray())
            if (Str(country, "iso_3166_1") == "US" && Str(country, "rating") is { } rating) return rating;
        return null;
    }

    private static void ApplyShowCredits(Show show, JsonElement r)
    {
        if (!r.TryGetProperty("credits", out var credits)) return;
        show.Credits.Clear();
        int order = 0;
        foreach (var c in credits.GetProperty("cast").EnumerateArray().Take(20))
        {
            show.Credits.Add(new Credit
            {
                Name = Str(c, "name") ?? "",
                Kind = CreditKind.Cast,
                Character = Str(c, "character"),
                ProfileUrl = Str(c, "profile_path") is { } p ? Images + "w185" + p : null,
                Order = order++,
            });
        }
        // A show's creators are its writers in all but name.
        if (r.TryGetProperty("created_by", out var creators))
            foreach (var c in creators.EnumerateArray().Take(4))
                show.Credits.Add(new Credit { Name = Str(c, "name") ?? "", Kind = CreditKind.Writer, Order = order++ });
    }

    /// <summary>Adds or updates every regular season's episodes (specials, season 0, are left out).</summary>
    private async Task ApplyEpisodesAsync(Show show, JsonElement r, CancellationToken ct)
    {
        if (!r.TryGetProperty("seasons", out var seasons)) return;
        foreach (var season in seasons.EnumerateArray())
        {
            if (!season.TryGetProperty("season_number", out var sn) || !sn.TryGetInt32(out var number) || number <= 0) continue;
            using var doc = await GetAsync($"/tv/{show.TmdbId}/season/{number}", ct);
            if (!doc.RootElement.TryGetProperty("episodes", out var episodes)) continue;
            foreach (var e in episodes.EnumerateArray())
            {
                if (!e.TryGetProperty("episode_number", out var en) || !en.TryGetInt32(out var episodeNumber)) continue;
                var episode = show.Episodes.FirstOrDefault(x => x.SeasonNumber == number && x.EpisodeNumber == episodeNumber);
                if (episode is null) show.Episodes.Add(episode = new Episode { SeasonNumber = number, EpisodeNumber = episodeNumber });
                episode.TmdbId = e.TryGetProperty("id", out var id) && id.TryGetInt32(out var idValue) ? idValue : episode.TmdbId;
                episode.Title = Str(e, "name") ?? episode.Title;
                episode.Overview = Str(e, "overview") ?? episode.Overview;
                if (Str(e, "still_path") is { } still) episode.StillUrl = Images + "w300" + still;
                if (DateOnly.TryParse(Str(e, "air_date"), System.Globalization.CultureInfo.InvariantCulture, out var aired)) episode.AirDate = aired;
                if (e.TryGetProperty("runtime", out var rt) && rt.TryGetInt32(out var minutes)) episode.RuntimeMinutes = minutes;
            }
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
