using Microsoft.EntityFrameworkCore;

namespace Phantom.Api.Data;

public class PhantomDbContext(DbContextOptions<PhantomDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<EpisodeWatchState> EpisodeWatchStates => Set<EpisodeWatchState>();
    public DbSet<ShowWatchlistItem> ShowWatchlist => Set<ShowWatchlistItem>();
    public DbSet<ShowRating> ShowRatings => Set<ShowRating>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<Credit> Credits => Set<Credit>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<BlockedRelease> BlockedReleases => Set<BlockedRelease>();
    public DbSet<WatchState> WatchStates => Set<WatchState>();
    public DbSet<WatchlistItem> Watchlist => Set<WatchlistItem>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistItem> PlaylistItems => Set<PlaylistItem>();
    public DbSet<ServerDownload> ServerDownloads => Set<ServerDownload>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<ServerSettings> Settings => Set<ServerSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Account names (what people sign in with) are unique on the server; profile names only within their account.
        b.Entity<User>().HasIndex(u => u.Username).IsUnique().HasFilter("\"ParentUserId\" IS NULL");
        b.Entity<User>().HasIndex(u => new { u.ParentUserId, u.Username }).IsUnique().HasFilter("\"ParentUserId\" IS NOT NULL");

        b.Entity<Movie>().HasIndex(m => m.SortTitle);
        b.Entity<Movie>().HasIndex(m => m.TmdbId);
        b.Entity<Movie>().HasIndex(m => m.ExternalKey).IsUnique();
        b.Entity<Movie>().HasMany(m => m.Genres).WithMany(g => g.Movies);
        b.Entity<Movie>().HasMany(m => m.Credits).WithOne().HasForeignKey(c => c.MovieId);

        b.Entity<Show>().HasIndex(s => s.SortTitle);
        b.Entity<Show>().HasIndex(s => s.TmdbId);
        b.Entity<Show>().HasIndex(s => s.ImdbId);
        b.Entity<Show>().HasIndex(s => s.ExternalKey).IsUnique();
        b.Entity<Show>().HasMany(s => s.Genres).WithMany(g => g.Shows).UsingEntity("GenreShow");
        b.Entity<Show>().HasMany(s => s.Credits).WithOne().HasForeignKey(c => c.ShowId);
        b.Entity<Show>().HasMany(s => s.Episodes).WithOne(e => e.Show).HasForeignKey(e => e.ShowId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Episode>().HasIndex(e => new { e.ShowId, e.SeasonNumber, e.EpisodeNumber }).IsUnique();

        b.Entity<Genre>().HasIndex(g => g.Name).IsUnique();
        b.Entity<Credit>().HasIndex(c => c.Name);

        b.Entity<Release>().HasIndex(r => r.InfoHash).IsUnique();
        b.Entity<Release>().HasOne(r => r.Movie).WithMany(m => m.Releases).HasForeignKey(r => r.MovieId);
        b.Entity<Release>().HasOne(r => r.Show).WithMany(s => s.Releases).HasForeignKey(r => r.ShowId);
        b.Entity<Release>().HasIndex(r => new { r.ShowId, r.SeasonNumber, r.EpisodeNumber });
        b.Entity<BlockedRelease>().HasKey(r => r.InfoHash);

        b.Entity<WatchState>().HasKey(w => new { w.UserId, w.MovieId });
        b.Entity<WatchlistItem>().HasKey(w => new { w.UserId, w.MovieId });
        b.Entity<EpisodeWatchState>().HasKey(w => new { w.UserId, w.EpisodeId });
        b.Entity<ShowWatchlistItem>().HasKey(w => new { w.UserId, w.ShowId });
        b.Entity<ShowRating>().HasKey(r => new { r.UserId, r.ShowId });
        // A playlist entry goes with its movie or show.
        b.Entity<PlaylistItem>().HasOne(i => i.Movie).WithMany().HasForeignKey(i => i.MovieId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlaylistItem>().HasOne(i => i.Show).WithMany().HasForeignKey(i => i.ShowId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlaylistItem>().HasOne(i => i.Episode).WithMany().HasForeignKey(i => i.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlaylistItem>().Ignore(i => i.Key);

        b.Entity<Collection>().HasMany(c => c.Movies).WithMany();
        b.Entity<Playlist>().HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PlaylistId).OnDelete(DeleteBehavior.Cascade);
    }
}
