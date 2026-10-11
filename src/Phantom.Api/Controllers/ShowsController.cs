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
public class ShowsController(PhantomDbContext db, ShowLibraryService shows, LibraryService library, DiscoverService discover) : ControllerBase
{
    /// <summary>"More Like This": shows (and movies) sharing the most genres, cast and creators with this one.</summary>
    [HttpGet("{id:int}/related")]
    public async Task<ActionResult<List<MovieSummaryDto>>> Related(int id, CancellationToken ct)
    {
        if (!await shows.Visible(User).AnyAsync(s => s.Id == id, ct)) return NotFound();
        var refs = await discover.MoreLikeAsync(User, new TitleRef(true, id), ct);
        var userId = User.UserId();
        var movieIds = refs.Where(r => !r.IsShow).Select(r => r.Id).ToList();
        var showIds = refs.Where(r => r.IsShow).Select(r => r.Id).ToList();
        var found = (await library.SummariesAsync(library.Visible(User).Where(m => movieIds.Contains(m.Id)), userId, ct))
            .Select(m => (Ref: new TitleRef(false, m.Id), Item: m))
            .Concat((await shows.SummariesAsync(shows.Visible(User).Where(s => showIds.Contains(s.Id)), userId, ct))
                .Select(s => (Ref: new TitleRef(true, s.Id), Item: s)))
            .ToDictionary(x => x.Ref, x => x.Item);
        return refs.Where(found.ContainsKey).Select(r => found[r]).ToList();
    }

    /// <summary>
    /// The sources (releases) a season can be played from, best first: those of its episodes, the season's packs and
    /// whole-show packs. Playing an episode from one that hasn't got it falls back to the best that has.
    /// </summary>
    [HttpGet("{id:int}/seasons/{season:int}/releases")]
    public async Task<ActionResult<List<ReleaseDto>>> SeasonReleases(int id, int season, CancellationToken ct)
    {
        if (!await shows.Visible(User).AnyAsync(s => s.Id == id, ct)) return NotFound();
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        var releases = await db.Releases.AsNoTracking().Where(r => r.ShowId == id && (r.SeasonNumber == null || r.SeasonNumber == season)).ToListAsync(ct);
        // On the server's disk first, then sharing, then whole packs (one choice covers the season), quality, seeders.
        return releases
            .OrderByDescending(r => r.LocalFilePath != null && System.IO.File.Exists(r.LocalFilePath))
            .ThenByDescending(r => r.Seeders > 0)
            .ThenByDescending(r => r.EpisodeNumber == null)
            .ThenByDescending(r => settings.PreferredQuality != null && r.Quality == settings.PreferredQuality)
            .ThenByDescending(r => ReleaseNameParser.QualityRank(r.Quality))
            .ThenByDescending(r => r.Seeders)
            .ThenBy(r => r.EpisodeNumber)
            .Select(ShowLibraryService.ToReleaseDto).ToList();
    }

    /// <summary>Rates the show for this profile, out of 10 (null clears it). Steers recommendations.</summary>
    [HttpPut("{id:int}/rating")]
    public async Task<IActionResult> Rate(int id, SetRatingRequest req, CancellationToken ct)
    {
        if (req.Rating is < 0 or > 10) return BadRequest("Rating must be between 0 and 10.");
        if (!await db.Shows.AnyAsync(s => s.Id == id, ct)) return NotFound();
        var userId = User.UserId();
        var rating = await db.ShowRatings.FindAsync([userId, id], ct);
        if (req.Rating is not { } value)
        {
            if (rating is not null) db.ShowRatings.Remove(rating);
        }
        else if (rating is null) db.ShowRatings.Add(new ShowRating { UserId = userId, ShowId = id, Rating = value });
        else
        {
            rating.Rating = value;
            rating.RatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

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

    /// <summary>Marks every episode of one season watched (or none).</summary>
    [HttpPost("{id:int}/seasons/{season:int}/watched")]
    public Task<IActionResult> MarkSeasonWatched(int id, int season, CancellationToken ct) =>
        SetWatchedAsync(db.Episodes.Where(e => e.ShowId == id && e.SeasonNumber == season), true, ct);

    [HttpPost("{id:int}/seasons/{season:int}/unwatched")]
    public Task<IActionResult> MarkSeasonUnwatched(int id, int season, CancellationToken ct) =>
        SetWatchedAsync(db.Episodes.Where(e => e.ShowId == id && e.SeasonNumber == season), false, ct);

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
