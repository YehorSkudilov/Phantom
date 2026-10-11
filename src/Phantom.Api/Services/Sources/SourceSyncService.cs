using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services.Sources;

/// <summary>
/// Background worker that pulls every enabled source on its interval, so new movies and shows appear in the library
/// automatically. Admins can also request an immediate sync.
/// </summary>
/// <remarks>
/// Every source runs as its own worker, in parallel: fetching is mostly waiting on other sites, so they don't hold each
/// other up. A source still working through its catalogue starts its next batch straight away. While someone is
/// watching (see <see cref="PlaybackActivity"/>) it backs off: one source at a time, with a pause between batches, so
/// the stream gets the bandwidth, disk and database.
/// </remarks>
public class SourceSyncService(IServiceScopeFactory scopes, PlaybackActivity activity, ILogger<SourceSyncService> log) : BackgroundService
{
    /// <summary>Between catalogue batches while someone's watching.</summary>
    private static readonly TimeSpan WatchingPause = TimeSpan.FromMinutes(3);

    private readonly Channel<int?> _requests = Channel.CreateUnbounded<int?>();
    private readonly ConcurrentDictionary<int, Task> _running = new();
    private readonly ConcurrentDictionary<int, bool> _forced = new();

    // While someone's watching, only one source fetches at a time.
    private readonly SemaphoreSlim _watchingSlot = new(1, 1);

    /// <summary>Whether a source is being synced right now.</summary>
    public bool IsSyncing(int sourceId) => _running.ContainsKey(sourceId);

    /// <summary>Queue a sync now. Null syncs every enabled source.</summary>
    public void RequestSync(int? sourceId = null) => _requests.Writer.TryWrite(sourceId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a moment to finish starting before hitting the network.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await StartDueAsync(stoppingToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var id = await _requests.Reader.ReadAsync(timeout.Token);
                if (id is { } one) _forced[one] = true;
                else
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
                    foreach (var sid in await db.Sources.Where(s => s.Enabled).Select(s => s.Id).ToListAsync(stoppingToken)) _forced[sid] = true;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timer tick.
            }
        }
        await Task.WhenAll(_running.Values);
    }

    /// <summary>Starts a worker for every enabled source that's due (or asked for) and not already running.</summary>
    private async Task StartDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var sources = await db.Sources.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);
        foreach (var source in sources)
        {
            var due = _forced.TryRemove(source.Id, out _)
                      || source.LastSyncAt is null || source.LastSyncAt.Value.AddMinutes(source.SyncIntervalMinutes) <= DateTime.UtcNow;
            if (!due || _running.ContainsKey(source.Id)) continue;
            _running[source.Id] = Task.Run(() => RunSourceAsync(source.Id, ct), CancellationToken.None);
        }
    }

    /// <summary>One source's worker: a sync, then further catalogue batches back to back until the catalogue is in.</summary>
    private async Task RunSourceAsync(int sourceId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await SyncOnceAsync(sourceId, ct))
            {
                if (activity.IsWatching) await Task.Delay(WatchingPause, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Source worker {Id} stopped", sourceId);
        }
        finally
        {
            _running.TryRemove(sourceId, out _);
        }
    }

    /// <summary>Fetches and imports one batch. True when the source has more of its catalogue to come.</summary>
    private async Task<bool> SyncOnceAsync(int sourceId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId && s.Enabled, ct);
        var provider = scope.ServiceProvider.GetServices<IMovieSource>().FirstOrDefault(p => p.Type == source?.Type);
        if (source is null || provider is null) return false;

        var hasMore = false;
        var watching = activity.IsWatching;
        if (watching) await _watchingSlot.WaitAsync(ct);
        try
        {
            var releases = await provider.FetchAsync(source, ct);
            var result = await scope.ServiceProvider.GetRequiredService<LibraryImporter>().ImportAsync(source, releases, ct);
            source.LastSyncResult = $"OK: {result.Releases} found, {result.NewMovies} new movies, {result.NewShows} new shows, {result.NewReleases} new releases";
            hasMore = provider is IBackfillingSource backfill && backfill.HasMore(source);
            if (hasMore) source.LastSyncResult += " · still importing the catalogue";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Sync failed for source {Source}", source.Name);
            source.LastSyncResult = $"Error: {ex.Message}";
        }
        finally
        {
            if (watching) _watchingSlot.Release();
        }
        // Mid-catalogue it counts as due again in a minute, so a restart carries on rather than waiting out the interval.
        source.LastSyncAt = hasMore ? DateTime.UtcNow.AddMinutes(1 - source.SyncIntervalMinutes) : DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return hasMore;
    }
}
