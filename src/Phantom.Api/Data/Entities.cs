using Phantom.Shared;

namespace Phantom.Api.Data;

public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }

    /// <summary>Set for managed (Plex Home) profiles; points at the owning account.</summary>
    public int? ParentUserId { get; set; }
    public string? PinHash { get; set; }
    public string? AvatarColor { get; set; }

    /// <summary>Parental control: highest content rating this profile may see (e.g. "PG-13").</summary>
    public string? MaxContentRating { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Movie
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string SortTitle { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public string? Tagline { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public double? Rating { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string? ContentRating { get; set; }
    public string? Studio { get; set; }
    public string? TrailerUrl { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public string? ExternalKey { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? MetadataRefreshedAt { get; set; }

    public List<Genre> Genres { get; set; } = [];
    public List<Credit> Credits { get; set; } = [];
    public List<Release> Releases { get; set; } = [];
}

public class Genre
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Movie> Movies { get; set; } = [];
}

public enum CreditKind { Cast, Director, Writer, Producer }

public class Credit
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public required string Name { get; set; }
    public CreditKind Kind { get; set; }
    public string? Character { get; set; }
    public string? ProfileUrl { get; set; }
    public int Order { get; set; }
}

/// <summary>One torrent that contains the movie.</summary>
public class Release
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int? SourceId { get; set; }
    public required string Name { get; set; }
    public string SourceName { get; set; } = "";
    public required string InfoHash { get; set; }
    public string? MagnetUri { get; set; }
    public string? TorrentUrl { get; set; }
    public string? Quality { get; set; }
    public string? Codec { get; set; }
    public long SizeBytes { get; set; }
    public int Seeders { get; set; }
    public int Leechers { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Path of the fully downloaded video file once the server keeps a permanent copy.</summary>
    public string? LocalFilePath { get; set; }
}

public class WatchState
{
    public int UserId { get; set; }
    public int MovieId { get; set; }
    public Movie? Movie { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public bool Watched { get; set; }
    public int ViewCount { get; set; }
    public double? UserRating { get; set; }
    public DateTime? LastViewedAt { get; set; }
}

public class WatchlistItem
{
    public int UserId { get; set; }
    public int MovieId { get; set; }
    public Movie? Movie { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class Collection
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? PosterUrl { get; set; }
    public int? TmdbCollectionId { get; set; }
    public List<Movie> Movies { get; set; } = [];
}

public class Playlist
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<PlaylistItem> Items { get; set; } = [];
}

public class PlaylistItem
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public int MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int Order { get; set; }
}

public class ServerDownload
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int ReleaseId { get; set; }
    public Release? Release { get; set; }
    public int? RequestedByUserId { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class Source
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public SourceType Type { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Categories { get; set; }
    public string? Query { get; set; }
    public bool Enabled { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 360;
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncResult { get; set; }
}

/// <summary>Single-row table holding admin-editable server settings.</summary>
public class ServerSettings
{
    public int Id { get; set; } = 1;
    public string ServerName { get; set; } = "Phantom";
    public int MaxActiveDownloads { get; set; } = 3;
    public int MaxDownloadKbps { get; set; }
    public int MaxUploadKbps { get; set; }
    public bool SeedAfterDownload { get; set; } = true;
    public int StreamCacheHours { get; set; } = 24;
    public bool AutoDownloadNewReleases { get; set; }
    public string? PreferredQuality { get; set; } = "1080p";
    public int MinSeeders { get; set; } = 1;
}
