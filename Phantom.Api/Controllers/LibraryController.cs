using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/library")]
public class LibraryController(PhantomDbContext db, LibraryService library, ShowLibraryService shows, DiscoverService discover) : ControllerBase
{
    /// <summary>A page of the library: movies, or with kind=Show the shows.</summary>
    [HttpGet("movies")]
    public async Task<PagedResult<MovieSummaryDto>> Movies([FromQuery] MovieQuery query, CancellationToken ct)
    {
        var userId = User.UserId();
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        if (query.AllKinds) return await AllKindsAsync(query, userId, page, pageSize, ct);
        if (query.Kind == MediaKind.Show)
        {
            var sq = shows.Apply(shows.Visible(User), query, userId);
            var showTotal = await sq.CountAsync(ct);
            var showItems = await shows.SummariesAsync(sq.Skip((page - 1) * pageSize).Take(pageSize), userId, ct);
            return new PagedResult<MovieSummaryDto>(showItems, showTotal, page, pageSize);
        }
        var q = library.Apply(library.Visible(User), query, userId);
        var total = await q.CountAsync(ct);
        var items = await library.SummariesAsync(q.Skip((page - 1) * pageSize).Take(pageSize), userId, ct);
        return new PagedResult<MovieSummaryDto>(items, total, page, pageSize);
    }

    /// <summary>One row of the mixed library: just what sorting needs, for movies and shows alike.</summary>
    private sealed class LibraryRow
    {
        public int Id { get; init; }
        public bool IsShow { get; init; }
        public string SortTitle { get; init; } = "";
        public int? Year { get; init; }
        public double? Rating { get; init; }
        public int? Runtime { get; init; }
        public DateTime AddedAt { get; init; }
        public DateTime? LastViewed { get; init; }
    }

    /// <summary>
    /// Movies and shows as one list: both filtered as usual, put together in one query (a SQL UNION), sorted and paged
    /// there, then the page's summaries fetched by kind and put back in its order.
    /// </summary>
    private async Task<PagedResult<MovieSummaryDto>> AllKindsAsync(MovieQuery query, int userId, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<LibraryRow> rows = library.Apply(library.Visible(User), query, userId, sort: false).Select(m => new LibraryRow
        {
            Id = m.Id, IsShow = false, SortTitle = m.SortTitle, Year = m.Year, Rating = m.Rating, Runtime = m.RuntimeMinutes, AddedAt = m.AddedAt,
            LastViewed = db.WatchStates.Where(w => w.UserId == userId && w.MovieId == m.Id).Select(w => w.LastViewedAt).FirstOrDefault(),
        });
        // Shows have no quality or director: a filter on either leaves only movies.
        if (string.IsNullOrWhiteSpace(query.Quality) && string.IsNullOrWhiteSpace(query.Director))
        {
            rows = rows.Concat(shows.Apply(shows.Visible(User), query, userId, sort: false).Select(s => new LibraryRow
            {
                Id = s.Id, IsShow = true, SortTitle = s.SortTitle, Year = s.Year, Rating = s.Rating, Runtime = (int?)null, AddedAt = s.AddedAt,
                LastViewed = db.EpisodeWatchStates.Where(w => w.UserId == userId && w.Episode!.ShowId == s.Id).Max(w => w.LastViewedAt),
            }));
        }

        var ordered = (query.Sort, query.Descending) switch
        {
            (MovieSort.Title, false) => rows.OrderBy(r => r.SortTitle),
            (MovieSort.Title, true) => rows.OrderByDescending(r => r.SortTitle),
            (MovieSort.Year, false) => rows.OrderBy(r => r.Year).ThenBy(r => r.SortTitle),
            (MovieSort.Year, true) => rows.OrderByDescending(r => r.Year).ThenBy(r => r.SortTitle),
            (MovieSort.Rating, false) => rows.OrderBy(r => r.Rating),
            (MovieSort.Rating, true) => rows.OrderByDescending(r => r.Rating),
            (MovieSort.Runtime, false) => rows.OrderBy(r => r.Runtime),
            (MovieSort.Runtime, true) => rows.OrderByDescending(r => r.Runtime),
            (MovieSort.LastViewed, _) => rows.OrderByDescending(r => r.LastViewed),
            (MovieSort.Random, _) => rows.OrderBy(r => EF.Functions.Random()),
            (_, false) => rows.OrderBy(r => r.AddedAt),
            _ => rows.OrderByDescending(r => r.AddedAt),
        };

        var total = await rows.CountAsync(ct);
        var pageRows = await ordered.ThenBy(r => r.IsShow).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(r => new { r.Id, r.IsShow }).ToListAsync(ct);

        var movieIds = pageRows.Where(r => !r.IsShow).Select(r => r.Id).ToList();
        var showIds = pageRows.Where(r => r.IsShow).Select(r => r.Id).ToList();
        var movies = (await library.SummariesAsync(library.Visible(User).Where(m => movieIds.Contains(m.Id)), userId, ct)).ToDictionary(m => m.Id);
        var showItems = (await shows.SummariesAsync(shows.Visible(User).Where(s => showIds.Contains(s.Id)), userId, ct)).ToDictionary(s => s.Id);
        var items = pageRows
            .Select(r => r.IsShow ? showItems.GetValueOrDefault(r.Id) : movies.GetValueOrDefault(r.Id))
            .OfType<MovieSummaryDto>().ToList();
        return new PagedResult<MovieSummaryDto>(items, total, page, pageSize);
    }

