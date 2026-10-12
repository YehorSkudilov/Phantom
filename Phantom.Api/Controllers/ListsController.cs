using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/watchlist")]
public class WatchlistController(PhantomDbContext db, LibraryService library, ShowLibraryService shows) : ControllerBase
{
    /// <summary>Movies and shows on the watchlist, most recently added first (shows are added at api/shows/{id}/watchlist).</summary>
    [HttpGet]
    public async Task<List<MovieSummaryDto>> Get(CancellationToken ct)
    {
        var userId = User.UserId();
        var movieAdded = await db.Watchlist.Where(w => w.UserId == userId).ToDictionaryAsync(w => w.MovieId, w => w.AddedAt, ct);
        var showAdded = await db.ShowWatchlist.Where(w => w.UserId == userId).ToDictionaryAsync(w => w.ShowId, w => w.AddedAt, ct);
        var movieIds = movieAdded.Keys.ToList();
        var showIds = showAdded.Keys.ToList();
        var movies = await library.SummariesAsync(library.Visible(User).Where(m => movieIds.Contains(m.Id)), userId, ct);
        var showItems = await shows.SummariesAsync(shows.Visible(User).Where(s => showIds.Contains(s.Id)), userId, ct);
        return movies.Select(m => (Item: m, Added: movieAdded[m.Id]))
            .Concat(showItems.Select(s => (Item: s, Added: showAdded[s.Id])))
            .OrderByDescending(x => x.Added).Select(x => x.Item).ToList();
    }

