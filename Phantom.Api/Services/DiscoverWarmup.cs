namespace Phantom.Api.Services;

/// <summary>Fills Discover's cache of the library once the server is up (see <see cref="DiscoverService.WarmAsync"/>).</summary>
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
            log.LogInformation("Discover is ready ({Seconds:0.0} s)", (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // The first visit to Discover reads it instead.
            log.LogWarning(ex, "Couldn't get Discover ready ahead of time");
        }
    }
}
