using System.Collections.Concurrent;
using Phantom.Shared;

namespace Phantom.Api.Services;

/// <summary>In-memory "Now Playing" dashboard, fed by client timeline updates.</summary>
public class SessionTracker
{
    private readonly ConcurrentDictionary<string, NowPlayingDto> _sessions = new();

    public void Update(NowPlayingDto session)
    {
        if (session.State == PlayerState.Stopped) _sessions.TryRemove(session.SessionId, out _);
        else _sessions[session.SessionId] = session;
    }

    public IReadOnlyList<NowPlayingDto> Active()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-2);
        foreach (var stale in _sessions.Values.Where(s => s.UpdatedAt < cutoff))
            _sessions.TryRemove(stale.SessionId, out _);
        return _sessions.Values.OrderBy(s => s.Username).ToList();
    }
}
