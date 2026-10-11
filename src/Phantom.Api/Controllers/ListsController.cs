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
public class WatchlistController(PhantomDbContext db, LibraryService library) : ControllerBase
{
    [HttpGet]
    public async Task<List<MovieSummaryDto>> Get(CancellationToken ct)
    {
        var userId = User.UserId();
        var q = library.Visible(User)
            .Where(m => db.Watchlist.Any(w => w.UserId == userId && w.MovieId == m.Id))
            .OrderByDescending(m => db.Watchlist.Where(w => w.UserId == userId && w.MovieId == m.Id).Select(w => w.AddedAt).FirstOrDefault());
        return await library.SummariesAsync(q, userId, ct);
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
public class PlaylistsController(PhantomDbContext db, LibraryService library) : ControllerBase
{
    [HttpGet]
    public async Task<List<PlaylistDto>> List(CancellationToken ct)
    {
        var userId = User.UserId();
        return await db.Playlists.AsNoTracking().Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .Select(p => new PlaylistDto(p.Id, p.Name, p.Items.Count, p.Items.Sum(i => (long)(i.Movie!.RuntimeMinutes ?? 0))))
            .ToListAsync(ct);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PlaylistDetailDto>> Get(int id, CancellationToken ct)
    {
        var userId = User.UserId();
        var p = await db.Playlists.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (p is null) return NotFound();
        var ids = p.Items.OrderBy(i => i.Order).Select(i => i.MovieId).ToList();
        var movies = await library.SummariesAsync(library.Visible(User).Where(m => ids.Contains(m.Id)), userId, ct);
        var ordered = ids.Select(i => movies.FirstOrDefault(m => m.Id == i)).OfType<MovieSummaryDto>().ToList();
        return new PlaylistDetailDto(p.Id, p.Name, ordered);
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

    [HttpPost("{id:int}/items")]
    public async Task<IActionResult> AddItem(int id, AddItemRequest req, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null || !await db.Movies.AnyAsync(m => m.Id == req.MovieId, ct)) return NotFound();
        if (p.Items.All(i => i.MovieId != req.MovieId))
            p.Items.Add(new PlaylistItem { MovieId = req.MovieId, Order = p.Items.Count == 0 ? 0 : p.Items.Max(i => i.Order) + 1 });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}/items/{movieId:int}")]
    public async Task<IActionResult> RemoveItem(int id, int movieId, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null) return NotFound();
        db.PlaylistItems.RemoveRange(p.Items.Where(i => i.MovieId == movieId));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("{id:int}/order")]
    public async Task<IActionResult> Reorder(int id, ReorderRequest req, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        if (p is null) return NotFound();
        for (var i = 0; i < req.MovieIds.Count; i++)
            if (p.Items.FirstOrDefault(x => x.MovieId == req.MovieIds[i]) is { } item) item.Order = i;
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
}
