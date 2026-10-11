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
public class LibraryController(PhantomDbContext db, LibraryService library, ShowLibraryService shows) : ControllerBase
{
    /// <summary>A page of the library: movies, or with kind=Show the shows.</summary>
    [HttpGet("movies")]
    public async Task<PagedResult<MovieSummaryDto>> Movies([FromQuery] MovieQuery query, CancellationToken ct)
    {
        var userId = User.UserId();
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
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

    [HttpGet("filters")]
    public async Task<LibraryFiltersDto> Filters([FromQuery] MediaKind kind, CancellationToken ct)
    {
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

    /// <summary>Home screen rows, in the same spirit as Plex's hubs.</summary>
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
        var episodeStates = db.EpisodeWatchStates.Where(w => w.UserId == userId);
        var continueShows = await shows.Visible(User)
            .Select(s => new { s.Id, Last = episodeStates.Where(w => w.Episode!.ShowId == s.Id).Max(w => w.LastViewedAt) })
            .Where(x => x.Last != null && x.Last > DateTime.UtcNow.AddMonths(-6))
            .OrderByDescending(x => x.Last).Take(20).ToListAsync(ct);
        await AddMixed("continue", "Continue Watching",
            continueMovies.ToDictionary(x => x.Id, x => x.Last), continueShows.ToDictionary(x => x.Id, x => x.Last), nextEpisode: true);

        await AddMixed("watchlist", "Watchlist",
            await db.Watchlist.Where(w => w.UserId == userId).ToDictionaryAsync(w => w.MovieId, w => (DateTime?)w.AddedAt, ct),
            await db.ShowWatchlist.Where(w => w.UserId == userId).ToDictionaryAsync(w => w.ShowId, w => (DateTime?)w.AddedAt, ct));

        await Add("recent", "Recently Added", visible.OrderByDescending(m => m.AddedAt));

        // Shows bump up when a new episode comes in.
        await AddShows("shows-recent", "New On TV", shows.Visible(User).OrderByDescending(s => s.Releases.Max(r => r.AddedAt)));

        // Recommendations: unwatched movies sharing genres with what this user finished.
        var likedGenres = await db.WatchStates.Where(w => w.UserId == userId && w.Watched)
            .SelectMany(w => w.Movie!.Genres).GroupBy(g => g.Name)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3).ToListAsync(ct);
        if (likedGenres.Count > 0)
        {
            await Add("recommended", "Recommended For You",
                visible.Where(m => !states.Any(w => w.MovieId == m.Id && w.Watched) && m.Genres.Any(g => likedGenres.Contains(g.Name)))
                    .OrderByDescending(m => m.Rating));
        }

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
        var term = q.Trim().ToLower();
        var visible = library.Visible(User);

        var query = new MovieQuery { Search = q, Sort = MovieSort.Rating };
        var movies = await library.SummariesAsync(library.Apply(visible, query, userId).Take(40), userId, ct);
        var showItems = await shows.SummariesAsync(shows.Apply(shows.Visible(User), query, userId).Take(20), userId, ct);
        // Title matches first, then by rating, movies and shows together.
        var titles = movies.Concat(showItems)
            .OrderByDescending(i => i.Title.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => i.Rating ?? 0).Take(40).ToList();
        var people = await visible.SelectMany(m => m.Credits).Where(c => c.Name.ToLower().Contains(term)).Select(c => c.Name)
            .Union(shows.Visible(User).SelectMany(s => s.Credits).Where(c => c.Name.ToLower().Contains(term)).Select(c => c.Name))
            .Distinct().Take(20).ToListAsync(ct);
        var genres = await db.Genres.Where(g => g.Name.ToLower().Contains(term)).Select(g => g.Name).Take(10).ToListAsync(ct);
        return new SearchResultDto(titles, people, genres);
    }
}
