using Microsoft.EntityFrameworkCore;

namespace Phantom.Api.Data;

public class PhantomDbContext(DbContextOptions<PhantomDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<Credit> Credits => Set<Credit>();
    public DbSet<Release> Releases => Set<Release>();
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
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();

        b.Entity<Movie>().HasIndex(m => m.SortTitle);
        b.Entity<Movie>().HasIndex(m => m.TmdbId);
        b.Entity<Movie>().HasIndex(m => m.ExternalKey).IsUnique();
        b.Entity<Movie>().HasMany(m => m.Genres).WithMany(g => g.Movies);
        b.Entity<Movie>().HasMany(m => m.Credits).WithOne().HasForeignKey(c => c.MovieId);

        b.Entity<Genre>().HasIndex(g => g.Name).IsUnique();
        b.Entity<Credit>().HasIndex(c => c.Name);

        b.Entity<Release>().HasIndex(r => r.InfoHash).IsUnique();
        b.Entity<Release>().HasOne(r => r.Movie).WithMany(m => m.Releases).HasForeignKey(r => r.MovieId);

        b.Entity<WatchState>().HasKey(w => new { w.UserId, w.MovieId });
        b.Entity<WatchlistItem>().HasKey(w => new { w.UserId, w.MovieId });

        b.Entity<Collection>().HasMany(c => c.Movies).WithMany();
        b.Entity<Playlist>().HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PlaylistId).OnDelete(DeleteBehavior.Cascade);
    }
}
