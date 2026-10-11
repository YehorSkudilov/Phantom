using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Metadata;

/// <summary>
/// Keeps the whole library's metadata (posters, cast, ratings...) fresh: it fills in titles just imported (sources add
/// them bare), and refreshes movies and shows that haven't been for <see cref="RefreshAfter"/> (shows still airing every
/// <see cref="AiringRefreshAfter"/>, for their new episodes). An admin's "Refresh all metadata" (see ServerController)
/// queues everything.
/// </summary>
/// <remarks>
/// Several titles are looked up at once (<see cref="Workers"/>), and while there's a backlog it goes straight on to the
/// next batch. While someone is watching it drops to one at a time and its usual pace (see <see cref="PlaybackActivity"/>).
/// </remarks>
public class MetadataRefreshService(IServiceScopeFactory scopes, PlaybackActivity activity, ILogger<MetadataRefreshService> log) : BackgroundService
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan AiringRefreshAfter = TimeSpan.FromDays(3);

    /// <summary>TMDB lookups at once; well inside its rate limit (about 40 requests a second).</summary>
    private const int Workers = 6;
    private const int BatchSize = 120;
    private const int ShowBatchSize = 60;

    /// <summary>Movies waiting for a refresh.</summary>
    public static IQueryable<Movie> Due(PhantomDbContext db)
    {
        var cutoff = DateTime.UtcNow - RefreshAfter;
        return db.Movies.Where(m => m.MetadataRefreshedAt == null || m.MetadataRefreshedAt < cutoff);
    }

    /// <summary>Shows waiting for a refresh.</summary>
    public static IQueryable<Show> DueShows(PhantomDbContext db)
    {
        var cutoff = DateTime.UtcNow - RefreshAfter;
        var airingCutoff = DateTime.UtcNow - AiringRefreshAfter;
        // One still without a poster is tried again every few hours: it may have been looked up before TVmaze was used,
        // or TVmaze may have been busy.
        var posterCutoff = DateTime.UtcNow - TimeSpan.FromHours(6);
        return db.Shows.Where(s => s.MetadataRefreshedAt == null || s.MetadataRefreshedAt < cutoff
                                   || (s.Status != "Ended" && s.Status != "Canceled" && s.MetadataRefreshedAt < airingCutoff)
                                   || (s.PosterUrl == null && s.MetadataRefreshedAt < posterCutoff));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var backlog = false;
            try
            {
                backlog = await RefreshBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Metadata refresh batch failed");
            }
            await Task.Delay(backlog && !activity.IsWatching ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>Refreshes the next batch of movies and shows. True when there was a full batch (so more are waiting).</summary>
    private async Task<bool> RefreshBatchAsync(CancellationToken ct)
    {
        List<int> movieIds, showIds;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            // Movies need TMDB; shows get TVmaze without it (see TmdbMetadataProvider). Titles never looked up (just
            // imported) first.
            movieIds = scope.ServiceProvider.GetRequiredService<IMetadataProvider>().IsConfigured
                ? await Due(db).OrderBy(m => m.MetadataRefreshedAt != null).ThenBy(m => m.MetadataRefreshedAt)
                    .Select(m => m.Id).Take(BatchSize).ToListAsync(ct)
                : [];
            // Shows take a request per season, so fewer at a time.
            // Shows never looked up first, then ones still without a poster, then the oldest.
            showIds = await DueShows(db).OrderBy(s => s.MetadataRefreshedAt != null).ThenBy(s => s.PosterUrl != null).ThenBy(s => s.MetadataRefreshedAt)
                .Select(s => s.Id).Take(ShowBatchSize).ToListAsync(ct);
        }
        if (movieIds.Count + showIds.Count == 0) return false;

        var workers = activity.IsWatching ? 1 : Workers;
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct };
        // Movies and shows side by side: shows wait mostly on TVmaze's pace, which needn't hold the movies up.
        await Task.WhenAll(
            Parallel.ForEachAsync(movieIds, options, (id, token) => new ValueTask(RefreshAsync(id, isShow: false, token))),
            Parallel.ForEachAsync(showIds, options, (id, token) => new ValueTask(RefreshAsync(id, isShow: true, token))));
        log.LogInformation("Refreshed metadata for {Movies} movies and {Shows} shows ({Workers} at a time)", movieIds.Count, showIds.Count, workers);
        return movieIds.Count == BatchSize || showIds.Count == ShowBatchSize;
    }

    /// <summary>
    /// One title, in its own database context. Two lookups at once can both add the same new genre or collection; the
    /// one that loses is simply run again, and finds it there.
    /// </summary>
    private async Task RefreshAsync(int id, bool isShow, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var metadata = scope.ServiceProvider.GetRequiredService<IMetadataProvider>();
            try
            {
                if (isShow)
                {
                    var show = await db.Shows.Include(s => s.Genres).Include(s => s.Credits).Include(s => s.Episodes)
                        .AsSplitQuery().FirstOrDefaultAsync(s => s.Id == id, ct);
                    if (show is null) return;
                    await metadata.EnrichAsync(db, show, ct);
                    // Marked done even when the provider had nothing, so a show it can't find isn't retried every minute.
                    if (show.MetadataRefreshedAt is null || show.MetadataRefreshedAt < DateTime.UtcNow - AiringRefreshAfter)
                        show.MetadataRefreshedAt = DateTime.UtcNow;
                }
                else
                {
                    var movie = await db.Movies.Include(m => m.Genres).Include(m => m.Credits).FirstOrDefaultAsync(m => m.Id == id, ct);
                    if (movie is null) return;
                    await metadata.EnrichAsync(db, movie, ct);
                    if (movie.MetadataRefreshedAt is null || movie.MetadataRefreshedAt < DateTime.UtcNow - RefreshAfter)
                        movie.MetadataRefreshedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0)
            {
                log.LogDebug(ex, "Metadata for {Kind} {Id} clashed with another lookup; trying again", isShow ? "show" : "movie", id);
            }
        }
    }
}
