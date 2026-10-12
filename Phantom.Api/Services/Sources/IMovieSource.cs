using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>A torrent discovered by a source, before it is matched to a library movie or show.</summary>
public record SourceRelease
{
    /// <summary>The movie's title, or for a show's release (<see cref="IsShow"/>) the show's.</summary>
    public required string Title { get; init; }
    public int? Year { get; init; }
    public required string ReleaseName { get; init; }
    public required string InfoHash { get; init; }
    public string? MagnetUri { get; init; }
    public string? TorrentUrl { get; init; }
    public string? Quality { get; init; }
    public string? Codec { get; init; }
    public long SizeBytes { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }

    /// <summary>
    /// Part of a TV show: one episode (<see cref="Episode"/> set), a season pack (only <see cref="Season"/>), or several
    /// seasons (neither).
    /// </summary>
    public bool IsShow { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }

    // Optional metadata some sources supply directly (used when no TMDB key is configured).
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string? Director { get; init; }
    public int? RuntimeMinutes { get; init; }
}

/// <summary>A built-in torrent source. Despite the name it can find shows as well as movies (see <see cref="SourceRelease.IsShow"/>).</summary>
public interface IMovieSource
{
    SourceType Type { get; }
    Task<IReadOnlyList<SourceRelease>> FetchAsync(Source source, CancellationToken ct);
}

/// <summary>A source that works through its whole catalogue a batch per sync.</summary>
public interface IBackfillingSource
{
    /// <summary>True while there's catalogue left; the sync service then runs it again shortly.</summary>
    bool HasMore(Source source);

    /// <summary>How much of the catalogue is in, as a percentage (100 once done), for the admin's progress bar.</summary>
    double CatalogueProgress(Source source);
}
