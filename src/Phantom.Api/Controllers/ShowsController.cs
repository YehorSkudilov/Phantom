using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/shows")]
public class ShowsController(PhantomDbContext db, ShowLibraryService shows) : ControllerBase
{
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ShowDetailDto>> Get(int id, CancellationToken ct) =>
        await shows.DetailAsync(User, id, ct) is { } dto ? dto : NotFound();

    [HttpGet("{id:int}/seasons/{season:int}")]
    public async Task<ActionResult<List<EpisodeDto>>> Season(int id, int season, CancellationToken ct) =>
        await shows.SeasonAsync(User, id, season, ct) is { } episodes ? episodes : NotFound();

    /// <summary>Marks every episode watched (or none), like Plex's "Mark as played" on a show.</summary>
    [HttpPost("{id:int}/watched")]
    public Task<IActionResult> MarkWatched(int id, CancellationToken ct) => SetWatchedAsync(db.Episodes.Where(e => e.ShowId == id), true, ct);

    [HttpPost("{id:int}/unwatched")]
    public Task<IActionResult> MarkUnwatched(int id, CancellationToken ct) => SetWatchedAsync(db.Episodes.Where(e => e.ShowId == id), false, ct);

    [HttpPut("{id:int}/watchlist")]
    public async Task<IActionResult> AddToWatchlist(int id, CancellationToken ct)
    {
        if (!await db.Shows.AnyAsync(s => s.Id == id, ct)) return NotFound();
        var userId = User.UserId();
        if (await db.ShowWatchlist.FindAsync([userId, id], ct) is null)
        {
            db.ShowWatchlist.Add(new ShowWatchlistItem { UserId = userId, ShowId = id });
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpDelete("{id:int}/watchlist")]
    public async Task<IActionResult> RemoveFromWatchlist(int id, CancellationToken ct)
    {
        if (await db.ShowWatchlist.FindAsync([User.UserId(), id], ct) is { } item)
        {
            db.ShowWatchlist.Remove(item);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    private async Task<IActionResult> SetWatchedAsync(IQueryable<Episode> episodes, bool watched, CancellationToken ct)
    {
        var ids = await episodes.Select(e => e.Id).ToListAsync(ct);
        if (ids.Count == 0) return NotFound();
        await EpisodesController.SetWatchedAsync(db, User.UserId(), ids, watched, ct);
        return NoContent();
    }
}

[ApiController]
[Authorize]
[Route("api/episodes")]
public class EpisodesController(PhantomDbContext db, ShowLibraryService shows) : ControllerBase
{
    [HttpGet("{id:int}")]
    public async Task<ActionResult<EpisodeDetailDto>> Get(int id, CancellationToken ct) =>
        await shows.EpisodeAsync(User, id, ct) is { } dto ? dto : NotFound();

    [HttpPost("{id:int}/watched")]
    public async Task<IActionResult> MarkWatched(int id, CancellationToken ct)
    {
        if (!await db.Episodes.AnyAsync(e => e.Id == id, ct)) return NotFound();
        await SetWatchedAsync(db, User.UserId(), [id], true, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/unwatched")]
    public async Task<IActionResult> MarkUnwatched(int id, CancellationToken ct)
    {
        if (!await db.Episodes.AnyAsync(e => e.Id == id, ct)) return NotFound();
        await SetWatchedAsync(db, User.UserId(), [id], false, ct);
        return NoContent();
    }

    internal static async Task SetWatchedAsync(PhantomDbContext db, int userId, IReadOnlyList<int> episodeIds, bool watched, CancellationToken ct)
    {
        var states = await db.EpisodeWatchStates.Where(w => w.UserId == userId && episodeIds.Contains(w.EpisodeId)).ToDictionaryAsync(w => w.EpisodeId, ct);
        foreach (var id in episodeIds)
        {
            if (!states.TryGetValue(id, out var state))
            {
                if (!watched) continue;
                db.EpisodeWatchStates.Add(state = new EpisodeWatchState { UserId = userId, EpisodeId = id });
            }
            if (watched && !state.Watched) state.ViewCount++;
            state.Watched = watched;
            state.PositionMs = 0;
            if (watched) state.LastViewedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
