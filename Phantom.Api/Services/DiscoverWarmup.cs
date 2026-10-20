namespace Phantom.Api.Services;

/// <summary>
/// Fills Discover's cache of the library and the search index once the server is up (see
/// <see cref="DiscoverService.WarmAsync"/>), so neither the first visit nor the first search waits for them.
/// </summary>
public sealed class DiscoverWarmup(IServiceScopeFactory scopes, ILogger<DiscoverWarmup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // After the database has been migrated, and out of the way of the server starting.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            using var scope = scopes.CreateScope();
            var started = DateTime.UtcNow;
            await scope.ServiceProvider.GetRequiredService<DiscoverService>().WarmAsync(stoppingToken);
            await scope.ServiceProvider.GetRequiredService<SearchIndex>().WarmAsync(stoppingToken);
            log.LogInformation("Discover and search are ready ({Seconds:0.0} s)", (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // The first visit to Discover, or the first search, reads it instead.
            log.LogWarning(ex, "Couldn't get Discover and search ready ahead of time");
        }
    }
}
