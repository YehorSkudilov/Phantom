using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services.Metadata;

namespace Phantom.Api.Services.Sources;

public record ImportResult(int Releases, int NewMovies, int NewReleases, int NewShows = 0);

/// <summary>Matches source releases to library movies and shows, creating them and their releases as needed.</summary>
public class LibraryImporter(PhantomDbContext db, IMetadataProvider metadata, ILogger<LibraryImporter> log)
{
    public async Task<ImportResult> ImportAsync(Source source, IReadOnlyList<SourceRelease> releases, CancellationToken ct)
    {
        int newMovies = 0, newReleases = 0, newShows = 0;
        var settings = await db.Settings.FirstAsync(ct);

        foreach (var sr in releases.Where(r => r.Seeders >= settings.MinSeeders || r.TorrentUrl is not null))
        {
            var existing = await db.Releases.FirstOrDefaultAsync(r => r.InfoHash == sr.InfoHash, ct);
            if (existing is not null)
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
                await db.SaveChangesAsync(ct);
                continue;
            }

            var (movie, created) = await FindOrCreateMovieAsync(sr, ct);
            if (created)
            {
                newMovies++;
                await metadata.EnrichAsync(db, movie, ct);
            }

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
                await db.SaveChangesAsync(ct);
                db.ServerDownloads.Add(new ServerDownload { MovieId = movie.Id, ReleaseId = movie.Releases[^1].Id });
            }

            // Save per movie so a later failure does not lose the whole batch.
            await db.SaveChangesAsync(ct);
        }

        log.LogInformation("Imported {Count} releases from {Source}: {Movies} new movies, {Shows} new shows, {New} new releases",
            releases.Count, source.Name, newMovies, newShows, newReleases);
        return new ImportResult(releases.Count, newMovies, newReleases, newShows);
    }

    /// <summary>Adds a show's release, and the show if it's new (returning true then).</summary>
    private async Task<bool> ImportShowReleaseAsync(Source source, SourceRelease sr, CancellationToken ct)
    {
        var (show, created) = await FindOrCreateShowAsync(sr, ct);
        if (created) await metadata.EnrichAsync(db, show, ct);

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
        Show? show = null;
        if (sr.ImdbId is not null) show = await shows.FirstOrDefaultAsync(s => s.ImdbId == sr.ImdbId, ct);
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
        Movie? movie = null;
        if (sr.ImdbId is not null)
            movie = await db.Movies.Include(m => m.Releases).Include(m => m.Genres).FirstOrDefaultAsync(m => m.ImdbId == sr.ImdbId, ct);
        if (movie is null && sr.TmdbId is not null)
            movie = await db.Movies.Include(m => m.Releases).Include(m => m.Genres).FirstOrDefaultAsync(m => m.TmdbId == sr.TmdbId, ct);

        var key = ReleaseNameParser.NormalizeKey(sr.Title, sr.Year);
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
