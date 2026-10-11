using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services.Sources;

/// <summary>A torrent discovered by a source, before it is matched to a library movie.</summary>
public record SourceRelease
{
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

    // Optional metadata some sources supply directly (used when no TMDB key is configured).
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string? Director { get; init; }
    public int? RuntimeMinutes { get; init; }
}

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
