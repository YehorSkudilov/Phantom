using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services;

/// <summary>A movie or a show, by id.</summary>
public readonly record struct TitleRef(bool IsShow, int Id);

/// <summary>What the recommender knows about a title: what it's like, how good, and how wanted.</summary>
public sealed record Candidate(
    TitleRef Ref, string Title, IReadOnlyList<string> Genres, IReadOnlyList<string> People, int? Year, double? Rating,
    DateTime AddedAt, int Seeders, int RecentViewers = 0);

/// <summary>
/// Something a profile did with a title, and how much it says about its taste: watching it to the end counts most, and
/// a rating counts for or against (a low one steers away from titles like it).
/// </summary>
public sealed record TasteSignal(TitleRef Ref, double Weight, DateTime At);

/// <summary>
/// Picks titles for one profile from the whole library, from what it has watched, rated and saved. Each title is
/// scored by how much its genres, cast and director, and era match the profile's taste, plus a little for being well
/// rated and in demand. The list is then spread out, so it isn't all the same genre. Pure: the data is loaded by
/// <see cref="DiscoverService"/>.
/// </summary>
public static class Recommender
{
    /// <summary>Half the weight of what was watched goes every this many days, so taste follows what's watched lately.</summary>
    public const double HalfLifeDays = 120;