    [HttpPut("{movieId:int}")]
    public async Task<IActionResult> Add(int movieId, CancellationToken ct)
    {
        if (!await db.Movies.AnyAsync(m => m.Id == movieId, ct)) return NotFound();
        var userId = User.UserId();
        if (await db.Watchlist.FindAsync([userId, movieId], ct) is null)
        {
            db.Watchlist.Add(new WatchlistItem { UserId = userId, MovieId = movieId });
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpDelete("{movieId:int}")]
    public async Task<IActionResult> Remove(int movieId, CancellationToken ct)
    {
        if (await db.Watchlist.FindAsync([User.UserId(), movieId], ct) is { } item)
        {
            db.Watchlist.Remove(item);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}

[ApiController]
[Authorize]
[Route("api/collections")]
public class CollectionsController(PhantomDbContext db, LibraryService library) : ControllerBase
{
    [HttpGet]
    public async Task<List<CollectionDto>> List(CancellationToken ct)
    {
        var visible = library.Visible(User);
        return await db.Collections.AsNoTracking()
            .Select(c => new CollectionDto(c.Id, c.Name, c.Description,
                c.PosterUrl ?? c.Movies.Select(m => m.PosterUrl).FirstOrDefault(),
                c.Movies.Count(m => visible.Contains(m))))
            .Where(c => c.Count > 0)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CollectionDetailDto>> Get(int id, CancellationToken ct)
    {
        var c = await db.Collections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var q = library.Visible(User).Where(m => db.Collections.Any(x => x.Id == id && x.Movies.Contains(m))).OrderBy(m => m.Year);
        return new CollectionDetailDto(c.Id, c.Name, c.Description, c.PosterUrl, await library.SummariesAsync(q, User.UserId(), ct));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<ActionResult<CollectionDto>> Create(CreateCollectionRequest req, CancellationToken ct)
    {
        var c = new Collection { Name = req.Name, Description = req.Description };
        db.Collections.Add(c);
        await db.SaveChangesAsync(ct);
        return new CollectionDto(c.Id, c.Name, c.Description, null, 0);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{id:int}/items")]
    public async Task<IActionResult> AddItem(int id, AddItemRequest req, CancellationToken ct)
    {
        var c = await db.Collections.Include(x => x.Movies).FirstOrDefaultAsync(x => x.Id == id, ct);
        var movie = await db.Movies.FindAsync([req.MovieId], ct);
        if (c is null || movie is null) return NotFound();
        if (!c.Movies.Contains(movie)) c.Movies.Add(movie);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:int}/items/{movieId:int}")]
    public async Task<IActionResult> RemoveItem(int id, int movieId, CancellationToken ct)
    {
        var c = await db.Collections.Include(x => x.Movies).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        c.Movies.RemoveAll(m => m.Id == movieId);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var c = await db.Collections.FindAsync([id], ct);
        if (c is null) return NotFound();
        db.Collections.Remove(c);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[ApiController]
[Authorize]
[Route("api/playlists")]
public class PlaylistsController(PhantomDbContext db, LibraryService library, ShowLibraryService shows) : ControllerBase
{
    [HttpGet]
    public async Task<List<PlaylistDto>> List(CancellationToken ct)
    {
        var userId = User.UserId();
        return await db.Playlists.AsNoTracking().Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .Select(p => new PlaylistDto(p.Id, p.Name, p.Items.Count,
                p.Items.Sum(i => (long)(i.Movie!.RuntimeMinutes ?? 0) + (i.Episode!.RuntimeMinutes ?? 0))))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The profile's playlists holding any of <paramref name="keys"/> ("m12,s4,e99"), and which: what a title's (or a
    /// season's) playlist button shows.
    /// </summary>
    [HttpGet("containing")]
    public async Task<List<PlaylistMembershipDto>> Containing([FromQuery] string keys, CancellationToken ct)
    {
        var (movieIds, showIds, episodeIds) = Parse(keys.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var userId = User.UserId();
        var playlists = await db.Playlists.AsNoTracking().Where(p => p.UserId == userId).Include(p => p.Items).OrderBy(p => p.Name).ToListAsync(ct);
        return playlists
            .Select(p => new PlaylistMembershipDto(p.Id, p.Name, p.Items
                .Where(i => i.MovieId is { } m && movieIds.Contains(m) || i.ShowId is { } sh && showIds.Contains(sh)
                            || i.EpisodeId is { } e && episodeIds.Contains(e))
                .Select(i => i.Key).ToList()))
            .Where(p => p.Keys.Count > 0).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PlaylistDetailDto>> Get(int id, CancellationToken ct)
    {
        var userId = User.UserId();
        var p = await db.Playlists.AsNoTracking().Include(x => x.Items).ThenInclude(i => i.Episode)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (p is null) return NotFound();
        // Movies, shows and episodes together, in the playlist's order. An episode is its show's card, the episode named.
        var items = p.Items.OrderBy(i => i.Order).ToList();
        var movieIds = items.Where(i => i.MovieId != null).Select(i => i.MovieId!.Value).ToList();
        var showIds = items.Select(i => i.ShowId ?? i.Episode?.ShowId).OfType<int>().Distinct().ToList();
        var movies = (await library.SummariesAsync(library.Visible(User).Where(m => movieIds.Contains(m.Id)), userId, ct)).ToDictionary(m => m.Id);
        var showCards = (await shows.SummariesAsync(shows.Visible(User).Where(s => showIds.Contains(s.Id)), userId, ct)).ToDictionary(s => s.Id);
        var entries = items.Select(i => (i.Key, Card:
                i.MovieId is { } m ? movies.GetValueOrDefault(m)
                : i.ShowId is { } sh ? showCards.GetValueOrDefault(sh)
                : i.Episode is { } e && showCards.GetValueOrDefault(e.ShowId) is { } card
                    ? card with
                    {
                        Caption = $"S{e.SeasonNumber}:E{e.EpisodeNumber}" + (string.IsNullOrWhiteSpace(e.Title) ? "" : $" · {e.Title}"),
                        RuntimeMinutes = e.RuntimeMinutes ?? card.RuntimeMinutes,
                    }
                : null))
            .Where(x => x.Card is not null).ToList();
        return new PlaylistDetailDto(p.Id, p.Name, entries.Select(x => x.Card!).ToList(), entries.Select(x => x.Key).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<PlaylistDto>> Create(CreatePlaylistRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");
        var p = new Playlist { Name = req.Name.Trim(), UserId = User.UserId() };
        db.Playlists.Add(p);
        await db.SaveChangesAsync(ct);
        return new PlaylistDto(p.Id, p.Name, 0, 0);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Rename(int id, CreatePlaylistRequest req, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null) return NotFound();
        p.Name = req.Name.Trim();
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Adds a movie, a whole show, or episodes (one, or a season's) to the end; ones already in it stay where they are.</summary>
    [HttpPost("{id:int}/items")]
    public async Task<IActionResult> AddItem(int id, AddItemRequest req, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null) return NotFound();
        var order = p.Items.Count == 0 ? 0 : p.Items.Max(i => i.Order) + 1;
        if (req.EpisodeIds is { Count: > 0 } episodeIds)
        {
            var known = await db.Episodes.Where(e => episodeIds.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
            if (known.Count == 0) return NotFound();
            foreach (var e in episodeIds.Where(known.Contains).Distinct())
                if (p.Items.All(i => i.EpisodeId != e)) p.Items.Add(new PlaylistItem { EpisodeId = e, Order = order++ });
        }
        else if (req.ShowId is { } showId)
        {
            if (!await db.Shows.AnyAsync(s => s.Id == showId, ct)) return NotFound();
            if (p.Items.All(i => i.ShowId != showId)) p.Items.Add(new PlaylistItem { ShowId = showId, Order = order });
        }
        else
        {
            if (!await db.Movies.AnyAsync(m => m.Id == req.MovieId, ct)) return NotFound();
            if (p.Items.All(i => i.MovieId != req.MovieId)) p.Items.Add(new PlaylistItem { MovieId = req.MovieId, Order = order });
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}/items/{movieId:int}")]
    public Task<IActionResult> RemoveItem(int id, int movieId, CancellationToken ct) => RemoveEntries(id, $"m{movieId}", ct);

    /// <summary>Takes entries out, by key: "m12", "s4,e98,e99"...</summary>
    [HttpDelete("{id:int}/entries/{keys}")]
    public async Task<IActionResult> RemoveEntries(int id, string keys, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null) return NotFound();
        var remove = keys.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        db.PlaylistItems.RemoveRange(p.Items.Where(i => remove.Contains(i.Key)));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("{id:int}/order")]
    public async Task<IActionResult> Reorder(int id, ReorderRequest req, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null) return NotFound();
        var keys = req.Keys ?? req.MovieIds.Select(m => $"m{m}").ToList();
        for (var i = 0; i < keys.Count; i++)
            if (p.Items.FirstOrDefault(x => x.Key == keys[i]) is { } item) item.Order = i;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null) return NotFound();
        db.Playlists.Remove(p);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Task<Playlist?> FindAsync(int id, CancellationToken ct)
    {
        var userId = User.UserId();
        return db.Playlists.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
    }

    /// <summary>Entry keys ("m12", "s4", "e99") split by kind.</summary>
    private static (HashSet<int> Movies, HashSet<int> Shows, HashSet<int> Episodes) Parse(IEnumerable<string> keys)
    {
        var result = (Movies: new HashSet<int>(), Shows: new HashSet<int>(), Episodes: new HashSet<int>());
        foreach (var key in keys)
        {
            if (key.Length < 2 || !int.TryParse(key[1..], out var id)) continue;
            (key[0] switch { 's' => result.Shows, 'e' => result.Episodes, _ => result.Movies }).Add(id);
        }
        return result;
    }
}
