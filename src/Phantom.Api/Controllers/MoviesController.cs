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

        var q = library.Visible(User)
            .Where(m => m.Id != id && (m.Genres.Any(g => genres.Contains(g.Name)) || m.Credits.Any(c => c.Kind == CreditKind.Director && directors.Contains(c.Name))))
            .OrderByDescending(m => m.Credits.Count(c => c.Kind == CreditKind.Director && directors.Contains(c.Name)) * 3 + m.Genres.Count(g => genres.Contains(g.Name)))
            .ThenByDescending(m => m.Rating)
            .Take(20);
        return await library.SummariesAsync(q, User.UserId(), ct);
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
