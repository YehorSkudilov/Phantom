using Microsoft.EntityFrameworkCore;

namespace Phantom.Api.Data;

/// <summary>
/// Keeps SQLite's table statistics current. Without them its query planner guesses join orders, and on a large library
/// guessed wrong (e.g. scanning a profile's whole watch history once per show), making pages 5-10x slower.
/// </summary>
public static class DatabaseStatistics
{
    /// <summary>At startup: a full ANALYZE the first time (PRAGMA optimize skips never-analyzed databases), then optimize.</summary>
    public static async Task EnsureAsync(PhantomDbContext db, CancellationToken ct = default)
    {
        var analyzed = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'sqlite_stat1'").SingleAsync(ct) > 0;
        if (!analyzed) await db.Database.ExecuteSqlRawAsync("ANALYZE;", ct);
        await RefreshAsync(db, ct);
    }

    /// <summary>
    /// After the library has grown (imports): re-analyzes only the tables whose size has changed a lot since they were
    /// last analyzed, so it's cheap to call often. 0x10002 = check every table, not just ones this connection used.
    /// </summary>
    public static Task RefreshAsync(PhantomDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("PRAGMA optimize=0x10002;", ct);
}
