using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;
using Phantom.Shared;

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

    // Cuts a running worker's pause short when its source is asked for ("Check now").
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _wake = new();

    private SemaphoreSlim Wake(int sourceId) => _wake.GetOrAdd(sourceId, _ => new SemaphoreSlim(0));

    // While someone's watching, only one source fetches at a time.
    private readonly SemaphoreSlim _watchingSlot = new(1, 1);

    /// <summary>Whether a source is being synced right now, or has been asked to be and is about to.</summary>
    public bool IsSyncing(int sourceId) => _running.ContainsKey(sourceId) || _forced.ContainsKey(sourceId);

    /// <summary>Queue a sync now. Null syncs every enabled source.</summary>
    public void RequestSync(int? sourceId = null)
    {
        // Marked straight away, so the admin page shows it as checking from the moment it's asked.
        if (sourceId is { } id)
        {
            _forced[id] = true;
            if (_wake.TryGetValue(id, out var wake) && wake.CurrentCount == 0) wake.Release();
        }
        _requests.Writer.TryWrite(sourceId);
    }

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
                if (await _requests.Reader.ReadAsync(timeout.Token) is null)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
                    foreach (var sid in await db.Sources.Where(s => s.Enabled).Select(s => s.Id).ToListAsync(stoppingToken)) RequestSync(sid);
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
        var providers = scope.ServiceProvider.GetServices<IMovieSource>().ToList();
        var sources = await db.Sources.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);
        foreach (var source in sources)
        {
            // A running worker picks up a request for its own source (see RunSourceAsync).
            if (_running.ContainsKey(source.Id)) continue;
            // Mid-catalogue (say after a restart) it carries on a minute after the last batch, not the whole interval.
            var midCatalogue = providers.FirstOrDefault(p => p.Type == source.Type) is IBackfillingSource backfill && backfill.HasMore(source);
            var due = _forced.ContainsKey(source.Id)
                      || source.LastSyncAt is null
                      || source.LastSyncAt.Value.AddMinutes(midCatalogue ? 1 : source.SyncIntervalMinutes) <= DateTime.UtcNow;
            if (!due) continue;
            _running[source.Id] = Task.Run(() => RunSourceAsync(source.Id, ct), CancellationToken.None);
        }
        // Asked for but switched off (or gone): nothing to do.
        foreach (var id in _forced.Keys.Where(id => sources.All(s => s.Id != id))) _forced.TryRemove(id, out _);
    }

    /// <summary>One source's worker: a sync, then further catalogue batches back to back until the catalogue is in.</summary>
    private async Task RunSourceAsync(int sourceId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _forced.TryRemove(sourceId, out _);
                var more = await SyncOnceAsync(sourceId, ct);
                // Asked for again while it ran: go again now, so the result shown is from after the request.
                if (_forced.ContainsKey(sourceId)) continue;
                if (!more) break;
                // A "Check now" ends the pause early.
                if (activity.IsWatching) await Wake(sourceId).WaitAsync(WatchingPause, ct);
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
            hasMore = provider is IBackfillingSource backfill && backfill.HasMore(source);
            source.LastSyncResult = Describe(result) + (hasMore ? " Still working through the catalogue." : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Sync failed for source {Source}", source.Name);
            source.LastSyncResult = SourceDto.FailedPrefix + Explain(ex);
        }
        finally
        {
            if (watching) _watchingSlot.Release();
        }
        source.LastSyncAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return hasMore;
    }

    /// <summary>"Checked 300 torrents: 2 new movies, 1 new show, 5 new versions." or "... nothing new."</summary>
    private static string Describe(ImportResult result)
    {
        static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        var news = new List<string>();
        if (result.NewMovies > 0) news.Add(Count(result.NewMovies, "new movie", "new movies"));
        if (result.NewShows > 0) news.Add(Count(result.NewShows, "new show", "new shows"));
        // Releases beyond the new titles' own are new versions of ones already in the library.
        if (result.NewReleases > 0) news.Add(Count(result.NewReleases, "new version", "new versions"));
        return $"Checked {Count(result.Releases, "torrent", "torrents")}: {(news.Count == 0 ? "nothing new" : string.Join(", ", news))}.";
    }

    /// <summary>A sync failure in words.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        TaskCanceledException => "the site didn't answer in time. It's tried again on the next check.",
        HttpRequestException { StatusCode: { } status } => $"the site answered {(int)status} {status}. It may be down for now.",
        HttpRequestException => $"couldn't reach the site ({ex.Message}).",
        _ => ex.Message,
    };
}
