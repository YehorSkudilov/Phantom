namespace Phantom.Api.Services.Sources;

/// <summary>Builds magnet links for sources that only publish an info hash, and gives every magnet plenty of trackers.</summary>
public static class Magnets
{
    // Public trackers, so a magnet finds peers (and its metadata) quickly; DHT covers the rest. A source's own magnet
    // often names only one or two, some long gone, so these go on every magnet that's started (see WithTrackers).
    public static readonly string[] Trackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.tiny-vps.com:6969/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://open.tracker.cl:1337/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://tracker.theoks.net:6969/announce",
        "udp://tracker-udp.gbitt.info:80/announce",
        "udp://tracker.qu.ax:6969/announce",
        "udp://opentracker.io:6969/announce",
        "udp://tracker.0x7c0.com:6969/announce",
        "udp://retracker01-msk-virt.corbina.net:80/announce",
        "udp://bt.ktrackers.com:6666/announce",
        "udp://tracker.bittor.pw:1337/announce",
        "udp://isk.richardsw.club:6969/announce",
        "udp://ttk2.nbaonlineservice.com:6969/announce",
        "https://tracker.tamersunion.org:443/announce",
        "https://tracker.gbitt.info:443/announce",
        "http://tracker.renfei.net:8080/announce",
    ];

    public static string Build(string infoHash, string name) =>
        $"magnet:?xt=urn:btih:{infoHash.ToLowerInvariant()}&dn={Uri.EscapeDataString(name)}" +
        string.Concat(Trackers.Select(t => "&tr=" + Uri.EscapeDataString(t)));

    /// <summary><paramref name="magnet"/> with each of <see cref="Trackers"/> it doesn't name already.</summary>
    public static string WithTrackers(string magnet)
    {
        var present = magnet.Split(['?', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith("tr=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Uri.UnescapeDataString(p[3..]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return magnet + string.Concat(Trackers.Where(t => !present.Contains(t)).Select(t => "&tr=" + Uri.EscapeDataString(t)));
    }
}
