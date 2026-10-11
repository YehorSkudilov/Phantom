using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Api.Services.Metadata;

namespace Phantom.Api.Services.Sources;

public record ImportResult(int Releases, int NewMovies, int NewReleases);

/// <summary>Matches source releases to library movies, creating movies and releases as needed.</summary>
public class LibraryImporter(PhantomDbContext db, IMetadataProvider metadata, ILogger<LibraryImporter> log)
{
    public async Task<ImportResult> ImportAsync(Source source, IReadOnlyList<SourceRelease> releases, CancellationToken ct)
    {
        int newMovies = 0, newReleases = 0;
        var settings = await db.Settings.FirstAsync(ct);

        foreach (var sr in releases.Where(r => r.Seeders >= settings.MinSeeders || r.TorrentUrl is not null))
        {
            var existing = await db.Releases.FirstOrDefaultAsync(r => r.InfoHash == sr.InfoHash, ct);
            if (existing is not null)
            {
                existing.Seeders = sr.Seeders;
                existing.Leechers = sr.Leechers;
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

        log.LogInformation("Imported {Count} releases from {Source}: {Movies} new movies, {New} new releases",
            releases.Count, source.Name, newMovies, newReleases);
        return new ImportResult(releases.Count, newMovies, newReleases);
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
