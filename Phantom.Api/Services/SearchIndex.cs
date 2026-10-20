using Microsoft.EntityFrameworkCore;
using Phantom.Api.Data;

namespace Phantom.Api.Services;

/// <summary>
/// Search's view of the library, kept in memory: every title's names, and every person's name with what they're in.
/// Searching used to match the text against every credit in the database (several times per search), which SQLite
/// can only do by reading the whole table: slow on a big library. The index is read once, and again in the background
/// whenever the library has changed, the one before served meanwhile.
/// </summary>
public sealed class SearchIndex(IServiceScopeFactory scopes, ILogger<SearchIndex> log)
{
    public sealed record Entry(TitleRef Ref, string Title, string? OriginalTitle, double? Rating);

    /// <summary>A person, and every title they're credited in.</summary>
    public sealed record Person(string Name, TitleRef[] Titles);

    private sealed record Snapshot(string Version, Entry[] Titles, Person[] People)
    {
        public Dictionary<TitleRef, Entry> ById { get; } = Titles.ToDictionary(t => t.Ref);
    }

    private Snapshot? _current;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private int _refreshing;

    /// <summary>
    /// The best rated <paramref name="take"/> movies and shows whose name, or a credited person's name, contains
    /// <paramref name="text"/> (title matches first), and the people whose name does. Some may not be visible to the
    /// profile searching, so ask for more than will be shown.
    /// </summary>
    public async Task<(List<int> Movies, List<int> Shows, List<Person> People)> FindAsync(string text, int take, CancellationToken ct)
    {
        var index = await CurrentAsync(ct);
        var matched = new HashSet<TitleRef>();
        var byTitle = new HashSet<TitleRef>();
        foreach (var t in index.Titles)
            if (t.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || t.OriginalTitle?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                byTitle.Add(t.Ref);
        var people = index.People.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var p in people) matched.UnionWith(p.Titles);
        matched.UnionWith(byTitle);
        List<int> Best(bool shows) => matched.Where(r => r.IsShow == shows && index.ById.ContainsKey(r))
            .OrderByDescending(byTitle.Contains).ThenByDescending(r => index.ById[r].Rating ?? 0)
            .Take(take).Select(r => r.Id).ToList();
        return (Best(false), Best(true), people);
    }

    /// <summary>Reads the index now, so the first search doesn't wait for it.</summary>
    public Task WarmAsync(CancellationToken ct) => CurrentAsync(ct);

    private async Task<Snapshot> CurrentAsync(CancellationToken ct)
    {
        if (_current is { } current)
        {
            if (Interlocked.Exchange(ref _refreshing, 1) == 0) _ = RefreshIfChangedAsync(current);
            return current;
        }
        await _loading.WaitAsync(ct);
        try
        {
            return _current ??= await LoadAsync(ct);
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task RefreshIfChangedAsync(Snapshot current)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            if (await VersionAsync(db, CancellationToken.None) != current.Version) _current = await LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The index already there goes on being used; the next search tries again.
            log.LogWarning(ex, "Couldn't refresh the search index");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>What changes whenever a title or credit is added, removed or renamed (ids only grow).</summary>
    private static async Task<string> VersionAsync(PhantomDbContext db, CancellationToken ct)
    {
        var movies = await db.Movies.CountAsync(ct);
        var lastMovie = await db.Movies.MaxAsync(m => (int?)m.Id, ct);
        var shows = await db.Shows.CountAsync(ct);
        var lastShow = await db.Shows.MaxAsync(s => (int?)s.Id, ct);
        var credits = await db.Credits.CountAsync(ct);
        var lastCredit = await db.Credits.MaxAsync(c => (int?)c.Id, ct);
        return $"{movies}:{lastMovie}:{shows}:{lastShow}:{credits}:{lastCredit}";
    }

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var version = await VersionAsync(db, ct);
        var movies = await db.Movies.AsNoTracking().Select(m => new { m.Id, m.Title, m.OriginalTitle, m.Rating }).ToListAsync(ct);
        var shows = await db.Shows.AsNoTracking().Select(s => new { s.Id, s.Title, s.OriginalTitle, s.Rating }).ToListAsync(ct);
        var credits = await db.Credits.AsNoTracking().Select(c => new { c.Name, c.MovieId, c.ShowId }).ToListAsync(ct);

        var titles = movies.Select(m => new Entry(new TitleRef(false, m.Id), m.Title, m.OriginalTitle, m.Rating))
            .Concat(shows.Select(s => new Entry(new TitleRef(true, s.Id), s.Title, s.OriginalTitle, s.Rating)))
            .ToArray();
        var people = credits
            .Select(c => (c.Name, Ref: c.MovieId is { } m ? new TitleRef(false, m) : c.ShowId is { } s ? new TitleRef(true, s) : (TitleRef?)null))
            .Where(c => c.Ref is not null)
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Person(g.First().Name, g.Select(c => c.Ref!.Value).Distinct().ToArray()))
            .ToArray();
        return new Snapshot(version, titles, people);
    }
}
