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
public class LibraryController(PhantomDbContext db, LibraryService library) : ControllerBase
{
    [HttpGet("movies")]
    public async Task<PagedResult<MovieSummaryDto>> Movies([FromQuery] MovieQuery query, CancellationToken ct)
    {
        var userId = User.UserId();
        var q = library.Apply(library.Visible(User), query, userId);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var total = await q.CountAsync(ct);
        var items = await library.SummariesAsync(q.Skip((page - 1) * pageSize).Take(pageSize), userId, ct);
        return new PagedResult<MovieSummaryDto>(items, total, page, pageSize);
    }

    [HttpGet("filters")]
    public async Task<LibraryFiltersDto> Filters(CancellationToken ct)
    {
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

        await Add("continue", "Continue Watching",
            visible.Where(m => states.Any(w => w.MovieId == m.Id && !w.Watched && w.PositionMs > 0))
                .OrderByDescending(m => states.Where(w => w.MovieId == m.Id).Select(w => w.LastViewedAt).FirstOrDefault()));

        await Add("watchlist", "Watchlist",
            visible.Where(m => db.Watchlist.Any(w => w.UserId == userId && w.MovieId == m.Id))
                .OrderByDescending(m => db.Watchlist.Where(w => w.UserId == userId && w.MovieId == m.Id).Select(w => w.AddedAt).FirstOrDefault()));

        await Add("recent", "Recently Added", visible.OrderByDescending(m => m.AddedAt));

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
        await Add("ondisk", "Available Offline On Server", visible.Where(m => m.Releases.Any(r => r.LocalFilePath != null)).OrderByDescending(m => m.AddedAt));
        await Add("rediscover", "Rediscover",
            visible.Where(m => states.Any(w => w.MovieId == m.Id && w.Watched && w.LastViewedAt < DateTime.UtcNow.AddMonths(-6)))
                .OrderBy(m => EF.Functions.Random()));

        var topGenres = await visible.SelectMany(m => m.Genres).GroupBy(g => g.Name)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(5).ToListAsync(ct);
        foreach (var genre in topGenres)
            await Add($"genre:{genre}", genre, visible.Where(m => m.Genres.Any(g => g.Name == genre)).OrderByDescending(m => m.Rating).ThenByDescending(m => m.AddedAt));

        return hubs;
    }

    [HttpGet("search")]
    public async Task<SearchResultDto> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return new SearchResultDto([], [], []);
        var userId = User.UserId();
        var term = q.Trim().ToLower();
        var visible = library.Visible(User);

        var movies = await library.SummariesAsync(
            library.Apply(visible, new MovieQuery { Search = q, Sort = MovieSort.Rating }, userId).Take(40), userId, ct);
        var people = await visible.SelectMany(m => m.Credits).Where(c => c.Name.ToLower().Contains(term))
            .Select(c => c.Name).Distinct().Take(20).ToListAsync(ct);
        var genres = await db.Genres.Where(g => g.Name.ToLower().Contains(term)).Select(g => g.Name).Take(10).ToListAsync(ct);
        return new SearchResultDto(movies, people, genres);
    }
}
