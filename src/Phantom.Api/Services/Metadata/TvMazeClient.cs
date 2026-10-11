using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Metadata;

/// <summary>
/// TVmaze (api.tvmaze.com): free show metadata with no key, used for what TMDB doesn't give (no TMDB key, or a show it
/// can't find): poster, summary, genres, rating, network, status, and the episodes with their titles, dates and
/// pictures. It only fills in what's missing, so TMDB's details win where there are both.
/// </summary>
public partial class TvMazeClient(HttpClient http, ILogger<TvMazeClient> log)
{
    private const string Api = "https://api.tvmaze.com";

    // TVmaze allows about 20 requests every 10 seconds; the metadata workers share this pace.
    private static readonly SemaphoreSlim Pace = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(550);

    /// <summary>Fills in the show's missing details and episodes. False when TVmaze doesn't know it.</summary>
    public async Task<bool> EnrichAsync(Show show, CancellationToken ct)
    {
        try
        {
            using var doc = await FindAsync(show, ct);
            if (doc is null) return false;
            var r = doc.RootElement;

            show.Overview ??= Text(Str(r, "summary"));
            show.Status ??= Str(r, "status") is "Running" ? "Returning Series" : Str(r, "status");
            if (show.Year is null && Str(r, "premiered") is { Length: >= 4 } premiered && int.TryParse(premiered[..4], out var year)) show.Year = year;
            if (r.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
            {
                show.PosterUrl ??= Str(image, "original") ?? Str(image, "medium");
            }
            if (r.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Object
                && rating.TryGetProperty("average", out var average) && average.ValueKind == JsonValueKind.Number)
                show.Rating ??= Math.Round(average.GetDouble(), 1);
            show.Network ??= Name(r, "network") ?? Name(r, "webChannel");
            if (r.TryGetProperty("averageRuntime", out var runtime) && runtime.TryGetInt(out var minutes)) show.EpisodeRuntimeMinutes ??= minutes;
            if (r.TryGetProperty("externals", out var externals) && externals.ValueKind == JsonValueKind.Object) show.ImdbId ??= Str(externals, "imdb");

            if (r.TryGetProperty("_embedded", out var embedded) && embedded.TryGetProperty("episodes", out var episodes))
                ApplyEpisodes(show, episodes);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            log.LogDebug(ex, "TVmaze lookup failed for {Title}", show.Title);
            return false;
        }
    }

    /// <summary>The show with its episodes: by IMDb id when there is one, else by name.</summary>
    private async Task<JsonDocument?> FindAsync(Show show, CancellationToken ct)
    {
        int? id = null;
        if (!string.IsNullOrEmpty(show.ImdbId) && await GetAsync($"/lookup/shows?imdb={Uri.EscapeDataString(show.ImdbId)}", ct) is { } byImdb)
        {
            using (byImdb) id = byImdb.RootElement.TryGetProperty("id", out var i) && i.TryGetInt(out var v) ? v : null;
        }
        if (id is null)
        {
            foreach (var name in TitleVariants(show.Title))
            {
                if (await GetAsync($"/singlesearch/shows?q={Uri.EscapeDataString(name)}", ct) is not { } byName) continue;
                using (byName) id = byName.RootElement.TryGetProperty("id", out var i) && i.TryGetInt(out var v) ? v : null;
                if (id is not null) break;
            }
        }
        return id is null ? null : await GetAsync($"/shows/{id}?embed=episodes", ct);
    }

    private static void ApplyEpisodes(Show show, JsonElement episodes)
    {
        foreach (var e in episodes.EnumerateArray())
        {
            if (!e.TryGetProperty("season", out var s) || !s.TryGetInt(out var season) || season <= 0) continue;
            if (!e.TryGetProperty("number", out var n) || !n.TryGetInt(out var number)) continue;
            var episode = show.Episodes.FirstOrDefault(x => x.SeasonNumber == season && x.EpisodeNumber == number);
            if (episode is null) show.Episodes.Add(episode = new Episode { SeasonNumber = season, EpisodeNumber = number });
            episode.Title ??= Str(e, "name");
            episode.Overview ??= Text(Str(e, "summary"));
            if (e.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
                episode.StillUrl ??= Str(image, "medium") ?? Str(image, "original");
            if (episode.AirDate is null && DateOnly.TryParse(Str(e, "airdate"), CultureInfo.InvariantCulture, out var aired)) episode.AirDate = aired;
            if (e.TryGetProperty("runtime", out var rt) && rt.TryGetInt(out var minutes)) episode.RuntimeMinutes ??= minutes;
        }
    }

    /// <summary>The name as given, then without a trailing country or year ("The Office US", "Doctor Who 2005").</summary>
    public static IEnumerable<string> TitleVariants(string title)
    {
        yield return title;
        var trimmed = TrailingTagRegex().Replace(title, "").Trim();
        if (trimmed.Length > 0 && trimmed != title) yield return trimmed;
    }

    [GeneratedRegex(@"\s*\(?\b(US|UK|AU|NZ|CA|IE|19\d{2}|20\d{2})\)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingTagRegex();

    /// <summary>A GET at TVmaze's pace; null for a 404 (not found).</summary>
    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        await Pace.WaitAsync(ct);
        try
        {
            var wait = MinGap - (DateTime.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            for (var attempt = 0; ; attempt++)
            {
                _lastRequest = DateTime.UtcNow;
                using var res = await http.GetAsync(Api + path, ct);
                if (res.StatusCode == HttpStatusCode.NotFound) return null;
                // Too many at once (other servers share its limit too): wait as asked, then once more.
                if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10), ct);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            }
        }
        finally
        {
            Pace.Release();
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

    private static string? Name(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "name") : null;

    /// <summary>TVmaze summaries are HTML ("&lt;p&gt;...&lt;/p&gt;"); plain text for the app.</summary>
    private static string? Text(string? html) =>
        html is null ? null : WebUtility.HtmlDecode(HtmlTagRegex().Replace(html, "")).Trim() is { Length: > 0 } text ? text : null;

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
