namespace Phantom.Api.Services.Sources;

/// <summary>
/// Requests to torrent sites, which are often slow or briefly down: each is tried a few times, with a pause between,
/// before it counts as failed. A source that still fails a page keeps what it already fetched (see each source).
/// </summary>
public static class SourceRequests
{
    private const int Attempts = 3;

    /// <summary>A timeout, a dropped connection or an error page: worth another try, and not worth failing a sync over.</summary>
    public static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException;

    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> request, ILogger log, string what, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await request(ct);
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex, ct))
            {
                log.LogDebug("{What} failed ({Error}); trying again", what, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }
    }
}
