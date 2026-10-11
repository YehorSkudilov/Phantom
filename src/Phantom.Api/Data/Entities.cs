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

/// <summary>A TV show. Its episodes come from TMDB (or, without a key, from the releases found for it).</summary>
public class Show
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string SortTitle { get; set; } = "";
    public string? OriginalTitle { get; set; }

    /// <summary>The year it first aired.</summary>
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public double? Rating { get; set; }
    public string? ContentRating { get; set; }
    public string? Network { get; set; }

    /// <summary>"Returning Series", "Ended", ... as TMDB has it.</summary>
    public string? Status { get; set; }
    public int? EpisodeRuntimeMinutes { get; set; }
    public string? TrailerUrl { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public string? ExternalKey { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? MetadataRefreshedAt { get; set; }

    public List<Genre> Genres { get; set; } = [];
    public List<Credit> Credits { get; set; } = [];
    public List<Episode> Episodes { get; set; } = [];
    public List<Release> Releases { get; set; } = [];
}

public class Episode
{
    public int Id { get; set; }
    public int ShowId { get; set; }
    public Show? Show { get; set; }

    /// <summary>0 for specials.</summary>
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Title { get; set; }
    public string? Overview { get; set; }
    public string? StillUrl { get; set; }
    public DateOnly? AirDate { get; set; }
    public int? RuntimeMinutes { get; set; }
    public int? TmdbId { get; set; }
}

public class Genre
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Movie> Movies { get; set; } = [];
    public List<Show> Shows { get; set; } = [];
}

public enum CreditKind { Cast, Director, Writer, Producer }

/// <summary>Someone in a movie's or a show's cast or crew (exactly one of MovieId and ShowId is set).</summary>
public class Credit
{
    public int Id { get; set; }
    public int? MovieId { get; set; }
    public int? ShowId { get; set; }
    public required string Name { get; set; }
    public CreditKind Kind { get; set; }
    public string? Character { get; set; }
    public string? ProfileUrl { get; set; }
    public int Order { get; set; }
}

/// <summary>
/// One torrent: a movie, or part of a show (exactly one of MovieId and ShowId is set). For a show it's a single
/// episode, a season pack (no EpisodeNumber) or several seasons (no SeasonNumber); which file in a pack is which
/// episode is worked out from the file names when it's played (see TorrentEngine.EpisodeFile).
/// </summary>
public class Release
{
    public int Id { get; set; }
    public int? MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int? ShowId { get; set; }
    public Show? Show { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
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

/// <summary>How far a profile has got with an episode (WatchState's counterpart for shows).</summary>
public class EpisodeWatchState
{
    public int UserId { get; set; }
    public int EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public bool Watched { get; set; }
    public int ViewCount { get; set; }
    public DateTime? LastViewedAt { get; set; }
}

public class ShowWatchlistItem
{
    public int UserId { get; set; }
    public int ShowId { get; set; }
    public Show? Show { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
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

/// <summary>A release the server keeps a copy of: a movie's, or one with the episode it was saved for.</summary>
public class ServerDownload
{
    public int Id { get; set; }
    public int? MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int? EpisodeId { get; set; }
    public Episode? Episode { get; set; }
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

    /// <summary>Movies saved on the server are deleted this many days after they finish; 0 keeps them for good.</summary>
    public int KeepServerDownloadsDays { get; set; } = 7;

    /// <summary>The stream cache is kept under this many GB (least recently watched cleared first); 0 for no limit.</summary>
    public int MaxStreamCacheGb { get; set; }
}
