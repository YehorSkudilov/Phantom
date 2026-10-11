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
        if (update.EpisodeId is { } episodeId) return await EpisodeTimelineAsync(update, episodeId, ct);

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

    /// <summary><see cref="Timeline"/> for an episode: its own progress, watched at the same 90%.</summary>
    private async Task<IActionResult> EpisodeTimelineAsync(TimelineUpdate update, int episodeId, CancellationToken ct)
    {
        var episode = await db.Episodes.AsNoTracking().Where(e => e.Id == episodeId)
            .Select(e => new { e.SeasonNumber, e.EpisodeNumber, Show = e.Show!.Title, e.Show.PosterUrl }).FirstOrDefaultAsync(ct);
        if (episode is null) return NotFound();

        var userId = User.UserId();
        var state = await db.EpisodeWatchStates.FindAsync([userId, episodeId], ct);
        if (state is null)
        {
            state = new EpisodeWatchState { UserId = userId, EpisodeId = episodeId };
            db.EpisodeWatchStates.Add(state);
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
            MovieId: 0,
            MovieTitle: $"{episode.Show} · S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
            PosterUrl: episode.PosterUrl,
            PositionMs: update.PositionMs,
            DurationMs: update.DurationMs,
            State: update.State,
            ClientName: update.ClientName,
            Transcoding: false,
            UpdatedAt: DateTime.UtcNow,
            EpisodeId: episodeId));
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("sessions")]
    public IReadOnlyList<NowPlayingDto> Sessions() => sessions.Active();

    /// <summary>Per-user watch history, newest first: movies, and shows by their latest episode.</summary>
    [HttpGet("history")]
    public async Task<List<MovieSummaryDto>> History([FromServices] LibraryService library, [FromServices] ShowLibraryService shows, CancellationToken ct)
    {
        var userId = User.UserId();
        var movies = await library.Visible(User)
            .Where(m => db.WatchStates.Any(w => w.UserId == userId && w.MovieId == m.Id && w.LastViewedAt != null))
            .Select(m => new { m.Id, Last = db.WatchStates.Where(w => w.UserId == userId && w.MovieId == m.Id).Select(w => w.LastViewedAt).FirstOrDefault() })
            .OrderByDescending(x => x.Last).Take(100).ToListAsync(ct);
        var showRows = await shows.Visible(User)
            .Join(shows.LastViewed(userId), s => s.Id, l => l.ShowId, (s, l) => new { s.Id, Last = l.LastViewedAt })
            .OrderByDescending(x => x.Last).Take(100).ToListAsync(ct);

        var movieIds = movies.Select(m => m.Id).ToList();
        var showIds = showRows.Select(s => s.Id).ToList();
        var items = (await library.SummariesAsync(library.Visible(User).Where(m => movieIds.Contains(m.Id)), userId, ct))
            .Select(i => (Item: i, Last: movies.First(m => m.Id == i.Id).Last))
            .Concat((await shows.SummariesAsync(shows.Visible(User).Where(s => showIds.Contains(s.Id)), userId, ct))
                .Select(i => (Item: i, Last: showRows.First(s => s.Id == i.Id).Last)));
        return items.OrderByDescending(x => x.Last).Take(100).Select(x => x.Item).ToList();
    }
}
