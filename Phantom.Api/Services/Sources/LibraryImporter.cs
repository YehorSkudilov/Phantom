using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Sources;

public record ImportResult(int Releases, int NewMovies, int NewReleases, int NewShows = 0);

/// <summary>
/// Matches source releases to library movies and shows, creating them and their releases as needed. New titles come in
/// bare and MetadataRefreshService fills in their artwork and details from TMDB in the background, so an import is
/// database work only, and quick.
/// </summary>
public class LibraryImporter(PhantomDbContext db, ILogger<LibraryImporter> log)
{
    /// <summary>Saved together, rather than one database write per release.</summary>
    private const int SaveEvery = 100;

    // Sources sync in parallel, but their imports take turns: SQLite has one writer at a time, and two imports adding
    // the same new movie at once would clash on its key.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<ImportResult> ImportAsync(Source source, IReadOnlyList<SourceRelease> releases, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            return await ImportBatchAsync(source, releases, ct);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<ImportResult> ImportBatchAsync(Source source, IReadOnlyList<SourceRelease> releases, CancellationToken ct)
    {
        int newMovies = 0, newReleases = 0, newShows = 0, unsaved = 0;
        var settings = await db.Settings.FirstAsync(ct);
        var wanted = releases.Where(r => r.Seeders >= settings.MinSeeders || r.TorrentUrl is not null).DistinctBy(r => r.InfoHash).ToList();
        // Ones found to be fake stay out.
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in wanted.Select(r => r.InfoHash.ToLowerInvariant()).Chunk(500))
            blocked.UnionWith(await db.BlockedReleases.Where(b => chunk.Contains(b.InfoHash)).Select(b => b.InfoHash).ToListAsync(ct));
        wanted.RemoveAll(r => blocked.Contains(r.InfoHash));

        // The releases already known, looked up together rather than one query each.
        var known = new Dictionary<string, Release>();
        foreach (var chunk in wanted.Select(r => r.InfoHash).Chunk(500))
            foreach (var r in await db.Releases.Where(r => chunk.Contains(r.InfoHash)).ToListAsync(ct))
                known[r.InfoHash] = r;

        foreach (var sr in wanted)
        {
            if (++unsaved >= SaveEvery)
            {
                await SaveAsync(ct);
                unsaved = 0;
            }

            if (known.TryGetValue(sr.InfoHash, out var existing))
            {
                existing.Seeders = sr.Seeders;
                existing.Leechers = sr.Leechers;
                existing.TorrentUrl ??= sr.TorrentUrl;
                existing.MagnetUri ??= sr.MagnetUri;
                continue;
            }

            if (sr.IsShow)
            {
                if (await ImportShowReleaseAsync(source, sr, ct)) newShows++;
                newReleases++;
                continue;
            }

            var (movie, created) = await FindOrCreateMovieAsync(sr, ct);
            if (created) newMovies++;

            movie.Releases.Add(new Release
            {
                Name = sr.ReleaseName,
                SourceId = source.Id,
                SourceName = source.Name,
                InfoHash = sr.InfoHash,
                MagnetUri = sr.MagnetUri,
                TorrentUrl = sr.TorrentUrl,
                Quality = sr.Quality,
                Codec = sr.Codec,
                SizeBytes = sr.SizeBytes,
                Seeders = sr.Seeders,
                Leechers = sr.Leechers,
            });
            newReleases++;

            if (settings.AutoDownloadNewReleases && created)
            {
                await SaveAsync(ct);
                db.ServerDownloads.Add(new ServerDownload { MovieId = movie.Id, ReleaseId = movie.Releases[^1].Id });
            }
        }
        await SaveAsync(ct);