    public sealed class Taste
    {
        public Dictionary<string, double> Genres { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> People { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, double> Decades { get; } = [];
        public bool IsEmpty => Genres.Count == 0 && People.Count == 0;
    }

    /// <summary>The profile's taste: genres, people and decades, each weighted by the signals of the titles that have them.</summary>
    public static Taste BuildTaste(IEnumerable<TasteSignal> signals, IReadOnlyDictionary<TitleRef, Candidate> titles, DateTime now)
    {
        var taste = new Taste();
        foreach (var signal in signals)
        {
            if (!titles.TryGetValue(signal.Ref, out var title)) continue;
            var age = Math.Max(0, (now - signal.At).TotalDays);
            var weight = signal.Weight * Math.Pow(0.5, age / HalfLifeDays);
            // A title with many genres says less about each of them.
            foreach (var g in title.Genres) Add(taste.Genres, g, weight / Math.Sqrt(title.Genres.Count));
            foreach (var p in title.People) Add(taste.People, p, weight * 0.5);
            if (title.Year is { } y) taste.Decades[y / 10] = taste.Decades.GetValueOrDefault(y / 10) + weight;
        }
        return taste;

        static void Add(Dictionary<string, double> d, string key, double w) => d[key] = d.GetValueOrDefault(key) + w;
    }

    /// <summary>How well <paramref name="c"/> matches <paramref name="taste"/>, and how good and wanted it is.</summary>
    public static double Score(Candidate c, Taste taste, double maxGenre, double maxPerson, double maxDecade, double maxSeeders, DateTime now)
    {
        var genre = c.Genres.Count == 0 || maxGenre <= 0 ? 0 : c.Genres.Sum(g => taste.Genres.GetValueOrDefault(g)) / maxGenre / Math.Sqrt(c.Genres.Count);
        var people = maxPerson <= 0 ? 0 : Math.Min(1, c.People.Sum(p => Math.Max(0, taste.People.GetValueOrDefault(p))) / maxPerson);
        var decade = c.Year is { } y && maxDecade > 0 ? Math.Max(0, taste.Decades.GetValueOrDefault(y / 10)) / maxDecade : 0;
        return 1.0 * genre + 0.6 * people + 0.15 * decade + Quality(c, maxSeeders, now);
    }

    /// <summary>Good, in demand, new: what counts when nothing is known about the profile's taste.</summary>
    public static double Quality(Candidate c, double maxSeeders, DateTime now) =>
        0.35 * (c.Rating ?? 5) / 10
        + 0.15 * (maxSeeders > 0 ? Math.Log(1 + c.Seeders) / Math.Log(1 + maxSeeders) : 0)
        + ((now - c.AddedAt).TotalDays < 30 ? 0.08 : 0);

    /// <summary>
    /// The best <paramref name="take"/> of <paramref name="candidates"/> for this taste, spread across genres: each pick
    /// makes others of the same main genre count a little less.
    /// </summary>
    public static List<Candidate> ForYou(IReadOnlyList<Candidate> candidates, Taste taste, DateTime now, int take = 20)
    {
        var maxGenre = taste.Genres.Values.DefaultIfEmpty(0).Max();
        var maxPerson = taste.People.Values.DefaultIfEmpty(0).Max();
        var maxDecade = taste.Decades.Values.DefaultIfEmpty(0).Max();
        var maxSeeders = candidates.Select(c => (double)c.Seeders).DefaultIfEmpty(0).Max();
        var scored = candidates.Select(c => (Item: c, Score: Score(c, taste, maxGenre, maxPerson, maxDecade, maxSeeders, now)))
            .OrderByDescending(x => x.Score).Take(take * 4).ToList();
        return Diversify(scored, take);
    }

    /// <summary>Picks from the scored list, each time favouring a genre not picked much yet.</summary>
    private static List<Candidate> Diversify(List<(Candidate Item, double Score)> scored, int take)
    {
        var picked = new List<Candidate>();
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (picked.Count < take && scored.Count > 0)
        {
            var best = 0;
            var bestScore = double.MinValue;
            for (var i = 0; i < scored.Count; i++)
            {
                var main = scored[i].Item.Genres.FirstOrDefault();
                var adjusted = scored[i].Score - (main is null ? 0 : 0.15 * genreCounts.GetValueOrDefault(main));
                if (adjusted > bestScore) (best, bestScore) = (i, adjusted);
            }
            var item = scored[best].Item;
            scored.RemoveAt(best);
            picked.Add(item);
            if (item.Genres.FirstOrDefault() is { } g) genreCounts[g] = genreCounts.GetValueOrDefault(g) + 1;
        }
        return picked;
    }

    /// <summary>Titles most like <paramref name="seed"/>: shared genres, cast and director, and era.</summary>
    public static List<Candidate> MoreLike(Candidate seed, IReadOnlyList<Candidate> candidates, DateTime now, int take = 20)
    {
        var genres = seed.Genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var people = seed.People.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxSeeders = candidates.Select(c => (double)c.Seeders).DefaultIfEmpty(0).Max();
        return candidates.Where(c => c.Ref != seed.Ref)
            .Select(c =>
            {
                var union = genres.Count + c.Genres.Count(g => !genres.Contains(g));
                var sharedGenres = union == 0 ? 0 : (double)c.Genres.Count(genres.Contains) / union;
                // Two people in common (or one director) is a strong link.
                var sharedPeople = Math.Min(2, c.People.Count(people.Contains)) / 2.0;
                var era = seed.Year is { } a && c.Year is { } b && Math.Abs(a - b) <= 10 ? 0.1 : 0;
                return (Item: c, Similarity: sharedGenres + 0.7 * sharedPeople + era, Score: sharedGenres + 0.7 * sharedPeople + era + Quality(c, maxSeeders, now));
            })
            // Something has to be shared: a top-rated film in another genre isn't "like" it.
            .Where(x => x.Similarity >= 0.34)
            .OrderByDescending(x => x.Score).Take(take).Select(x => x.Item).ToList();
    }

    /// <summary>
    /// What's popular now: watched by several people on this server lately counts most, then how many are sharing it
    /// (its torrent's seeders), then being new to the library or new out.
    /// </summary>
    public static List<Candidate> PopularNow(IReadOnlyList<Candidate> candidates, DateTime now, int take = 20)
    {
        var maxSeeders = candidates.Select(c => (double)c.Seeders).DefaultIfEmpty(0).Max();
        return candidates
            .Select(c => (Item: c, Score:
                1.2 * c.RecentViewers
                + (maxSeeders > 0 ? 1.5 * Math.Log(1 + c.Seeders) / Math.Log(1 + maxSeeders) : 0)
                + ((now - c.AddedAt).TotalDays < 30 ? 0.4 : 0)
                + (c.Year >= now.Year - 1 ? 0.5 : 0)
                + 0.2 * (c.Rating ?? 5) / 10))
            .Where(x => x.Item.RecentViewers > 0 || x.Item.Seeders > 0 || x.Item.Year >= now.Year - 1)
            .OrderByDescending(x => x.Score).Take(take).Select(x => x.Item).ToList();
    }
}

/// <summary>A row of Discover: its key, title and titles in order.</summary>
public sealed record DiscoverRow(string Key, string Title, IReadOnlyList<TitleRef> Titles);

/// <summary>Loads the library and a profile's history for <see cref="Recommender"/>, and makes Discover's rows from them.</summary>
public class DiscoverService(PhantomDbContext db, LibraryService library, ShowLibraryService shows)
{
    /// <summary>How far back "lately" goes for what's popular on this server.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    /// <summary>"More Like This" for one title: the library's titles most like it (see <see cref="Recommender.MoreLike"/>).</summary>
    public async Task<List<TitleRef>> MoreLikeAsync(ClaimsPrincipal user, TitleRef seed, CancellationToken ct, int take = 20)
    {
        var all = await CandidatesAsync(user, DateTime.UtcNow, ct);
        if (all.FirstOrDefault(c => c.Ref == seed) is not { } item) return [];
        return Recommender.MoreLike(item, all, DateTime.UtcNow, take).Select(c => c.Ref).ToList();
    }

