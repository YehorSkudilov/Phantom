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
    string? MaxContentRating,
    int? AccountId = null,
    // One of ProfileAvatars.All, shown in place of the name's initial; null for the initial.
    string? Avatar = null);

/// <summary>Plex Home style managed profile (child/guest accounts under the owner).</summary>
public record CreateProfileRequest(string Username, string? Pin, string? MaxContentRating, string? AvatarColor, string? Avatar = null);

/// <summary>
/// A profile's name and picture. Null leaves the name as it is; an empty <see cref="Avatar"/> goes back to the
/// initial. The account's own (default) profile is renamed with the account (UpdateAccountRequest), its name being
/// what it signs in with.
/// </summary>
public record UpdateProfileRequest(string? Username, string? Avatar);

/// <summary>The pictures a profile can have, drawn on its colour.</summary>
public static class ProfileAvatars
{
    /// <summary>The ones to pick from: emoji, in colour.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "👽", "🤖", "👻", "👾", "🎃", "🦄", "🐲", "🦖",
        "🐱", "🐶", "🦊", "🐼", "🐨", "🦁", "🐯", "🐸",
        "🐵", "🐙", "🦋", "🐧", "🦉", "🐢", "🐝", "🦈",
        "🚀", "📺", "🎮", "🎸", "🍿", "🍕", "🍩", "🌈",
    ];

    /// <summary>The first set, Material icon names (drawn in white): still shown and kept for profiles that have one.</summary>
    public static readonly IReadOnlyList<string> Legacy =
    [
        "face", "sentiment_very_satisfied", "pets", "cruelty_free", "emoji_nature",
        "local_florist", "rocket_launch", "sports_esports", "sports_soccer", "music_note",
        "headphones", "star", "favorite", "bolt", "local_pizza",
        "icecream", "directions_car", "flight", "wb_sunny", "theater_comedy",
    ];

    public static bool IsKnown(string avatar) => All.Contains(avatar) || Legacy.Contains(avatar);
}

public record SwitchProfileRequest(int ProfileId, string? Pin);

/// <summary>Changes the signed-in account's username and/or password; the current password confirms it.</summary>
public record UpdateAccountRequest(string CurrentPassword, string? Username, string? NewPassword);

/// <summary>A TV asking to be signed in with a code (see PairingController).</summary>
public record PairingStartRequest(string? DeviceName);

public record PairingStartResponse(string PairingId, string Code, DateTime ExpiresAt);

public record PairingApproveRequest(string Code);

public record PairingApprovedResponse(string DeviceName);

// ---------- Movies ----------

/// <summary>What a library item is. Rows, search results and lists mix movies and shows.</summary>
public enum MediaKind { Movie, Show }

/// <summary>
/// A movie or a show (<see cref="Kind"/>) on a row, in a grid or in search results. For a show, <see cref="Watched"/>
/// means every episode is, <see cref="ProgressPercent"/> is how many are, and <see cref="Caption"/> says which episode
/// is next ("S2 · E5") or how many seasons there are.
/// </summary>
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
    string? BackdropUrl = null,
    MediaKind Kind = MediaKind.Movie,
    string? Caption = null);

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

/// <summary>
/// A torrent release of a movie or an episode ("version" in Plex terms). For an episode, <see cref="Covers"/> says when
/// it's a pack ("Season 2", "Several seasons") rather than just that episode.
/// </summary>
public record ReleaseDto(
    int Id,
    string Name,
    string Source,
    string? Quality,
    string? Codec,
    long SizeBytes,
    int Seeders,
    int Leechers,
    bool AvailableOffline,
    string? Covers = null);

public record PlaybackStateDto(long PositionMs, long DurationMs, bool Watched, int ViewCount, DateTime? LastViewedAt)
{
    public double ProgressPercent => DurationMs <= 0 ? 0 : Math.Clamp(PositionMs * 100.0 / DurationMs, 0, 100);
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

// ---------- Shows ----------

public record ShowDetailDto(
    int Id,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string? ContentRating,
    string? Network,
    string? Status,
    int? EpisodeRuntimeMinutes,
    string? TrailerUrl,
    string? ImdbId,
    int? TmdbId,
    IReadOnlyList<string> Genres,
    IReadOnlyList<CastMemberDto> Cast,
    IReadOnlyList<CrewMemberDto> Crew,
    IReadOnlyList<SeasonDto> Seasons,
    // What Play / Resume plays: the episode in progress, else the one after the last watched, else the first.
    EpisodeDto? NextEpisode,
    bool OnWatchlist,
    DateTime AddedAt,
    // This profile's rating, out of 10.
    double? UserRating = null);

/// <summary>A season, with how many of its episodes can be played (have a release) and have been watched.</summary>
public record SeasonDto(int Number, string Title, int EpisodeCount, int AvailableCount, int WatchedCount);

public record EpisodeDto(
    int Id,
    int ShowId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
    string? StillUrl,
    DateOnly? AirDate,
    int? RuntimeMinutes,
    bool Available,
    PlaybackStateDto Playback)
{
    /// <summary>"S02E05".</summary>
    public string Code => $"S{SeasonNumber:00}E{EpisodeNumber:00}";

    /// <summary>"5. The Title", or "Episode 5" when it has no title yet.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"Episode {EpisodeNumber}" : $"{EpisodeNumber}. {Title}";
}

/// <summary>An episode with what the player needs: its show, its releases and the episode after it.</summary>
public record EpisodeDetailDto(
    EpisodeDto Episode,
    string ShowTitle,
    string? ShowPosterUrl,
    string? ShowBackdropUrl,
    IReadOnlyList<ReleaseDto> Releases,
    EpisodeDto? Next);

public enum MovieSort { Title, Year, AddedAt, Rating, Runtime, LastViewed, Random }

public record MovieQuery
{
    public string? Search { get; init; }
    public string? Genre { get; init; }

