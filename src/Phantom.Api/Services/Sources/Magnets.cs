namespace Phantom.Api.Services.Sources;

/// <summary>Builds magnet links for sources that only publish an info hash.</summary>
public static class Magnets
{
    // Public trackers so a bare hash finds peers quickly; DHT covers the rest.
    private static readonly string[] Trackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://open.stealth.si:80/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.tiny-vps.com:6969/announce",
    ];

    public static string Build(string infoHash, string name) =>
        $"magnet:?xt=urn:btih:{infoHash.ToLowerInvariant()}&dn={Uri.EscapeDataString(name)}" +
        string.Concat(Trackers.Select(t => "&tr=" + Uri.EscapeDataString(t)));
}
