using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services;

/// <summary>LibraryService's counterpart for shows: per-user queries, episode progress and DTO projections.</summary>
public class ShowLibraryService(PhantomDbContext db)
{
    /// <summary>Shows with something to play, honouring the profile's content-rating limit.</summary>
    public IQueryable<Show> Visible(ClaimsPrincipal user)
    {
        var q = db.Shows.AsNoTracking().Where(s => s.Releases.Any());
        if (user.MaxContentRating() is { } max)
        {
            var allowed = ContentRatings.AllowedUpTo(max).ToList();
            q = q.Where(s => s.ContentRating == null || s.ContentRating == "" || allowed.Contains(s.ContentRating));
        }
        return q;
    }

    /// <param name="sort">False for the filters alone (the library's "All" sorts movies and shows together).</param>
    public IQueryable<Show> Apply(IQueryable<Show> q, MovieQuery query, int userId, bool sort = true)
    {
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLower();
            q = q.Where(x => x.Title.ToLower().Contains(s)
                             || (x.OriginalTitle != null && x.OriginalTitle.ToLower().Contains(s))
                             || x.Credits.Any(c => c.Name.ToLower().Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(query.Genre)) q = q.Where(x => x.Genres.Any(g => g.Name == query.Genre));
        if (query.Year is { } year) q = q.Where(x => x.Year == year);
        if (query.Decade is { } decade) q = q.Where(x => x.Year >= decade && x.Year < decade + 10);
        if (!string.IsNullOrWhiteSpace(query.ContentRating)) q = q.Where(x => x.ContentRating == query.ContentRating);
        if (!string.IsNullOrWhiteSpace(query.Actor)) q = q.Where(x => x.Credits.Any(c => c.Kind == CreditKind.Cast && c.Name == query.Actor));
        if (!string.IsNullOrWhiteSpace(query.StartsWith))
        {
            var letter = query.StartsWith.ToLower();
            q = letter == "#" ? q.Where(x => x.SortTitle.CompareTo("a") < 0) : q.Where(x => x.SortTitle.StartsWith(letter));
        }

        var states = db.EpisodeWatchStates.Where(w => w.UserId == userId);
        if (query.Unwatched == true) q = q.Where(x => !states.Any(w => w.Episode!.ShowId == x.Id && w.Watched));
        if (query.Unwatched == false) q = q.Where(x => states.Any(w => w.Episode!.ShowId == x.Id && w.Watched));
        if (query.InProgress == true) q = q.Where(x => states.Any(w => w.Episode!.ShowId == x.Id && (w.Watched || w.PositionMs > 0)));

        if (!sort) return q;
        return (query.Sort, query.Descending) switch
        {
            (MovieSort.Title, false) => q.OrderBy(x => x.SortTitle),
            (MovieSort.Title, true) => q.OrderByDescending(x => x.SortTitle),
            (MovieSort.Year, false) => q.OrderBy(x => x.Year).ThenBy(x => x.SortTitle),
            (MovieSort.Year, true) => q.OrderByDescending(x => x.Year).ThenBy(x => x.SortTitle),
            (MovieSort.Rating, false) => q.OrderBy(x => x.Rating),
            (MovieSort.Rating, true) => q.OrderByDescending(x => x.Rating),
            (MovieSort.LastViewed, _) => q.OrderByDescending(x => states.Where(w => w.Episode!.ShowId == x.Id).Max(w => w.LastViewedAt)),
            (MovieSort.Random, _) => q.OrderBy(x => EF.Functions.Random()),
            (_, false) => q.OrderBy(x => x.AddedAt),
            _ => q.OrderByDescending(x => x.AddedAt),
        };
    }

    /// <summary>
    /// Shows as row items. With <paramref name="nextEpisode"/> the caption names the episode Play would start (for
    /// Continue Watching), else how many seasons there are.
    /// </summary>
    /// <remarks>
    /// The page is picked first (ids only, so sorting and LIMIT stay cheap), then everything about those shows is
    /// fetched in a handful of batched queries and worked out in memory. Counting available episodes per show in SQL
    /// meant a correlated episodes-by-releases subquery that SQLite ran for every show before sorting.
    /// </remarks>
    public async Task<List<MovieSummaryDto>> SummariesAsync(IQueryable<Show> q, int userId, CancellationToken ct, bool nextEpisode = false)
    {
        var ids = await q.Select(s => s.Id).ToListAsync(ct);
        if (ids.Count == 0) return [];

        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.Year, s.PosterUrl, s.BackdropUrl, s.Rating, s.ContentRating, s.AddedAt })
            .ToDictionaryAsync(s => s.Id, ct);
        var progress = await LoadProgressAsync(userId, ids, ct);