        log.LogInformation("Imported {Count} releases from {Source}: {Movies} new movies, {Shows} new shows, {New} new releases",
            releases.Count, source.Name, newMovies, newShows, newReleases);
        return new ImportResult(releases.Count, newMovies, newReleases, newShows);
    }

    /// <summary>
    /// Saves what's been added. A batch that can't be saved is dropped rather than stopping the import: its releases
    /// come round again on the next sync.
    /// </summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            log.LogWarning(ex, "Couldn't save part of an import; it's tried again next sync");
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Adds a show's release, and the show if it's new (returning true then).</summary>
    private async Task<bool> ImportShowReleaseAsync(Source source, SourceRelease sr, CancellationToken ct)
    {
        var (show, created) = await FindOrCreateShowAsync(sr, ct);

        // An episode's release makes sure the episode is listed, even before (or without) TMDB.
        if (sr is { Season: { } season, Episode: { } number }
            && !show.Episodes.Any(e => e.SeasonNumber == season && e.EpisodeNumber == number))
            show.Episodes.Add(new Episode { SeasonNumber = season, EpisodeNumber = number });

        show.Releases.Add(new Release
        {
            Name = sr.ReleaseName,
            SourceId = source.Id,
            SourceName = source.Name,
            InfoHash = sr.InfoHash,
            MagnetUri = sr.MagnetUri,
            TorrentUrl = sr.TorrentUrl,
            Quality = sr.Quality,
            Codec = sr.Codec,
            SizeBytes = sr.SizeBytes,
            Seeders = sr.Seeders,
            Leechers = sr.Leechers,
            SeasonNumber = sr.Season,
            EpisodeNumber = sr.Season is null ? null : sr.Episode,
        });
        return created;
    }

    private async Task<(Show, bool)> FindOrCreateShowAsync(SourceRelease sr, CancellationToken ct)
    {
        IQueryable<Show> shows = db.Shows.Include(s => s.Episodes).Include(s => s.Genres).Include(s => s.Credits).AsSplitQuery();
        var show = sr.ImdbId is null ? null : db.Shows.Local.FirstOrDefault(s => s.ImdbId == sr.ImdbId);
        if (show is null && sr.ImdbId is not null) show = await shows.FirstOrDefaultAsync(s => s.ImdbId == sr.ImdbId, ct);
        if (show is null && sr.TmdbId is not null) show = await shows.FirstOrDefaultAsync(s => s.TmdbId == sr.TmdbId, ct);

        // Episode names seldom carry the year, so the name alone is the key (with the year when there is one, for
        // remakes like "Doctor Who 2005").
        var key = ReleaseNameParser.NormalizeKey(sr.Title, sr.Year);
        show ??= db.Shows.Local.FirstOrDefault(s => s.ExternalKey == key) ?? await shows.FirstOrDefaultAsync(s => s.ExternalKey == key, ct);
        if (show is null && sr.Year is not null)
        {
            var bare = ReleaseNameParser.NormalizeKey(sr.Title, null);
            show = await shows.FirstOrDefaultAsync(s => s.ExternalKey == bare, ct);
        }
        if (show is not null)
        {
            show.ImdbId ??= sr.ImdbId;
            return (show, false);
        }

        show = new Show
        {
            Title = sr.Title,
            SortTitle = ReleaseNameParser.SortTitle(sr.Title),
            Year = sr.Year,
            ExternalKey = key,
            ImdbId = sr.ImdbId,
            TmdbId = sr.TmdbId,
            Overview = sr.Overview,
            PosterUrl = sr.PosterUrl,
        };
        db.Shows.Add(show);
        return (show, true);
    }

    private async Task<(Movie, bool)> FindOrCreateMovieAsync(SourceRelease sr, CancellationToken ct)
    {
        // Movies added earlier in this batch aren't saved yet, so they're looked for among those first.
        var key = ReleaseNameParser.NormalizeKey(sr.Title, sr.Year);
        var movie = db.Movies.Local.FirstOrDefault(m => sr.ImdbId is not null && m.ImdbId == sr.ImdbId
                                                       || sr.TmdbId is not null && m.TmdbId == sr.TmdbId
                                                       || m.ExternalKey == key);
        if (movie is null && sr.ImdbId is not null)
            movie = await db.Movies.Include(m => m.Releases).Include(m => m.Genres).FirstOrDefaultAsync(m => m.ImdbId == sr.ImdbId, ct);
        if (movie is null && sr.TmdbId is not null)
            movie = await db.Movies.Include(m => m.Releases).Include(m => m.Genres).FirstOrDefaultAsync(m => m.TmdbId == sr.TmdbId, ct);
        movie ??= await db.Movies.Include(m => m.Releases).Include(m => m.Genres).FirstOrDefaultAsync(m => m.ExternalKey == key, ct);
        if (movie is not null) return (movie, false);

        movie = new Movie
        {
            Title = sr.Title,
            SortTitle = ReleaseNameParser.SortTitle(sr.Title),
            Year = sr.Year,
            ExternalKey = key,
            ImdbId = sr.ImdbId,
            TmdbId = sr.TmdbId,
            Overview = sr.Overview,
            PosterUrl = sr.PosterUrl,
            RuntimeMinutes = sr.RuntimeMinutes,
        };
        foreach (var name in sr.Genres)
        {
            var genre = db.Genres.Local.FirstOrDefault(g => g.Name == name)
                        ?? await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct)
                        ?? new Genre { Name = name };
            movie.Genres.Add(genre);
        }
        if (!string.IsNullOrWhiteSpace(sr.Director))
            movie.Credits.Add(new Credit { Name = sr.Director, Kind = CreditKind.Director });

        db.Movies.Add(movie);
        return (movie, true);
    }
}
