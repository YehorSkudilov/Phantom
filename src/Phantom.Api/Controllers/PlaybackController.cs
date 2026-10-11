using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/playback")]
public class PlaybackController(PhantomDbContext db, SessionTracker sessions) : ControllerBase
{
    /// <summary>Movies count as watched once 90% has been played, like Plex.</summary>
    public const double WatchedThreshold = 0.9;

    /// <summary>Clients call this every ~10s while playing and on pause/stop (scrobbling + resume points).</summary>
    [HttpPost("timeline")]
    public async Task<IActionResult> Timeline(TimelineUpdate update, CancellationToken ct)
    {
        var movie = await db.Movies.AsNoTracking().Where(m => m.Id == update.MovieId)
            .Select(m => new { m.Title, m.PosterUrl }).FirstOrDefaultAsync(ct);
        if (movie is null) return NotFound();

        var userId = User.UserId();
        var state = await db.WatchStates.FindAsync([userId, update.MovieId], ct);
        if (state is null)
        {
            state = new WatchState { UserId = userId, MovieId = update.MovieId };
            db.WatchStates.Add(state);
        }

        var finished = update.DurationMs > 0 && update.PositionMs >= update.DurationMs * WatchedThreshold;
        if (finished && !state.Watched)
        {
            state.Watched = true;
            state.ViewCount++;
            state.PositionMs = 0;
        }
        else if (!finished)
        {
            if (update.PositionMs > 0) state.Watched = false;
            state.PositionMs = update.PositionMs;
        }
        if (update.DurationMs > 0) state.DurationMs = update.DurationMs;
        state.LastViewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        sessions.Update(new NowPlayingDto(
            SessionId: $"{userId}:{update.ClientName}",
            Username: User.FindFirstValue(ClaimTypes.Name) ?? "?",
            MovieId: update.MovieId,
            MovieTitle: movie.Title,
            PosterUrl: movie.PosterUrl,
            PositionMs: update.PositionMs,
            DurationMs: update.DurationMs,
            State: update.State,
            ClientName: update.ClientName,
            Transcoding: false,
            UpdatedAt: DateTime.UtcNow));
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("sessions")]
    public IReadOnlyList<NowPlayingDto> Sessions() => sessions.Active();

    /// <summary>Per-user watch history, newest first.</summary>
    [HttpGet("history")]
    public async Task<List<MovieSummaryDto>> History([FromServices] LibraryService library, CancellationToken ct)
    {
        var userId = User.UserId();
        var q = library.Visible(User)
            .Where(m => db.WatchStates.Any(w => w.UserId == userId && w.MovieId == m.Id && w.LastViewedAt != null))
            .OrderByDescending(m => db.WatchStates.Where(w => w.UserId == userId && w.MovieId == m.Id).Select(w => w.LastViewedAt).FirstOrDefault())
            .Take(100);
        return await library.SummariesAsync(q, userId, ct);
    }
}