        var result = new List<MovieSummaryDto>(ids.Count);
        foreach (var id in ids)
        {
            if (!shows.TryGetValue(id, out var r)) continue;
            var p = progress[id];
            var playable = p.Episodes.Where(p.Coverage.Covers).ToList();
            var available = playable.Count;
            var watched = playable.Count(e => p.States.TryGetValue(e.Id, out var st) && st.Watched);
            var seasons = p.Episodes.Where(e => e.SeasonNumber > 0).Select(e => e.SeasonNumber).Distinct().Count();

            var caption = seasons switch { 0 => null, 1 => "1 season", var n => $"{n} seasons" };
            double percent = available > 0 ? Math.Round(watched * 100.0 / available, 1) : 0;
            if (nextEpisode && Next(p.Episodes, p.Coverage, p.States) is { } next)
            {
                caption = $"S{next.Episode.SeasonNumber} · E{next.Episode.EpisodeNumber}";
                // On Continue Watching the bar is the episode's, as Netflix shows it.
                percent = next.State is { DurationMs: > 0, Watched: false } st ? Math.Round(st.PositionMs * 100.0 / st.DurationMs, 1) : 0;
            }
            result.Add(new MovieSummaryDto(
                r.Id, r.Title, r.Year, r.PosterUrl, r.Rating, null, r.ContentRating,
                available > 0 && watched >= available, percent, r.AddedAt, null, r.BackdropUrl,
                MediaKind.Show, caption));
        }
        return result;
    }

    /// <summary>What row items need about each show: its episodes (numbers only), which have a release, and progress.</summary>
    private sealed record ShowProgress(List<Episode> Episodes, ReleaseCoverage Coverage, Dictionary<int, EpisodeWatchState> States);

    /// <summary>Three queries for any number of shows, instead of three per show.</summary>
    private async Task<Dictionary<int, ShowProgress>> LoadProgressAsync(int userId, List<int> showIds, CancellationToken ct)
    {
        var episodes = await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowId))
            .OrderBy(e => e.SeasonNumber == 0).ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .Select(e => new Episode { Id = e.Id, ShowId = e.ShowId, SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber })
            .ToListAsync(ct);
        var releases = await db.Releases.AsNoTracking().Where(r => r.ShowId != null && showIds.Contains(r.ShowId.Value))
            .Select(r => new { ShowId = r.ShowId!.Value, r.SeasonNumber, r.EpisodeNumber }).Distinct().ToListAsync(ct);
        var states = await db.EpisodeWatchStates.AsNoTracking()
            .Where(w => w.UserId == userId && showIds.Contains(w.Episode!.ShowId))
            .Select(w => new { w.Episode!.ShowId, State = w })
            .ToListAsync(ct);

        var episodesByShow = episodes.ToLookup(e => e.ShowId);
        var releasesByShow = releases.ToLookup(r => r.ShowId);
        var statesByShow = states.ToLookup(s => s.ShowId);
        return showIds.Distinct().ToDictionary(id => id, id => new ShowProgress(
            episodesByShow[id].ToList(),
            new ReleaseCoverage(releasesByShow[id].Select(r => (r.SeasonNumber, r.EpisodeNumber))),
            statesByShow[id].ToDictionary(s => s.State.EpisodeId, s => s.State)));
    }

    /// <summary>
    /// Each show's last watched time for a profile, as one grouped query to join on. Filtering and sorting every show by a
    /// per-show MAX subquery repeated it several times over; a join computes it once. (As a sort key over a UNION, keep
    /// the per-show MAX: EF turns this into a correlated GROUP BY there, which is slower.)
    /// </summary>
    public IQueryable<ShowLastViewed> LastViewed(int userId) =>
        db.EpisodeWatchStates.Where(w => w.UserId == userId && w.LastViewedAt != null)
            .GroupBy(w => w.Episode!.ShowId)
            .Select(g => new ShowLastViewed { ShowId = g.Key, LastViewedAt = g.Max(w => w.LastViewedAt) });

    public async Task<ShowDetailDto?> DetailAsync(ClaimsPrincipal user, int id, CancellationToken ct)
    {
        var userId = user.UserId();
        var s = await Visible(user)
            .Include(x => x.Genres)
            .Include(x => x.Credits)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;

        var (episodes, releases, states) = await LoadAsync(userId, id, ct);
        var coverage = new ReleaseCoverage(releases);
        var seasons = episodes.GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key == 0).ThenBy(g => g.Key)
            .Select(g => new SeasonDto(
                g.Key,
                g.Key == 0 ? "Specials" : $"Season {g.Key}",
                g.Count(),
                g.Count(coverage.Covers),
                g.Count(e => states.TryGetValue(e.Id, out var st) && st.Watched)))
            .ToList();
        var next = Next(episodes, coverage, states);
        var onWatchlist = await db.ShowWatchlist.AnyAsync(w => w.UserId == userId && w.ShowId == id, ct);
        var rating = await db.ShowRatings.Where(r => r.UserId == userId && r.ShowId == id).Select(r => (double?)r.Rating).FirstOrDefaultAsync(ct);

        return new ShowDetailDto(
            s.Id, s.Title, s.OriginalTitle, s.Year, s.Overview, s.PosterUrl, s.BackdropUrl, s.Rating, s.ContentRating,
            s.Network, s.Status, s.EpisodeRuntimeMinutes, s.TrailerUrl, s.ImdbId, s.TmdbId,
            s.Genres.Select(g => g.Name).OrderBy(n => n).ToList(),
            s.Credits.Where(c => c.Kind == CreditKind.Cast).OrderBy(c => c.Order).Select(c => new CastMemberDto(c.Name, c.Character, c.ProfileUrl)).ToList(),
            s.Credits.Where(c => c.Kind != CreditKind.Cast).OrderBy(c => c.Order).Select(c => new CrewMemberDto(c.Name, "Creator")).ToList(),
            seasons,
            next is null ? null : ToDto(next.Value.Episode, coverage, next.Value.State),
            onWatchlist,
            s.AddedAt,
            rating);
    }

    /// <summary>A season's episodes, or null when the show isn't visible to this profile.</summary>
    public async Task<List<EpisodeDto>?> SeasonAsync(ClaimsPrincipal user, int showId, int season, CancellationToken ct)
    {
        if (!await Visible(user).AnyAsync(s => s.Id == showId, ct)) return null;
        var (episodes, releases, states) = await LoadAsync(user.UserId(), showId, ct, season);
        var coverage = new ReleaseCoverage(releases);
        return episodes.Select(e => ToDto(e, coverage, states.GetValueOrDefault(e.Id))).ToList();
    }

    public async Task<EpisodeDetailDto?> EpisodeAsync(ClaimsPrincipal user, int episodeId, CancellationToken ct)
    {
        var episode = await db.Episodes.AsNoTracking().Include(e => e.Show).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null || !await Visible(user).AnyAsync(s => s.Id == episode.ShowId, ct)) return null;

        var (episodes, releases, states) = await LoadAsync(user.UserId(), episode.ShowId, ct);
        var coverage = new ReleaseCoverage(releases);
        var index = episodes.FindIndex(e => e.Id == episodeId);
        var next = episodes.Skip(index + 1).FirstOrDefault(coverage.Covers);
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        return new EpisodeDetailDto(
            ToDto(episode, coverage, states.GetValueOrDefault(episode.Id)),
            episode.Show!.Title, episode.Show.PosterUrl, episode.Show.BackdropUrl,
            OrderForPlayback(releases.Where(r => Covers(r, episode)), episode, settings.PreferredQuality).Select(ToReleaseDto).ToList(),
            next is null ? null : ToDto(next, coverage, states.GetValueOrDefault(next.Id)));
    }

    /// <summary>
    /// The episode to play next for this profile: the one in progress, else the one after the last watched, else the
    /// first not yet watched. Only episodes with a release count.
    /// </summary>
    public async Task<(Episode Episode, EpisodeWatchState? State)?> NextEpisodeAsync(int userId, int showId, CancellationToken ct)
    {
        var (episodes, releases, states) = await LoadAsync(userId, showId, ct);
        return Next(episodes, new ReleaseCoverage(releases), states);
    }

    private static (Episode Episode, EpisodeWatchState? State)? Next(List<Episode> episodes, ReleaseCoverage coverage, Dictionary<int, EpisodeWatchState> states)
    {
        var playable = episodes.Where(coverage.Covers).ToList();
        if (playable.Count == 0) return null;
        EpisodeWatchState? State(Episode e) => states.GetValueOrDefault(e.Id);

        var inProgress = playable.Where(e => State(e) is { Watched: false, PositionMs: > 0 })
            .MaxBy(e => State(e)!.LastViewedAt);
        if (inProgress is not null) return (inProgress, State(inProgress));

        var lastWatched = playable.Where(e => State(e) is { Watched: true }).MaxBy(e => State(e)!.LastViewedAt);
        if (lastWatched is not null)
        {
            var after = playable.SkipWhile(e => e != lastWatched).Skip(1).FirstOrDefault();
            if (after is not null) return (after, State(after));
        }

        var first = playable.FirstOrDefault(e => State(e) is not { Watched: true }) ?? playable[0];
        return (first, State(first));
    }

    /// <summary>
    /// A show's episodes in watching order (specials last), its releases and this profile's progress; with
    /// <paramref name="season"/>, only that season's episodes and progress (and the releases that can cover it).
    /// </summary>
    private async Task<(List<Episode> Episodes, List<Release> Releases, Dictionary<int, EpisodeWatchState> States)> LoadAsync(
        int userId, int showId, CancellationToken ct, int? season = null)
    {
        var episodeQuery = db.Episodes.AsNoTracking().Where(e => e.ShowId == showId);
        var releaseQuery = db.Releases.AsNoTracking().Where(r => r.ShowId == showId);
        if (season is { } n)
        {
            episodeQuery = episodeQuery.Where(e => e.SeasonNumber == n);
            releaseQuery = releaseQuery.Where(r => r.SeasonNumber == null || r.SeasonNumber == n);
        }
        var episodes = await episodeQuery
            .OrderBy(e => e.SeasonNumber == 0).ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToListAsync(ct);
        var releases = await releaseQuery.ToListAsync(ct);
        var stateQuery = db.EpisodeWatchStates.AsNoTracking().Where(w => w.UserId == userId && w.Episode!.ShowId == showId);
        if (season is { } s) stateQuery = stateQuery.Where(w => w.Episode!.SeasonNumber == s);
        var states = await stateQuery.ToDictionaryAsync(w => w.EpisodeId, ct);
        return (episodes, releases, states);
    }

    /// <summary>Whether <paramref name="release"/> has <paramref name="episode"/> in it.</summary>
    public static bool Covers(Release release, Episode episode) =>
        release.SeasonNumber is null
        || release.SeasonNumber == episode.SeasonNumber && (release.EpisodeNumber is null || release.EpisodeNumber == episode.EpisodeNumber);

    /// <summary>
    /// An episode's releases, best to play first: one on the server's disk, then the episode on its own (less to
    /// fetch before it starts, and no other viewer of the pack to share the stream with), the preferred quality, seeders.
    /// </summary>
    public static IEnumerable<Release> OrderForPlayback(IEnumerable<Release> releases, Episode episode, string? preferredQuality) =>
        releases.Where(r => Covers(r, episode))
            .OrderByDescending(r => r.LocalFilePath != null && File.Exists(r.LocalFilePath))
            .ThenByDescending(r => r.Seeders > 0)
            .ThenByDescending(r => r.EpisodeNumber != null)
            .ThenByDescending(r => preferredQuality != null && r.Quality == preferredQuality)
            .ThenByDescending(r => ReleaseNameParser.QualityRank(r.Quality))
            .ThenByDescending(r => r.Seeders);

    public static ReleaseDto ToReleaseDto(Release r) =>
        LibraryService.ToDto(r) with
        {
            Covers = r.EpisodeNumber is not null ? null : r.SeasonNumber is { } season ? $"Season {season} pack" : "Several seasons",
        };

    private static EpisodeDto ToDto(Episode e, ReleaseCoverage coverage, EpisodeWatchState? state) =>
        new(e.Id, e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.Overview, e.StillUrl, e.AirDate, e.RuntimeMinutes,
            coverage.Covers(e),
            state is null ? new PlaybackStateDto(0, 0, false, 0, null)
                : new PlaybackStateDto(state.PositionMs, state.DurationMs, state.Watched, state.ViewCount, state.LastViewedAt));
}

public sealed class ShowLastViewed
{
    public int ShowId { get; init; }
    public DateTime? LastViewedAt { get; init; }
}

/// <summary>
/// Which episodes a show's releases cover, as hash lookups: checking every release for every episode got slow for
/// long-running shows (thousands of each). Same rules as <see cref="ShowLibraryService.Covers"/>.
/// </summary>
public sealed class ReleaseCoverage
{
    private readonly bool _wholeShow;
    private readonly HashSet<int> _seasons = [];
    private readonly HashSet<(int, int)> _episodes = [];

    public ReleaseCoverage(IEnumerable<Release> releases) : this(releases.Select(r => (r.SeasonNumber, r.EpisodeNumber))) { }

    public ReleaseCoverage(IEnumerable<(int? Season, int? Episode)> releases)
    {
        foreach (var (season, episode) in releases)
        {
            if (season is not { } s) _wholeShow = true;
            else if (episode is not { } e) _seasons.Add(s);
            else _episodes.Add((s, e));
        }
    }

    public bool Covers(Episode e) =>
        _wholeShow || _seasons.Contains(e.SeasonNumber) || _episodes.Contains((e.SeasonNumber, e.EpisodeNumber));
}
