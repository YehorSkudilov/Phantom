using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Api.Services.Metadata;
using Phantom.Api.Services.Torrents;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movies")]
public class MoviesController(PhantomDbContext db, LibraryService library) : ControllerBase
{
    [HttpGet("{id:int}")]
    public async Task<ActionResult<MovieDetailDto>> Get(int id, CancellationToken ct) =>
        await library.DetailAsync(User, id, ct) is { } dto ? dto : NotFound();

    /// <summary>"More Like This": shares genres and ideally the director, ranked by overlap.</summary>
    [HttpGet("{id:int}/related")]
    public async Task<ActionResult<List<MovieSummaryDto>>> Related(int id, CancellationToken ct)
    {
        var movie = await db.Movies.AsNoTracking().Include(m => m.Genres).Include(m => m.Credits).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (movie is null) return NotFound();
        var genres = movie.Genres.Select(g => g.Name).ToList();
        var directors = movie.Credits.Where(c => c.Kind == CreditKind.Director).Select(c => c.Name).ToList();

        // Scored from the matches themselves (a point per shared genre, three per shared director), grouped by movie:
        // scoring every movie in the library with per-movie subqueries got slow as it grew.
        var points = db.Genres.Where(g => genres.Contains(g.Name)).SelectMany(g => g.Movies).Select(m => new { MovieId = m.Id, Points = 1 })
            .Concat(db.Credits.Where(c => c.MovieId != null && c.Kind == CreditKind.Director && directors.Contains(c.Name))
                .Select(c => new { MovieId = c.MovieId!.Value, Points = 3 }))
            .GroupBy(x => x.MovieId)
            .Select(g => new { MovieId = g.Key, Score = g.Sum(x => x.Points) });
        var ids = await library.Visible(User).Where(m => m.Id != id)
            .Join(points, m => m.Id, p => p.MovieId, (m, p) => new { m.Id, p.Score, m.Rating })
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Rating).ThenBy(x => x.Id)
            .Take(20).Select(x => x.Id).ToListAsync(ct);
        var items = await library.SummariesAsync(library.Visible(User).Where(m => ids.Contains(m.Id)), User.UserId(), ct);
        return items.OrderBy(i => ids.IndexOf(i.Id)).ToList();
    }

    [HttpPost("{id:int}/watched")]
    public Task<IActionResult> MarkWatched(int id, CancellationToken ct) => SetWatched(id, true, ct);

    [HttpPost("{id:int}/unwatched")]
    public Task<IActionResult> MarkUnwatched(int id, CancellationToken ct) => SetWatched(id, false, ct);

    private async Task<IActionResult> SetWatched(int id, bool watched, CancellationToken ct)
    {
        if (!await db.Movies.AnyAsync(m => m.Id == id, ct)) return NotFound();
        var state = await GetOrCreateStateAsync(id, ct);
        state.Watched = watched;
        state.PositionMs = 0;
        if (watched)
        {
            state.ViewCount++;
            state.LastViewedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("{id:int}/rating")]
    public async Task<IActionResult> Rate(int id, SetRatingRequest req, CancellationToken ct)
    {
        if (req.Rating is < 0 or > 10) return BadRequest("Rating must be between 0 and 10.");
        if (!await db.Movies.AnyAsync(m => m.Id == id, ct)) return NotFound();
        var state = await GetOrCreateStateAsync(id, ct);
        state.UserRating = req.Rating;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<WatchState> GetOrCreateStateAsync(int movieId, CancellationToken ct)
    {
        var userId = User.UserId();
        var state = await db.WatchStates.FindAsync([userId, movieId], ct);
        if (state is null)
        {
            state = new WatchState { UserId = userId, MovieId = movieId };
            db.WatchStates.Add(state);
        }
        return state;
    }
}
