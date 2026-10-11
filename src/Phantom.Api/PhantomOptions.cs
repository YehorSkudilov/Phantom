namespace Phantom.Api;

public class PhantomOptions
{
    public const string Section = "Phantom";

    /// <summary>Root for the database, torrent cache and permanent downloads.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Optional TMDB v3 API key; enables posters, cast, trailers, ratings and collections.</summary>
    public string? TmdbApiKey { get; set; }

    /// <summary>HMAC key for signing JWTs. Generated and persisted under DataDirectory when empty.</summary>
    public string? JwtSigningKey { get; set; }

    public int TokenLifetimeDays { get; set; } = 30;

    /// <summary>Allow open sign-up after the first (admin) account exists.</summary>
    public bool AllowRegistration { get; set; }

    public string FfmpegPath { get; set; } = "ffmpeg";

    public int TorrentListenPort { get; set; } = 55125;

    /// <summary>Make sure the built-in sources (Internet Archive, YTS, The Pirate Bay) exist; see Program.SeedSourcesAsync.</summary>
    public bool SeedDefaultSources { get; set; } = true;

    public string DatabasePath => Path.Combine(DataDirectory, "phantom.db");
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public string MediaDirectory => Path.Combine(DataDirectory, "media");
    public string EngineDirectory => Path.Combine(DataDirectory, "engine");
}