    /// <summary>The language it was made in: an ISO 639-1 code (see <see cref="FilterChoice"/>).</summary>
    public string? Language { get; init; }

    /// <summary>A country it was made in: an ISO 3166-1 code.</summary>
    public string? Country { get; init; }
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

    /// <summary>Movies, or shows (which ignore Quality, Director and the Runtime sort).</summary>
    public MediaKind Kind { get; init; } = MediaKind.Movie;

    /// <summary>
    /// Movies and shows together, sorted as one list (<see cref="Kind"/> is then ignored). A Quality or Director filter
    /// leaves it movies only, as shows have neither.
    /// </summary>
    public bool AllKinds { get; init; }
    public bool Descending { get; init; } = true;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public record LibraryFiltersDto(
    IReadOnlyList<string> Genres,
    IReadOnlyList<int> Years,
    IReadOnlyList<string> Qualities,
    IReadOnlyList<string> ContentRatings,
    IReadOnlyList<FilterChoice>? Languages = null,
    IReadOnlyList<FilterChoice>? Countries = null);

/// <summary>A choice in a filter that's sent as a code but shown by name: a language ("zh", "Chinese") or a country.</summary>
public record FilterChoice(string Code, string Name);

/// <summary>A titled row on the home screen (Continue Watching, Recently Added, ...).</summary>
public record HubDto(string Key, string Title, IReadOnlyList<MovieSummaryDto> Items);

public record SearchResultDto(IReadOnlyList<MovieSummaryDto> Movies, IReadOnlyList<string> People, IReadOnlyList<string> Genres);

// ---------- Playback ----------

/// <summary>Where playback is; for an episode <see cref="EpisodeId"/> is set and MovieId is 0.</summary>
public record TimelineUpdate(int MovieId, int? ReleaseId, long PositionMs, long DurationMs, PlayerState State, string? ClientName, int? EpisodeId = null);

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
    long ResumePositionMs,
    string? ThumbnailUrl = null,
    int? EpisodeId = null,
    // All the seek previews made so far in one download (see StreamControllerBase.Thumbnails).
    string? ThumbnailPackUrl = null,
    // The conversion as HLS, for Apple's player, which can't play the piped one at TranscodeUrl. "{session}" is for the
    // client to fill in with a new GUID each time it starts converting (see HlsSessions on the server).
    string? HlsUrl = null);

/// <summary>
/// What the server is doing to get a movie playing, for the player's loading screen: <see cref="Message"/> in words,
/// the numbers behind it, and <see cref="Ready"/> once the file is complete.
/// </summary>
public record StreamStatusDto(
    string Message,
    int Peers,
    double Progress,
    long DownloadRate,
    bool Ready,
    // Towards starting playback (not the whole movie): how much of the opening buffer is in (0-1), and about how long
    // the rest will take at the current speed.
    double? BufferProgress = null,
    int? SecondsToStart = null,
    // Downloading slower than the movie plays, so it would keep pausing: worth saving on the server instead.
    bool TooSlow = false,
    long NeededRate = 0,
    // How far into the video (0-1) it's there to play without waiting, unbroken from the position asked about: all of
    // it once the server has the whole file. The player shows it on its timeline.
    double? AvailableTo = null);

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
    DateTime UpdatedAt,
    int? EpisodeId = null);

// ---------- Lists ----------

public record CollectionDto(int Id, string Name, string? Description, string? PosterUrl, int Count);

public record CollectionDetailDto(int Id, string Name, string? Description, string? PosterUrl, IReadOnlyList<MovieSummaryDto> Movies);

public record CreateCollectionRequest(string Name, string? Description);

public record PlaylistDto(int Id, string Name, int Count, long TotalDurationMinutes);

/// <summary>
/// A playlist's entries in order. An episode's is its show's card with the episode in its caption; <paramref name="Keys"/>
/// names each ("m12" movie 12, "s4" show 4, "e99" episode 99), in the same order.
/// </summary>
public record PlaylistDetailDto(int Id, string Name, IReadOnlyList<MovieSummaryDto> Items, IReadOnlyList<string>? Keys = null);

