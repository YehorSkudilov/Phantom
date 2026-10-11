using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Metadata;

/// <summary>
/// Keeps the whole library's metadata (posters, cast, ratings...) fresh: every minute it refreshes the next batch of
/// movies and shows that have never been refreshed or not for <see cref="RefreshAfter"/> (shows still airing every
/// <see cref="AiringRefreshAfter"/>, for their new episodes). An admin's "Refresh all metadata" (see ServerController)
/// queues everything.
/// </summary>
public class MetadataRefreshService(IServiceScopeFactory scopes, ILogger<MetadataRefreshService> log) : BackgroundService
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan AiringRefreshAfter = TimeSpan.FromDays(3);
    private const int BatchSize = 100;

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
        return db.Shows.Where(s => s.MetadataRefreshedAt == null || s.MetadataRefreshedAt < cutoff
                                   || (s.Status != "Ended" && s.Status != "Canceled" && s.MetadataRefreshedAt < airingCutoff));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Metadata refresh batch failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task RefreshBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var metadata = scope.ServiceProvider.GetRequiredService<IMetadataProvider>();
        if (!metadata.IsConfigured) return;
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();

        var batch = await Due(db).OrderBy(m => m.MetadataRefreshedAt).Take(BatchSize)
            .Include(m => m.Genres).Include(m => m.Credits).ToListAsync(ct);
        foreach (var movie in batch)
        {
            await metadata.EnrichAsync(db, movie, ct);
            // Marked done even when the provider had nothing, so a movie it can't find isn't retried every minute.
            if (movie.MetadataRefreshedAt is null || movie.MetadataRefreshedAt < DateTime.UtcNow - RefreshAfter)
                movie.MetadataRefreshedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        if (batch.Count > 0) log.LogInformation("Refreshed metadata for {Count} movies", batch.Count);

        // Shows take a request per season, so fewer at a time.
        var shows = await DueShows(db).OrderBy(s => s.MetadataRefreshedAt).Take(BatchSize / 10)
            .Include(s => s.Genres).Include(s => s.Credits).Include(s => s.Episodes).AsSplitQuery().ToListAsync(ct);
        foreach (var show in shows)
        {
            await metadata.EnrichAsync(db, show, ct);
            if (show.MetadataRefreshedAt is null || show.MetadataRefreshedAt < DateTime.UtcNow - AiringRefreshAfter)
                show.MetadataRefreshedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        if (shows.Count > 0) log.LogInformation("Refreshed metadata for {Count} shows", shows.Count);
    }
}
