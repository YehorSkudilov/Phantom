using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Background worker that pulls every enabled source on its interval, so new movies
/// appear in the library automatically. Admins can also request an immediate sync.
/// </summary>
public class SourceSyncService(IServiceScopeFactory scopes, ILogger<SourceSyncService> log) : BackgroundService
{
    private readonly Channel<int?> _requests = Channel.CreateUnbounded<int?>();

    /// <summary>Queue a sync now. Null syncs every enabled source.</summary>
    public void RequestSync(int? sourceId = null) => _requests.Writer.TryWrite(sourceId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a moment to finish starting before hitting the network.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunDueAsync(forceId: null, force: false, stoppingToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            try
            {
                var id = await _requests.Reader.ReadAsync(timeout.Token);
                await RunDueAsync(id, force: true, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timer tick.
            }
        }
    }

    private async Task RunDueAsync(int? forceId, bool force, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var providers = scope.ServiceProvider.GetServices<IMovieSource>().ToDictionary(p => p.Type);

        var sources = await db.Sources.Where(s => s.Enabled).ToListAsync(ct);
        foreach (var source in sources)
        {
            var due = force
                ? forceId is null || forceId == source.Id
                : source.LastSyncAt is null || source.LastSyncAt.Value.AddMinutes(source.SyncIntervalMinutes) <= DateTime.UtcNow;
            if (!due || !providers.TryGetValue(source.Type, out var provider)) continue;

            try
            {
                var releases = await provider.FetchAsync(source, ct);
                var importer = scope.ServiceProvider.GetRequiredService<LibraryImporter>();
                var result = await importer.ImportAsync(source, releases, ct);
                source.LastSyncResult = $"OK: {result.Releases} found, {result.NewMovies} new movies, {result.NewReleases} new releases";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Sync failed for source {Source}", source.Name);
                source.LastSyncResult = $"Error: {ex.Message}";
            }
            source.LastSyncAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}
