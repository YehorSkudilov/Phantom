using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Phantom.Services.Casting;

/// <summary>
/// Finds the cast devices on this network: they announce themselves over mDNS as "_googlecast._tcp". Each platform is
/// asked in its own way, as each lets apps see the local network differently: Apple's only through Bonjour (raw
/// multicast needs an entitlement Apple hands out on request), Windows' through its own DNS-SD (its firewall drops the
/// devices' replies to a socket of the app's), and Android's straight, holding a multicast lock while it listens.
/// </summary>
public static class CastDiscovery
{
    public const string ServiceType = "_googlecast._tcp";
    private const int DefaultPort = 8009;

    /// <summary>The devices that answered within <paramref name="window"/>, by name.</summary>
    public static async Task<IReadOnlyList<CastDevice>> FindAsync(TimeSpan window, CancellationToken ct = default)
    {
        IReadOnlyList<CastDevice> found;
#if IOS || MACCATALYST
        found = await MainThread.InvokeOnMainThreadAsync(() => BrowseBonjourAsync(window, ct));
#elif WINDOWS
        found = await BrowseWindowsAsync(window, ct);
#elif ANDROID
        var wifi = Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService) as Android.Net.Wifi.WifiManager;
        var multicast = wifi?.CreateMulticastLock("phantom-cast");
        multicast?.SetReferenceCounted(false);
        multicast?.Acquire();
        try
        {
            found = await BrowseMdnsAsync(window, ct);
        }
        finally
        {
            multicast?.Release();
        }
#else
        found = await BrowseMdnsAsync(window, ct);
#endif
        return [.. found.GroupBy(d => d.Id).Select(g => g.First()).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static CastDevice Device(string instance, string host, int port, IReadOnlyDictionary<string, string> txt)
    {
        // The instance name is the model and an id ("Chromecast-1a2b..."); "fn" is what the owner called it.
        var name = txt.TryGetValue("fn", out var fn) && fn.Length > 0 ? fn : instance.Split('.')[0];
        var id = txt.TryGetValue("id", out var tid) && tid.Length > 0 ? tid : instance;
        return new CastDevice(id, name, host, port > 0 ? port : DefaultPort);
    }

    // ---------- mDNS, directly (Android) ----------

    /// <summary>
    /// Asks "who's _googlecast._tcp.local?" from a socket on each network this device is on. Sent from a port other than
    /// mDNS's own, the devices answer that socket directly (RFC 6762's one-shot queries), so nothing has to listen on
    /// 5353 alongside the system's own responder.
    /// </summary>
    private static async Task<IReadOnlyList<CastDevice>> BrowseMdnsAsync(TimeSpan window, CancellationToken ct)
    {
        var devices = new Dictionary<string, CastDevice>();
        var query = Mdns.Query($"{ServiceType}.local");
        var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        var sockets = new List<UdpClient>();
        foreach (var local in LocalAddresses())
        {
            try
            {
                var socket = new UdpClient(new IPEndPoint(local, 0));
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                sockets.Add(socket);
            }
            catch (SocketException)
            {
                // A network that won't take multicast (mobile data, a VPN): the others may.
            }
        }
        if (sockets.Count == 0) sockets.Add(new UdpClient(AddressFamily.InterNetwork));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(window);
        try
        {
            var listening = sockets.Select(socket => ListenAsync(socket, devices, stop.Token)).ToList();
            // UDP gets lost: asked again a moment later.
            for (var attempt = 0; attempt < 3 && !stop.IsCancellationRequested; attempt++)
            {
                foreach (var socket in sockets)
                {
                    try
                    {
                        await socket.SendAsync(query, group, stop.Token);
                    }
                    catch (SocketException)
                    {
                    }
                }
                try
                {
                    await Task.Delay(800, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
            await Task.WhenAll(listening);
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }
        lock (devices) return [.. devices.Values];
    }

    private static async Task ListenAsync(UdpClient socket, Dictionary<string, CastDevice> devices, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await socket.ReceiveAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            // Each device answers for itself, so the address it answered from is the one to connect to.
            foreach (var (instance, port, txt) in Mdns.Services(packet.Buffer, $"{ServiceType}.local"))
            {
                var device = Device(instance, packet.RemoteEndPoint.Address.ToString(), port, txt);
                lock (devices) devices[device.Id] = device;
            }
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            foreach (var address in properties.UnicastAddresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                    yield return address.Address;
        }
    }

#if WINDOWS
    // ---------- Windows' DNS-SD ----------

    private static async Task<IReadOnlyList<CastDevice>> BrowseWindowsAsync(TimeSpan window, CancellationToken ct)
    {
        const string aqs = "System.Devices.AepService.ProtocolId:=\"{4526e8c1-8aac-4153-9b16-55e86ada0e54}\" AND " +
                           "System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"" + ServiceType + "\"";
        string[] properties =
        [
            "System.Devices.Dnssd.InstanceName", "System.Devices.Dnssd.HostName", "System.Devices.Dnssd.PortNumber",
            "System.Devices.Dnssd.TextAttributes", "System.Devices.IpAddress",
        ];
        var devices = new Dictionary<string, CastDevice>();
        var watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(aqs, properties,
            Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpointService);
        watcher.Added += (_, info) =>
        {
            var p = info.Properties;
            var instance = p.TryGetValue("System.Devices.Dnssd.InstanceName", out var i) && i is string s ? s : info.Name;
            var host = p.TryGetValue("System.Devices.IpAddress", out var ips) && ips is string[] { Length: > 0 } list
                ? list.FirstOrDefault(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) ?? list[0]
                : p.TryGetValue("System.Devices.Dnssd.HostName", out var h) && h is string hostName ? hostName : null;
            if (host is null) return;
            var port = p.TryGetValue("System.Devices.Dnssd.PortNumber", out var n) && n is ushort number ? number : DefaultPort;
            var txt = p.TryGetValue("System.Devices.Dnssd.TextAttributes", out var t) && t is string[] attributes
                ? Mdns.TxtPairs(attributes)
                : new Dictionary<string, string>();
            var device = Device(instance, host, port, txt);
            lock (devices) devices[device.Id] = device;
        };
        watcher.Start();
        try
        {
            await Task.Delay(window, ct);
        }
        finally
        {
            if (watcher.Status is Windows.Devices.Enumeration.DeviceWatcherStatus.Started or Windows.Devices.Enumeration.DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
        lock (devices) return [.. devices.Values];
    }
#endif

#if IOS || MACCATALYST
    // ---------- Bonjour (iOS, macOS) ----------

    private static async Task<IReadOnlyList<CastDevice>> BrowseBonjourAsync(TimeSpan window, CancellationToken ct)
    {
        var devices = new Dictionary<string, CastDevice>();
        var resolving = new List<Foundation.NSNetService>(); // kept alive until resolved
        using var browser = new Foundation.NSNetServiceBrowser();
        browser.FoundService += (_, e) =>
        {
            var service = e.Service;
            resolving.Add(service);
            service.AddressResolved += (_, _) =>
            {
                var txt = new Dictionary<string, string>();
                if (service.GetTxtRecordData() is { } data && Foundation.NSNetService.DictionaryFromTxtRecord(data) is { } record)
                    foreach (var key in record.Keys)
                        if (record[key] is Foundation.NSData value)
                            txt[key.ToString()] = Encoding.UTF8.GetString(value.ToArray());
                var host = service.Addresses?.Select(IPv4).FirstOrDefault(a => a is not null) ?? service.HostName;
                if (host is null) return;
                var device = Device(service.Name, host, (int)service.Port, txt);
                devices[device.Id] = device;
            };
            service.Resolve(Math.Max(1, window.TotalSeconds));
        };
        browser.SearchForServices($"{ServiceType}.", "local.");
        try
        {
            await Task.Delay(window, ct);
        }
        finally
        {
            browser.Stop();
            foreach (var service in resolving) service.Stop();
        }
        return [.. devices.Values];
    }

    /// <summary>The IPv4 address in a resolved sockaddr, if that's what it is.</summary>
    private static string? IPv4(Foundation.NSData sockaddr)
    {
        var bytes = sockaddr.ToArray();
        // sockaddr_in: length, family (AF_INET = 2), port, then the address.
        return bytes.Length >= 8 && bytes[1] == 2 ? new IPAddress(bytes.AsSpan(4, 4)).ToString() : null;
    }
#endif
}

/// <summary>Just enough DNS to ask for a service over mDNS and read the answers.</summary>
internal static class Mdns
{
    private const int Ptr = 12, Txt = 16, Srv = 33;

    /// <summary>A PTR question for <paramref name="service"/>, asking for the answer to be sent straight back.</summary>
    public static byte[] Query(string service)
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in service.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange([0, 0, Ptr, 0x80, 1]); // end of name, type PTR, class IN with the unicast-response bit
        return [.. bytes];
    }

    /// <summary>The instances of <paramref name="service"/> a response tells of, with their port and TXT record.</summary>
    public static List<(string Instance, int Port, Dictionary<string, string> Txt)> Services(byte[] packet, string service)
    {
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var txts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var suffix = "." + service;
        try
        {
            if (packet.Length < 12 || (packet[2] & 0x80) == 0) return []; // a question, not an answer
            var questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
            var records = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8))
                          + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));
            var at = 12;
            for (var q = 0; q < questions; q++)
            {
                ReadName(packet, ref at);
                at += 4;
            }
            for (var r = 0; r < records; r++)
            {
                var name = ReadName(packet, ref at);
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at));
                var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at + 8));
                var data = at + 10;
                if (data + length > packet.Length) break;
                switch (type)
                {
                    case Ptr when name.Equals(service, StringComparison.OrdinalIgnoreCase):
                        var target = data;
                        instances.Add(ReadName(packet, ref target));
                        break;
                    case Srv when name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && length >= 6:
                        instances.Add(name);
                        ports[name] = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(data + 4));
                        break;
                    case Txt when name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase):
                        instances.Add(name);
                        var strings = new List<string>();
                        for (var i = data; i < data + length && i + 1 + packet[i] <= data + length; i += 1 + packet[i])
                            strings.Add(Encoding.UTF8.GetString(packet, i + 1, packet[i]));
                        txts[name] = TxtPairs(strings);
                        break;
                }
                at = data + length;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
        {
            // A truncated or odd packet: what was read so far still counts.
        }
        return [.. instances.Where(i => i.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(i => (i, ports.GetValueOrDefault(i), txts.GetValueOrDefault(i) ?? []))];
    }

    /// <summary>"key=value" strings as a dictionary.</summary>
    public static Dictionary<string, string> TxtPairs(IEnumerable<string> strings)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in strings)
        {
            var eq = s.IndexOf('=');
            if (eq > 0) pairs[s[..eq]] = s[(eq + 1)..];
        }
        return pairs;
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var at = offset;
        var jumped = false;
        for (var hops = 0; ; )
        {
            int length = packet[at];
            if (length == 0)
            {
                at++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (++hops > 32) throw new FormatException("DNS name loops");
                if (!jumped) offset = at + 2;
                jumped = true;
                at = ((length & 0x3F) << 8) | packet[at + 1];
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(packet, at + 1, length));
            at += 1 + length;
        }
        if (!jumped) offset = at;
        return string.Join('.', labels);
    }
}