    /// <summary>Every title this profile may see, with what the recommender needs to know about it.</summary>
    private async Task<List<Candidate>> CandidatesAsync(ClaimsPrincipal user, DateTime now, CancellationToken ct)
    {
        var since = now - RecentWindow;

        var movieViewers = await db.WatchStates.Where(w => w.LastViewedAt > since)
            .GroupBy(w => w.MovieId).Select(g => new { g.Key, Count = g.Select(w => w.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var showViewers = await db.EpisodeWatchStates.Where(w => w.LastViewedAt > since)
            .GroupBy(w => w.Episode!.ShowId).Select(g => new { g.Key, Count = g.Select(w => w.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var movies = (await library.Visible(user).Select(m => new
        {
            m.Id, m.Title, Genres = m.Genres.Select(g => g.Name).ToList(),
            People = m.Credits.Where(c => c.Kind == CreditKind.Director || c.Kind == CreditKind.Cast && c.Order < 5).Select(c => c.Name).ToList(),
            m.Year, m.Rating, m.AddedAt, Seeders = m.Releases.Max(r => (int?)r.Seeders) ?? 0,
        }).AsSplitQuery().ToListAsync(ct))
            .Select(m => new Candidate(new TitleRef(false, m.Id), m.Title, m.Genres, m.People, m.Year, m.Rating, m.AddedAt, m.Seeders,
                movieViewers.GetValueOrDefault(m.Id)));
        var showList = (await shows.Visible(user).Select(s => new
        {
            s.Id, s.Title, Genres = s.Genres.Select(g => g.Name).ToList(),
            People = s.Credits.Where(c => c.Kind != CreditKind.Cast || c.Order < 5).Select(c => c.Name).ToList(),
            s.Year, s.Rating, s.AddedAt, Seeders = db.Releases.Where(r => r.ShowId == s.Id).Max(r => (int?)r.Seeders) ?? 0,
        }).AsSplitQuery().ToListAsync(ct))
            .Select(s => new Candidate(new TitleRef(true, s.Id), s.Title, s.Genres, s.People, s.Year, s.Rating, s.AddedAt, s.Seeders,
                showViewers.GetValueOrDefault(s.Id)));
        return movies.Concat(showList).ToList();
    }

    public async Task<List<DiscoverRow>> RowsAsync(ClaimsPrincipal user, int userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var all = await CandidatesAsync(user, now, ct);
        var byRef = all.ToDictionary(c => c.Ref);

        // What this profile did: finished, part-watched, rated, saved for later.
        var movieStates = await db.WatchStates.Where(w => w.UserId == userId)
            .Select(w => new { w.MovieId, w.Watched, w.PositionMs, w.UserRating, w.LastViewedAt }).ToListAsync(ct);
        var showWatching = await db.EpisodeWatchStates.Where(w => w.UserId == userId && (w.Watched || w.PositionMs > 0))
            .GroupBy(w => w.Episode!.ShowId)
            .Select(g => new { ShowId = g.Key, Watched = g.Count(w => w.Watched), Last = g.Max(w => w.LastViewedAt) }).ToListAsync(ct);
        var watchlist = await db.Watchlist.Where(w => w.UserId == userId).Select(w => new { w.MovieId, w.AddedAt }).ToListAsync(ct);
        var showWatchlist = await db.ShowWatchlist.Where(w => w.UserId == userId).Select(w => new { w.ShowId, w.AddedAt }).ToListAsync(ct);
        var showRatings = await db.ShowRatings.Where(r => r.UserId == userId).Select(r => new { r.ShowId, r.Rating, r.RatedAt }).ToListAsync(ct);

        var signals = new List<TasteSignal>();
        foreach (var s in movieStates)
        {
            var at = s.LastViewedAt ?? now;
            // A rating says the most (10 → strongly like, 1 → strongly unlike); otherwise finishing it, or starting it.
            var weight = s.UserRating is { } r ? (r - 5.5) / 4.5 * 1.5 : s.Watched ? 1.0 : s.PositionMs > 0 ? 0.4 : 0;
            if (weight != 0) signals.Add(new TasteSignal(new TitleRef(false, s.MovieId), weight, at));
        }
        foreach (var s in showWatching)
            signals.Add(new TasteSignal(new TitleRef(true, s.ShowId), Math.Min(1.5, 0.4 + 0.1 * s.Watched), s.Last ?? now));
        signals.AddRange(watchlist.Select(w => new TasteSignal(new TitleRef(false, w.MovieId), 0.6, w.AddedAt)));
        signals.AddRange(showWatchlist.Select(w => new TasteSignal(new TitleRef(true, w.ShowId), 0.6, w.AddedAt)));
        signals.AddRange(showRatings.Select(r => new TasteSignal(new TitleRef(true, r.ShowId), (r.Rating - 5.5) / 4.5 * 1.5, r.RatedAt)));

        // Recommended: nothing already seen, started, rated or on the watchlist (those have rows of their own).
        var seen = signals.Select(s => s.Ref).ToHashSet();
        var fresh = all.Where(c => !seen.Contains(c.Ref)).ToList();
        var taste = Recommender.BuildTaste(signals, byRef, now);

        var rows = new List<DiscoverRow>
        {
            new("foryou", taste.IsEmpty ? "Top Picks" : "Recommended For You", Recommender.ForYou(fresh, taste, now).Select(c => c.Ref).ToList()),
            new("popular", "Popular Now", Recommender.PopularNow(all, now).Select(c => c.Ref).ToList()),
        };

        // "Because you watched": the two titles most recently finished (or liked), with what's most like each.
        var seeds = movieStates.Where(s => s.Watched && s.UserRating is null or >= 6).Select(s => (Ref: new TitleRef(false, s.MovieId), At: s.LastViewedAt ?? now))
            .Concat(showWatching.Where(s => s.Watched >= 2).Select(s => (Ref: new TitleRef(true, s.ShowId), At: s.Last ?? now)))
            .OrderByDescending(s => s.At).Select(s => s.Ref).Where(byRef.ContainsKey).Take(2);
        foreach (var seed in seeds)
        {
            var like = Recommender.MoreLike(byRef[seed], fresh, now);
            if (like.Count >= 4)
                rows.Add(new($"because:{(seed.IsShow ? "show" : "movie")}:{seed.Id}", $"Because You Watched {byRef[seed].Title}", like.Select(c => c.Ref).ToList()));
        }
        return rows;
    }
}