/// <summary>A playlist with any of the asked-about entries in it, and which of them.</summary>
public record PlaylistMembershipDto(int Id, string Name, IReadOnlyList<string> Keys);

public record CreatePlaylistRequest(string Name);

/// <summary>
/// What to add to a playlist: a movie, or with <paramref name="ShowId"/> a whole show, or with
/// <paramref name="EpisodeIds"/> episodes (one, or a season's), in that order.
/// </summary>
public record AddItemRequest(int MovieId, int? ShowId = null, IReadOnlyList<int>? EpisodeIds = null);

/// <summary>
/// The playlist's new order: by movie id, or with <paramref name="Keys"/> all its entries ("m12", "s4", "e99").
/// </summary>
public record ReorderRequest(IReadOnlyList<int> MovieIds, IReadOnlyList<string>? Keys = null);

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
    DateTime? CompletedAt,
    DateTime? ExpiresAt = null,
    // For an episode MovieId is 0 and MovieTitle reads "Show · S02E05".
    int? EpisodeId = null,
    int? ShowId = null,
    // Only for a device to copy (see CreateServerDownloadRequest.ForDevice): the device deletes it once it has it.
    bool ForDevice = false,
    // The movie's poster, or for an episode its show's: the Downloads grid shows it.
    string? PosterUrl = null);

/// <summary>
/// Saves a movie, or with <see cref="EpisodeId"/> an episode (MovieId 0), on the server. <see cref="ForDevice"/>: only
/// so a device can download it (the server has to have it whole to send it), not saved for the profile; the device
/// deletes it once it has its copy. One the profile has saved already is used as it is, and not deleted.
/// </summary>
public record CreateServerDownloadRequest(int MovieId, int? ReleaseId, int? EpisodeId = null, bool ForDevice = false);

// ---------- Sources ----------

// Stored as integers: append new types, never reorder. Torznab is no longer used (sources are built in) but keeps its
// number so the others don't shift.
public enum SourceType { InternetArchive, Torznab, Yts, PirateBay, Knaben, Eztv, WorldCinema }

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
    string? LastSyncResult,
    bool IsSyncing = false,
    double? CatalogueProgress = null)
{
    /// <summary>How the server starts <see cref="LastSyncResult"/> when the last check failed.</summary>
    public const string FailedPrefix = "Failed: ";

    // Older servers wrote "Error: ".
    public bool LastSyncFailed => LastSyncResult?.StartsWith(FailedPrefix) == true || LastSyncResult?.StartsWith("Error: ") == true;

    /// <summary>
    /// The last check, with when it was ("5 min ago · Checked 300 torrents: 2 new movies."). Empty while a check is
    /// running: what's showing then is that it's checking, not how an earlier one went.
    /// </summary>
    public string StatusText =>
        IsSyncing || string.IsNullOrWhiteSpace(LastSyncResult) ? ""
        : LastSyncAt is { } at ? $"{Ago(at)} · {LastSyncResult}" : LastSyncResult;

    private static string Ago(DateTime at)
    {
        var age = DateTime.UtcNow - DateTime.SpecifyKind(at, DateTimeKind.Utc);
        return age.TotalMinutes switch
        {
            < 1 => "Just now",
            < 60 => $"{(int)age.TotalMinutes} min ago",
            < 60 * 24 => $"{(int)age.TotalHours} h ago",
            _ => $"{(int)age.TotalDays} d ago",
        };
    }
}


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
    bool MetadataProviderConfigured,
    int ShowCount = 0);

/// <summary>The library-wide metadata refresh: whether it can run (a TMDB key is set) and how many movies are waiting.</summary>
public record MetadataStatusDto(bool Configured, int Pending, int Total);

public record ServerSettingsDto(
    string ServerName,
    int MaxActiveDownloads,
    int MaxDownloadKbps,
    int MaxUploadKbps,
    bool SeedAfterDownload,
    int StreamCacheHours,
    bool AutoDownloadNewReleases,
    string? PreferredQuality,
    int MinSeeders,
    int KeepServerDownloadsDays = 7,
    int MaxStreamCacheGb = 0);

// ---------- Storage ----------

/// <summary>What's taking space on the server's disk, for Manage server → Storage.</summary>
public record StorageDto(
    long DiskFreeBytes,
    long DiskTotalBytes,
    long StreamCacheBytes,
    long SavedBytes,
    long PreviewBytes,
    long DatabaseBytes,
    int MaxStreamCacheGb,
    int MinFreeDiskGb,
    IReadOnlyList<StoredTorrentDto> Items);

/// <summary>
/// One torrent's files on the server: a stream cache (cleared when idle) or a copy saved on the server
/// (<see cref="DownloadId"/> is its download, removed from Downloads).
/// </summary>
public record StoredTorrentDto(
    string InfoHash,
    string Title,
    bool SavedOnServer,
    long SizeBytes,
    DateTime? LastUsed,
    bool InUse,
    int? DownloadId)
{
    public string KindText => SavedOnServer ? "Saved on the server" : "Stream cache";
}
