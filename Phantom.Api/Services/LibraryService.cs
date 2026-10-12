using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services;

/// <summary>Builds per-user movie queries (parental filtering, watch state) and DTO projections.</summary>
public class LibraryService(PhantomDbContext db)
{
    /// <summary>Movies visible to this user, honouring the profile's content-rating limit.</summary>
    public IQueryable<Movie> Visible(ClaimsPrincipal user)
    {
        var q = db.Movies.AsNoTracking().Where(m => m.Releases.Any());
        if (user.MaxContentRating() is { } max)
        {
            var allowed = ContentRatings.AllowedUpTo(max).ToList();
            q = q.Where(m => m.ContentRating == null || m.ContentRating == "" || allowed.Contains(m.ContentRating));
        }
        return q;
    }

    /// <param name="sort">False for the filters alone (the library's "All" sorts movies and shows together).</param>
    public IQueryable<Movie> Apply(IQueryable<Movie> q, MovieQuery query, int userId, bool sort = true)
    {
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLower();
            q = q.Where(m => m.Title.ToLower().Contains(s)
                             || (m.OriginalTitle != null && m.OriginalTitle.ToLower().Contains(s))
                             || m.Credits.Any(c => c.Name.ToLower().Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(query.Genre)) q = q.Where(m => m.Genres.Any(g => g.Name == query.Genre));
        if (query.Year is { } year) q = q.Where(m => m.Year == year);
        if (query.Decade is { } decade) q = q.Where(m => m.Year >= decade && m.Year < decade + 10);
        if (!string.IsNullOrWhiteSpace(query.Quality)) q = q.Where(m => m.Releases.Any(r => r.Quality == query.Quality));
        if (!string.IsNullOrWhiteSpace(query.ContentRating)) q = q.Where(m => m.ContentRating == query.ContentRating);
        if (!string.IsNullOrWhiteSpace(query.Actor)) q = q.Where(m => m.Credits.Any(c => c.Kind == CreditKind.Cast && c.Name == query.Actor));
        if (!string.IsNullOrWhiteSpace(query.Director)) q = q.Where(m => m.Credits.Any(c => c.Kind == CreditKind.Director && c.Name == query.Director));
        if (!string.IsNullOrWhiteSpace(query.StartsWith))
        {
            var letter = query.StartsWith.ToLower();
            q = letter == "#"
                ? q.Where(m => m.SortTitle.CompareTo("a") < 0)
                : q.Where(m => m.SortTitle.StartsWith(letter));
        }

        var states = db.WatchStates.Where(w => w.UserId == userId);
        if (query.Unwatched == true) q = q.Where(m => !states.Any(w => w.MovieId == m.Id && w.Watched));
        if (query.Unwatched == false) q = q.Where(m => states.Any(w => w.MovieId == m.Id && w.Watched));
        if (query.InProgress == true) q = q.Where(m => states.Any(w => w.MovieId == m.Id && !w.Watched && w.PositionMs > 0));

        if (!sort) return q;
        return (query.Sort, query.Descending) switch
        {
            (MovieSort.Title, false) => q.OrderBy(m => m.SortTitle),
            (MovieSort.Title, true) => q.OrderByDescending(m => m.SortTitle),
            (MovieSort.Year, false) => q.OrderBy(m => m.Year).ThenBy(m => m.SortTitle),
            (MovieSort.Year, true) => q.OrderByDescending(m => m.Year).ThenBy(m => m.SortTitle),
            (MovieSort.Rating, false) => q.OrderBy(m => m.Rating),
            (MovieSort.Rating, true) => q.OrderByDescending(m => m.Rating),
            (MovieSort.Runtime, false) => q.OrderBy(m => m.RuntimeMinutes),
            (MovieSort.Runtime, true) => q.OrderByDescending(m => m.RuntimeMinutes),
            (MovieSort.LastViewed, _) => q.OrderByDescending(m => states.Where(w => w.MovieId == m.Id).Select(w => w.LastViewedAt).FirstOrDefault()),
            (MovieSort.Random, _) => q.OrderBy(m => EF.Functions.Random()),
            (_, false) => q.OrderBy(m => m.AddedAt),
            _ => q.OrderByDescending(m => m.AddedAt),
        };
    }

    public async Task<List<MovieSummaryDto>> SummariesAsync(IQueryable<Movie> q, int userId, CancellationToken ct)
    {
        var rows = await q.Select(m => new
        {
            m.Id, m.Title, m.Year, m.PosterUrl, m.BackdropUrl, m.Rating, m.RuntimeMinutes, m.ContentRating, m.AddedAt,
            Qualities = m.Releases.Select(r => r.Quality).ToList(),
            State = db.WatchStates.Where(w => w.UserId == userId && w.MovieId == m.Id)
                .Select(w => new { w.Watched, w.PositionMs, w.DurationMs }).FirstOrDefault(),
        }).ToListAsync(ct);

        return rows.Select(r => new MovieSummaryDto(
            r.Id, r.Title, r.Year, r.PosterUrl, r.Rating, r.RuntimeMinutes, r.ContentRating,
            r.State?.Watched ?? false,
            r.State is { DurationMs: > 0 } s ? Math.Round(s.PositionMs * 100.0 / s.DurationMs, 1) : 0,
            r.AddedAt,
            r.Qualities.OrderByDescending(ReleaseNameParser.QualityRank).FirstOrDefault(),
            r.BackdropUrl)).ToList();
    }

    public async Task<MovieDetailDto?> DetailAsync(ClaimsPrincipal user, int id, CancellationToken ct)
    {
        var userId = user.UserId();
        var m = await Visible(user)
            .Include(x => x.Genres)
            .Include(x => x.Credits)
            .Include(x => x.Releases)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return null;

        var state = await db.WatchStates.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId && w.MovieId == id, ct);
        var onWatchlist = await db.Watchlist.AnyAsync(w => w.UserId == userId && w.MovieId == id, ct);

        return new MovieDetailDto(
            m.Id, m.Title, m.OriginalTitle, m.Year, m.Tagline, m.Overview, m.PosterUrl, m.BackdropUrl,
            m.Rating, state?.UserRating, m.RuntimeMinutes, m.ContentRating, m.Studio, m.TrailerUrl, m.ImdbId, m.TmdbId,
            m.Genres.Select(g => g.Name).OrderBy(n => n).ToList(),
            m.Credits.Where(c => c.Kind == CreditKind.Cast).OrderBy(c => c.Order).Select(c => new CastMemberDto(c.Name, c.Character, c.ProfileUrl)).ToList(),
            m.Credits.Where(c => c.Kind != CreditKind.Cast).OrderBy(c => c.Kind).ThenBy(c => c.Order).Select(c => new CrewMemberDto(c.Name, c.Kind.ToString())).ToList(),
            m.Releases
                .OrderByDescending(r => r.LocalFilePath != null)
                .ThenByDescending(r => ReleaseNameParser.QualityRank(r.Quality))
                .ThenByDescending(r => r.Seeders)
                .Select(ToDto).ToList(),
            ToDto(state),
            onWatchlist,
            m.Releases.Any(r => r.LocalFilePath != null),
            m.AddedAt);
    }

    public static ReleaseDto ToDto(Release r) =>
        new(r.Id, r.Name, r.SourceName, r.Quality, r.Codec, r.SizeBytes, r.Seeders, r.Leechers, r.LocalFilePath != null);

    public static PlaybackStateDto ToDto(WatchState? s) =>
        s is null ? new PlaybackStateDto(0, 0, false, 0, null) : new PlaybackStateDto(s.PositionMs, s.DurationMs, s.Watched, s.ViewCount, s.LastViewedAt);

    /// <summary>Picks the release to play: one already on disk, else best quality with seeders.</summary>
    public static Release? PickRelease(IEnumerable<Release> releases, string? preferredQuality)
    {
        var list = releases.ToList();
        return list.FirstOrDefault(r => r.LocalFilePath != null && File.Exists(r.LocalFilePath))
               ?? list.Where(r => preferredQuality != null && r.Quality == preferredQuality).OrderByDescending(r => r.Seeders).FirstOrDefault()
               ?? list.OrderByDescending(r => r.Seeders > 0).ThenByDescending(r => ReleaseNameParser.QualityRank(r.Quality)).ThenByDescending(r => r.Seeders).FirstOrDefault();
    }
}
