using Phantom.Api.Services.Torrents;
using Phantom.Shared;

namespace Phantom.Api.Services;

/// <summary>
/// Whether someone is watching right now, so background work (source imports, metadata) can back off and leave the
/// network, disk and database to playback: a player has reported in lately, or a stream has been read from.
/// </summary>
public class PlaybackActivity(SessionTracker sessions, TorrentEngine engine)
{
    private static readonly TimeSpan Recently = TimeSpan.FromSeconds(45);

    public bool IsWatching =>
        sessions.Active().Any(s => s.State is PlayerState.Playing or PlayerState.Buffering && DateTime.UtcNow - s.UpdatedAt < Recently)
        || engine.StreamedWithin(Recently);
}
