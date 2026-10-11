namespace Phantom.Shared;

// ---------- Auth & users ----------

public record LoginRequest(string Username, string Password);

public record RegisterRequest(string Username, string Password);

public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);

public record UserDto(
    int Id,
    string Username,
    bool IsAdmin,
    bool IsManaged,
    bool HasPin,
    string? AvatarColor,
    string? MaxContentRating);

/// <summary>Plex Home style managed profile (child/guest accounts under the owner).</summary>
public record CreateProfileRequest(string Username, string? Pin, string? MaxContentRating, string? AvatarColor);

public record SwitchProfileRequest(int ProfileId, string? Pin);

// ---------- Movies ----------

public record MovieSummaryDto(
    int Id,
    string Title,
    int? Year,
    string? PosterUrl,
    double? Rating,
    int? RuntimeMinutes,
    string? ContentRating,
    bool Watched,
    double ProgressPercent,
    DateTime AddedAt,
    string? BestQuality,
    string? BackdropUrl = null);

public record MovieDetailDto(
    int Id,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Tagline,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    double? UserRating,
    int? RuntimeMinutes,
    string? ContentRating,
    string? Studio,
    string? TrailerUrl,
    string? ImdbId,
    int? TmdbId,
    IReadOnlyList<string> Genres,
    IReadOnlyList<CastMemberDto> Cast,
    IReadOnlyList<CrewMemberDto> Crew,
    IReadOnlyList<ReleaseDto> Releases,
    PlaybackStateDto Playback,
    bool OnWatchlist,
    bool DownloadedOnServer,
    DateTime AddedAt);

public record CastMemberDto(string Name, string? Character, string? ProfileUrl);

public record CrewMemberDto(string Name, string Job);

/// <summary>A torrent release of a movie ("version" in Plex terms).</summary>
public record ReleaseDto(
    int Id,
    string Name,
    string Source,
    string? Quality,
    string? Codec,
    long SizeBytes,
    int Seeders,
    int Leechers,
    bool AvailableOffline);

public record PlaybackStateDto(long PositionMs, long DurationMs, bool Watched, int ViewCount, DateTime? LastViewedAt)
{
    public double ProgressPercent => DurationMs <= 0 ? 0 : Math.Clamp(PositionMs * 100.0 / DurationMs, 0, 100);
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public enum MovieSort { Title, Year, AddedAt, Rating, Runtime, LastViewed, Random }

public record MovieQuery
{
    public string? Search { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
    public int? Decade { get; init; }
    public string? Quality { get; init; }
    public string? ContentRating { get; init; }
    public bool? Unwatched { get; init; }
    public bool? InProgress { get; init; }
    public string? StartsWith { get; init; }
    public string? Actor { get; init; }
    public string? Director { get; init; }
    public MovieSort Sort { get; init; } = MovieSort.AddedAt;
    public bool Descending { get; init; } = true;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public record LibraryFiltersDto(
    IReadOnlyList<string> Genres,
    IReadOnlyList<int> Years,
    IReadOnlyList<string> Qualities,
    IReadOnlyList<string> ContentRatings);

/// <summary>A titled row on the home screen (Continue Watching, Recently Added, ...).</summary>
public record HubDto(string Key, string Title, IReadOnlyList<MovieSummaryDto> Items);

public record SearchResultDto(IReadOnlyList<MovieSummaryDto> Movies, IReadOnlyList<string> People, IReadOnlyList<string> Genres);

// ---------- Playback ----------

public record TimelineUpdate(int MovieId, int? ReleaseId, long PositionMs, long DurationMs, PlayerState State, string? ClientName);

public enum PlayerState { Playing, Paused, Buffering, Stopped }

public record SetRatingRequest(double? Rating);

public record StreamInfoDto(
    int MovieId,
    int ReleaseId,
    string FileName,
    long FileSize,
    string DirectUrl,
    string? TranscodeUrl,
    IReadOnlyList<SubtitleTrackDto> Subtitles,
    long ResumePositionMs);

public record SubtitleTrackDto(string Id, string Language, string Label, string Url);

public record NowPlayingDto(
    string SessionId,
    string Username,
    int MovieId,
    string MovieTitle,
    string? PosterUrl,
    long PositionMs,
    long DurationMs,
    PlayerState State,
    string? ClientName,
    bool Transcoding,
    DateTime UpdatedAt);

// ---------- Lists ----------

public record CollectionDto(int Id, string Name, string? Description, string? PosterUrl, int Count);

public record CollectionDetailDto(int Id, string Name, string? Description, string? PosterUrl, IReadOnlyList<MovieSummaryDto> Movies);

public record CreateCollectionRequest(string Name, string? Description);

public record PlaylistDto(int Id, string Name, int Count, long TotalDurationMinutes);

public record PlaylistDetailDto(int Id, string Name, IReadOnlyList<MovieSummaryDto> Items);

public record CreatePlaylistRequest(string Name);

public record AddItemRequest(int MovieId);

public record ReorderRequest(IReadOnlyList<int> MovieIds);

// ---------- Server downloads ----------

public enum DownloadStatus { Queued, FetchingMetadata, Downloading, Paused, Completed, Failed, Cancelled }

public record ServerDownloadDto(
    int Id,
    int MovieId,
    string MovieTitle,
    int ReleaseId,
    string ReleaseName,
    DownloadStatus Status,
    double Progress,
    long DownloadRate,
    long UploadRate,
    int Peers,
    long SizeBytes,
    string? Error,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public record CreateServerDownloadRequest(int MovieId, int? ReleaseId);

// ---------- Sources ----------

// Stored as integers: append new types, never reorder.
public enum SourceType { InternetArchive, Torznab, Yts, PirateBay }

public record SourceDto(
    int Id,
    string Name,
    SourceType Type,
    string? BaseUrl,
    bool HasApiKey,
    string? Categories,
    string? Query,
    bool Enabled,
    int SyncIntervalMinutes,
    DateTime? LastSyncAt,
    string? LastSyncResult);

public record UpsertSourceRequest(
    string Name,
    SourceType Type,
    string? BaseUrl,
    string? ApiKey,
    string? Categories,
    string? Query,
    bool Enabled,
    int SyncIntervalMinutes);

// ---------- Server ----------

public record ServerInfoDto(
    string Name,
    string Version,
    int MovieCount,
    int ActiveTorrents,
    long DownloadRate,
    long UploadRate,
    long FreeDiskBytes,
    bool TranscoderAvailable,
    bool MetadataProviderConfigured);

public record ServerSettingsDto(
    string ServerName,
    int MaxActiveDownloads,
    int MaxDownloadKbps,
    int MaxUploadKbps,
    bool SeedAfterDownload,
    int StreamCacheHours,
    bool AutoDownloadNewReleases,
    string? PreferredQuality,
    int MinSeeders);