    [HttpGet("filters")]
    public async Task<LibraryFiltersDto> Filters([FromQuery] MediaKind kind, [FromQuery] bool all, CancellationToken ct)
    {
        // Movies and shows together: both kinds' choices in one list each.
        if (all)
        {
            var m = await Filters(MediaKind.Movie, false, ct);
            var s = await Filters(MediaKind.Show, false, ct);
            return new LibraryFiltersDto(
                m.Genres.Union(s.Genres).Order().ToList(),
                m.Years.Union(s.Years).OrderDescending().ToList(),
                m.Qualities,
                m.ContentRatings.Union(s.ContentRatings).OrderBy(ContentRatings.Rank).ToList());
        }
        if (kind == MediaKind.Show)
        {
            var visibleShows = shows.Visible(User);
            var showGenres = await visibleShows.SelectMany(s => s.Genres).Select(g => g.Name).Distinct().OrderBy(n => n).ToListAsync(ct);
            var showYears = await visibleShows.Where(s => s.Year != null).Select(s => s.Year!.Value).Distinct().OrderByDescending(y => y).ToListAsync(ct);
            var showRatings = await visibleShows.Where(s => s.ContentRating != null && s.ContentRating != "").Select(s => s.ContentRating!).Distinct().ToListAsync(ct);
            return new LibraryFiltersDto(showGenres, showYears, [], showRatings.OrderBy(ContentRatings.Rank).ToList());
        }
        var visible = library.Visible(User);
        var genres = await visible.SelectMany(m => m.Genres).Select(g => g.Name).Distinct().OrderBy(n => n).ToListAsync(ct);
        var years = await visible.Where(m => m.Year != null).Select(m => m.Year!.Value).Distinct().OrderByDescending(y => y).ToListAsync(ct);
        var qualities = await visible.SelectMany(m => m.Releases).Where(r => r.Quality != null).Select(r => r.Quality!).Distinct().ToListAsync(ct);
        var ratings = await visible.Where(m => m.ContentRating != null && m.ContentRating != "").Select(m => m.ContentRating!).Distinct().ToListAsync(ct);
        return new LibraryFiltersDto(
            genres,
            years,
            qualities.OrderByDescending(ReleaseNameParser.QualityRank).ToList(),
            ratings.OrderBy(ContentRatings.Rank).ToList());
    }

