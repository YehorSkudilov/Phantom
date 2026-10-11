using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Metadata;

/// <summary>
/// Keeps the whole library's metadata (posters, cast, ratings...) fresh: every minute it refreshes the next batch of
/// movies that have never been refreshed or not for <see cref="RefreshAfter"/>. An admin's "Refresh all metadata"
/// (see ServerController) queues every movie.
/// </summary>
public class MetadataRefreshService(IServiceScopeFactory scopes, ILogger<MetadataRefreshService> log) : BackgroundService
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    private const int BatchSize = 100;

    /// <summary>Movies waiting for a refresh.</summary>
    public static IQueryable<Movie> Due(PhantomDbContext db)
    {
        var cutoff = DateTime.UtcNow - RefreshAfter;
        return db.Movies.Where(m => m.MetadataRefreshedAt == null || m.MetadataRefreshedAt < cutoff);
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
    }
}