    /// <summary>
    /// Discover's rows, in the same spirit as Plex's hubs: carrying on, then picked for this profile (see
    /// <see cref="Recommender"/>) and what's popular, then the library's own rows.
    /// </summary>
    [HttpGet("hubs")]
    public async Task<List<HubDto>> Hubs(CancellationToken ct)
    {
        var userId = User.UserId();
        var visible = library.Visible(User);
        var states = db.WatchStates.Where(w => w.UserId == userId);
        var hubs = new List<HubDto>();

        async Task Add(string key, string title, IQueryable<Movie> q, int take = 20)
        {
            var items = await library.SummariesAsync(q.Take(take), userId, ct);
            if (items.Count > 0) hubs.Add(new HubDto(key, title, items));
        }

        // Continue Watching: movies part-way through, and shows with an episode to carry on with (in progress, or the
        // next after the last one watched), together by when they were last watched.
        var continueMovies = await visible.Where(m => states.Any(w => w.MovieId == m.Id && !w.Watched && w.PositionMs > 0))
            .Select(m => new { m.Id, Last = states.Where(w => w.MovieId == m.Id).Select(w => w.LastViewedAt).FirstOrDefault() })
            .OrderByDescending(x => x.Last).Take(20).ToListAsync(ct);
        var since = DateTime.UtcNow.AddMonths(-6);
        var continueShows = await shows.Visible(User)
            .Join(shows.LastViewed(userId), s => s.Id, l => l.ShowId, (s, l) => new { s.Id, Last = l.LastViewedAt })
            .Where(x => x.Last > since)
            .OrderByDescending(x => x.Last).Take(20).ToListAsync(ct);
        await AddMixed("continue", "Continue Watching",
            continueMovies.ToDictionary(x => x.Id, x => x.Last), continueShows.ToDictionary(x => x.Id, x => x.Last), nextEpisode: true);

        var picks = await discover.RowsAsync(User, userId, ct);
        foreach (var row in picks) await AddRanked(row);

        await AddMixed("watchlist", "Watchlist",
            await db.Watchlist.Where(w => w.UserId == userId).ToDictionaryAsync(w => w.MovieId, w => (DateTime?)w.AddedAt, ct),
            await db.ShowWatchlist.Where(w => w.UserId == userId).ToDictionaryAsync(w => w.ShowId, w => (DateTime?)w.AddedAt, ct));

        await Add("recent", "Recently Added", visible.OrderByDescending(m => m.AddedAt));

        // Shows bump up when a new episode comes in.
        var newestRelease = db.Releases.Where(r => r.ShowId != null)
            .GroupBy(r => r.ShowId!.Value).Select(g => new { ShowId = g.Key, Newest = g.Max(r => r.AddedAt) });
        await AddShows("shows-recent", "New On TV", shows.Visible(User)
            .Join(newestRelease, s => s.Id, r => r.ShowId, (s, r) => new { Show = s, r.Newest })
            .OrderByDescending(x => x.Newest).Select(x => x.Show));

        await Add("released", "Recently Released", visible.Where(m => m.Year != null).OrderByDescending(m => m.Year).ThenByDescending(m => m.AddedAt));
        await Add("top", "Top Rated", visible.Where(m => m.Rating != null).OrderByDescending(m => m.Rating));
        await AddShows("shows-top", "Top Rated Shows", shows.Visible(User).Where(s => s.Rating != null).OrderByDescending(s => s.Rating));
        await Add("ondisk", "Available Offline On Server", visible.Where(m => m.Releases.Any(r => r.LocalFilePath != null)).OrderByDescending(m => m.AddedAt));
        await Add("rediscover", "Rediscover",
            visible.Where(m => states.Any(w => w.MovieId == m.Id && w.Watched && w.LastViewedAt < DateTime.UtcNow.AddMonths(-6)))
                .OrderBy(m => EF.Functions.Random()));

        var topGenres = await visible.SelectMany(m => m.Genres).GroupBy(g => g.Name)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(5).ToListAsync(ct);
        foreach (var genre in topGenres)
            await Add($"genre:{genre}", genre, visible.Where(m => m.Genres.Any(g => g.Name == genre)).OrderByDescending(m => m.Rating).ThenByDescending(m => m.AddedAt));

        return hubs;

        // A row of movies and shows in the order given.
        async Task AddRanked(DiscoverRow row)
        {
            var movieIds = row.Titles.Where(t => !t.IsShow).Select(t => t.Id).ToList();
            var showIds = row.Titles.Where(t => t.IsShow).Select(t => t.Id).ToList();
            var found = (await library.SummariesAsync(visible.Where(m => movieIds.Contains(m.Id)), userId, ct))
                .Select(m => (Ref: new TitleRef(false, m.Id), Item: m))
                .Concat((await shows.SummariesAsync(shows.Visible(User).Where(s => showIds.Contains(s.Id)), userId, ct))
                    .Select(s => (Ref: new TitleRef(true, s.Id), Item: s)))
                .ToDictionary(x => x.Ref, x => x.Item);
            var items = row.Titles.Where(found.ContainsKey).Select(t => found[t]).ToList();
            if (items.Count > 0) hubs.Add(new HubDto(row.Key, row.Title, items));
        }

        async Task AddShows(string key, string title, IQueryable<Show> q)
        {
            var items = await shows.SummariesAsync(q.Take(20), userId, ct);
            if (items.Count > 0) hubs.Add(new HubDto(key, title, items));
        }

        // A row of movies and shows by id, newest of the given times first.
        async Task AddMixed(string key, string title, Dictionary<int, DateTime?> movieTimes, Dictionary<int, DateTime?> showTimes, bool nextEpisode = false)
        {
            var movieIds = movieTimes.Keys.ToList();
            var showIds = showTimes.Keys.ToList();
            var movieItems = await library.SummariesAsync(visible.Where(m => movieIds.Contains(m.Id)), userId, ct);
            var showItems = await shows.SummariesAsync(shows.Visible(User).Where(s => showIds.Contains(s.Id)), userId, ct, nextEpisode);
            // A show watched to the end has nothing to carry on with.
            if (nextEpisode) showItems = showItems.Where(s => !s.Watched).ToList();
            var items = movieItems.Select(m => (Item: m, At: movieTimes[m.Id]))
                .Concat(showItems.Select(s => (Item: s, At: showTimes[s.Id])))
                .OrderByDescending(x => x.At).Take(20).Select(x => x.Item).ToList();
            if (items.Count > 0) hubs.Add(new HubDto(key, title, items));
        }
    }

    [HttpGet("search")]
    public async Task<SearchResultDto> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return new SearchResultDto([], [], []);
        var userId = User.UserId();
        var visible = library.Visible(User);

        var query = new MovieQuery { Search = q, Sort = MovieSort.Rating };
        var movies = await library.SummariesAsync(library.Apply(visible, query, userId).Take(40), userId, ct);
        var showItems = await shows.SummariesAsync(shows.Apply(shows.Visible(User), query, userId).Take(20), userId, ct);
        // Title matches first, then by rating, movies and shows together.
        var titles = movies.Concat(showItems)
            .OrderByDescending(i => i.Title.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => i.Rating ?? 0).Take(40).ToList();
        // People in one pass over the credits (rather than one over movies' and another over shows'), most credited first.
        var like = Like.Contains(q);
        var people = await db.Credits.Where(c => EF.Functions.Like(c.Name, like, Like.Escape))
            .GroupBy(c => c.Name).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(20).ToListAsync(ct);
        var genres = await db.Genres.Where(g => EF.Functions.Like(g.Name, like, Like.Escape)).Select(g => g.Name).Take(10).ToListAsync(ct);
        return new SearchResultDto(titles, people, genres);
    }
}
