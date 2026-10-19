using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Phantom.Services.Casting;

/// <summary>A Chromecast, or a TV with one built in (Google TV, Android TV), found on the network.</summary>
public sealed record CastDevice(string Id, string Name, string Host, int Port);

public enum CastPlayerState { Idle, Loading, Buffering, Playing, Paused }

/// <summary>The TV said no: the video couldn't be loaded or played there.</summary>
public sealed class CastException(string message) : Exception(message);

/// <summary>
/// A connection to a cast device, playing a video in Google's default media receiver: the TV fetches the video itself
/// (the stream URLs carry their own sign-in), and this device sends it play, pause, seek and volume, and hears back where
/// it's got to. The Cast protocol is small enough to speak directly (length-prefixed protobuf messages carrying JSON,
/// over TLS on port 8009), which works the same on every platform without Google's SDK.
/// </summary>
public sealed class CastSession : IAsyncDisposable
{
    private const string DefaultReceiver = "CC1AD845";
    private const string ConnectionNs = "urn:x-cast:com.google.cast.tp.connection";
    private const string HeartbeatNs = "urn:x-cast:com.google.cast.tp.heartbeat";
    private const string ReceiverNs = "urn:x-cast:com.google.cast.receiver";
    private const string MediaNs = "urn:x-cast:com.google.cast.media";
    private const string SenderId = "sender-0";
    private const string ReceiverId = "receiver-0";

    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private int _requestId;
    private string? _transportId, _appSessionId;
    private int? _mediaSessionId;
    private TimeSpan _position;
    private DateTime _positionAt = DateTime.UtcNow;
    private double _rate = 1;
    private DateTime _lastHeard = DateTime.UtcNow;
    private bool _ended, _disconnected;

    public CastDevice Device { get; }
    public CastPlayerState State { get; private set; } = CastPlayerState.Idle;
    public TimeSpan Duration { get; private set; }
    public double Volume { get; private set; } = 1;
    public bool Muted { get; private set; }

    /// <summary>Where the TV is in the video, moved on since its last word while it plays.</summary>
    public TimeSpan Position => State == CastPlayerState.Playing ? _position + (DateTime.UtcNow - _positionAt) * _rate : _position;

    /// <summary>
    /// The video stopped on the TV: "FINISHED" (it reached the end), "ERROR" (it couldn't be played), "CANCELLED" (stopped
    /// there, or from another phone). Raised once per video loaded, off the UI thread.
    /// </summary>
    public event EventHandler<string>? Ended;

    /// <summary>The connection is gone (the TV turned off, or left the network). Off the UI thread.</summary>
    public event EventHandler? Disconnected;

    private CastSession(CastDevice device, TcpClient tcp, SslStream ssl)
    {
        Device = device;
        _tcp = tcp;
        _ssl = ssl;
    }

    /// <summary>
    /// Connects to <paramref name="device"/> and opens the media player on it. Fails with a <see cref="CastException"/>
    /// saying which step the TV didn't get through, including when <paramref name="ct"/> runs out.
    /// </summary>
    public static async Task<CastSession> ConnectAsync(CastDevice device, CancellationToken ct)
    {
        var tcp = new TcpClient();
        SslStream? ssl = null;
        var step = "The TV didn't answer on the network. Make sure it's on, and on the same Wi-Fi as this device.";
        try
        {
            await tcp.ConnectAsync(device.Host, device.Port, ct);
            step = "The TV didn't accept a secure connection.";
            // Cast devices identify themselves with a certificate of their own, not one a browser would trust.
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = device.Host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, ct);
        }
        catch (Exception ex)
        {
            ssl?.Dispose();
            tcp.Dispose();
            // Release builds carry no exception texts of their own, only resource keys: say what didn't happen instead.
            if (ex is OperationCanceledException or SocketException or IOException or System.Security.Authentication.AuthenticationException)
                throw new CastException(step);
            throw;
        }

        var session = new CastSession(device, tcp, ssl);
        try
        {
            await session.StartAsync(ct);
            return session;
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            if (ex is OperationCanceledException or IOException or ObjectDisposedException)
                throw new CastException("The TV didn't open its video player. If it's asleep, wake it and try again.");
            throw;
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        _ = ReadLoopAsync();
        _ = HeartbeatLoopAsync();
        await SendAsync(ConnectionNs, ReceiverId, ConnectMessage());

        // The player may be open already (cast from here a moment ago, or from another phone): no need to open it again.
        var status = await RequestAsync(ReceiverNs, ReceiverId, new JsonObject { ["type"] = "GET_STATUS" }, ct);
        if (FindApp(status) is null)
        {
            // Opening the player takes a moment, and it may say so before it's ready: ask until it's there.
            status = await RequestAsync(ReceiverNs, ReceiverId, new JsonObject { ["type"] = "LAUNCH", ["appId"] = DefaultReceiver }, ct);
            for (var attempt = 0; FindApp(status) is null; attempt++)
            {
                if (attempt >= 20) throw new CastException("The TV didn't open its video player.");
                await Task.Delay(500, ct);
                status = await RequestAsync(ReceiverNs, ReceiverId, new JsonObject { ["type"] = "GET_STATUS" }, ct);
            }
        }
        var app = FindApp(status)!;
        _transportId = app["transportId"]?.GetValue<string>();
        _appSessionId = app["sessionId"]?.GetValue<string>();
        if (_transportId is null) throw new CastException("The TV didn't open its video player.");
        await SendAsync(ConnectionNs, _transportId, ConnectMessage());
    }

    /// <summary>
    /// Opens a virtual connection, introducing this app the way Google's own senders do: some TVs (Google TV among them)
    /// leave a bare CONNECT unanswered, and with it everything after.
    /// </summary>
    private static JsonObject ConnectMessage() => new()
    {
        ["type"] = "CONNECT",
        ["origin"] = new JsonObject(),
        ["userAgent"] = "Phantom",
        ["connType"] = 0,
        ["senderInfo"] = new JsonObject
        {
            ["sdkType"] = 2,
            ["version"] = "X",
            ["browserVersion"] = "X",
            ["platform"] = 4,
            ["connectionType"] = 1,
        },
    };

    private static JsonObject? FindApp(JsonObject message) =>
        (message["status"]?["applications"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["appId"]?.GetValue<string>() == DefaultReceiver);

    /// <summary>Plays <paramref name="url"/> on the TV from <paramref name="start"/>.</summary>
    public async Task LoadAsync(string url, string contentType, string title, string? image, TimeSpan start, CancellationToken ct = default)
    {
        var images = new JsonArray();
        if (image is { Length: > 0 }) images.Add(new JsonObject { ["url"] = image });
        var load = new JsonObject
        {
            ["type"] = "LOAD",
            ["autoplay"] = true,
            ["currentTime"] = start.TotalSeconds,
            ["media"] = new JsonObject
            {
                ["contentId"] = url,
                ["contentUrl"] = url,
                ["streamType"] = "BUFFERED",
                ["contentType"] = contentType,
                ["metadata"] = new JsonObject { ["metadataType"] = 0, ["title"] = title, ["images"] = images },
            },
        };
        _ended = false;
        State = CastPlayerState.Loading;
        SetPosition(start, 1);
        Duration = TimeSpan.Zero;
        // Starting the server's conversion can take a while before the TV hears anything back.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var reply = await RequestAsync(MediaNs, Transport, load, timeout.Token);
        if (reply["type"]?.GetValue<string>() is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST")
        {
            State = CastPlayerState.Idle;
            throw new CastException(reply["reason"]?.GetValue<string>() is { } reason ? $"The TV couldn't play it ({reason})." : "The TV couldn't play it.");
        }
    }

    public Task PlayAsync() => MediaCommandAsync(new JsonObject { ["type"] = "PLAY" });

    public Task PauseAsync() => MediaCommandAsync(new JsonObject { ["type"] = "PAUSE" });

    public Task SeekAsync(TimeSpan to)
    {
        SetPosition(to, _rate);
        return MediaCommandAsync(new JsonObject { ["type"] = "SEEK", ["currentTime"] = to.TotalSeconds });
    }

    /// <summary>The TV's own volume, 0 to 1.</summary>
    public Task SetVolumeAsync(double level)
    {
        Volume = Math.Clamp(level, 0, 1);
        return SendAsync(ReceiverNs, ReceiverId, new JsonObject
        {
            ["type"] = "SET_VOLUME",
            ["requestId"] = NextRequestId(),
            ["volume"] = new JsonObject { ["level"] = Volume },
        });
    }

    public Task SetMutedAsync(bool muted)
    {
        Muted = muted;
        return SendAsync(ReceiverNs, ReceiverId, new JsonObject
        {
            ["type"] = "SET_VOLUME",
            ["requestId"] = NextRequestId(),
            ["volume"] = new JsonObject { ["muted"] = muted },
        });
    }

    /// <summary>Stops the video and closes the player on the TV.</summary>
    public async Task StopAsync()
    {
        _ended = true;
        if (_mediaSessionId is not null) await MediaCommandAsync(new JsonObject { ["type"] = "STOP" });
        if (_appSessionId is not null)
            await SendAsync(ReceiverNs, ReceiverId, new JsonObject { ["type"] = "STOP", ["requestId"] = NextRequestId(), ["sessionId"] = _appSessionId });
    }

    private string Transport => _transportId ?? throw new CastException("Not connected to the TV's player.");

    private Task MediaCommandAsync(JsonObject command)
    {
        if (_mediaSessionId is not { } id || _transportId is null) return Task.CompletedTask;
        command["mediaSessionId"] = id;
        command["requestId"] = NextRequestId();
        return SendAsync(MediaNs, _transportId, command);
    }

    private int NextRequestId() => Interlocked.Increment(ref _requestId);

    private void SetPosition(TimeSpan position, double rate)
    {
        _position = position;
        _positionAt = DateTime.UtcNow;
        _rate = rate;
    }

    private async Task<JsonObject> RequestAsync(string ns, string destination, JsonObject payload, CancellationToken ct)
    {
        var id = NextRequestId();
        payload["requestId"] = id;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await SendAsync(ns, destination, payload);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            return await reply.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CastException(_life.IsCancellationRequested ? "The connection to the TV was lost." : "The TV didn't answer.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(string ns, string destination, JsonObject payload)
    {
        var frame = CastMessage.Encode(SenderId, destination, ns, payload.ToJsonString());
        await _write.WaitAsync(_life.Token);
        try
        {
            await _ssl.WriteAsync(frame, _life.Token);
            await _ssl.FlushAsync(_life.Token);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_life.IsCancellationRequested)
            {
                await _ssl.ReadExactlyAsync(header, _life.Token);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is <= 0 or > 1 << 20) throw new IOException("Malformed message from the TV.");
                var body = new byte[length];
                await _ssl.ReadExactlyAsync(body, _life.Token);
                _lastHeard = DateTime.UtcNow;
                if (CastMessage.Decode(body) is { } message) Handle(message);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cast: {ex.Message}");
        }
        LoseConnection();
    }

    /// <summary>Pings the TV, as it expects every few seconds; a TV silent for a while has gone.</summary>
    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                await Task.Delay(5000, _life.Token);
                if (DateTime.UtcNow - _lastHeard > TimeSpan.FromSeconds(20))
                {
                    LoseConnection();
                    return;
                }
                await SendAsync(HeartbeatNs, ReceiverId, new JsonObject { ["type"] = "PING" });
                if (_mediaSessionId is not null && _transportId is not null)
                    await SendAsync(MediaNs, _transportId, new JsonObject { ["type"] = "GET_STATUS", ["requestId"] = NextRequestId() });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            LoseConnection();
        }
    }

    private void LoseConnection()
    {
        if (_life.IsCancellationRequested || _disconnected) return;
        _disconnected = true;
        foreach (var pending in _pending.Values) pending.TrySetCanceled();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void Handle(CastMessage message)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(message.Payload) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        if (payload is null) return;
        var type = payload["type"]?.GetValue<string>();

        switch (message.Namespace)
        {
            case HeartbeatNs when type == "PING":
                _ = SendAsync(HeartbeatNs, message.Source, new JsonObject { ["type"] = "PONG" }).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
                break;
            case ConnectionNs when type == "CLOSE" && message.Source == _transportId:
                // The player on the TV was closed (from the TV, or another phone took it over).
                EndOnce("CANCELLED");
                break;
            case ReceiverNs when type == "RECEIVER_STATUS":
                if (payload["status"]?["volume"] is JsonObject volume)
                {
                    if (volume["level"] is JsonValue level) Volume = level.GetValue<double>();
                    if (volume["muted"] is JsonValue muted) Muted = muted.GetValue<bool>();
                }
                if (_transportId is not null && payload["status"] is JsonObject && FindApp(payload) is null) EndOnce("CANCELLED");
                break;
            case MediaNs when type == "MEDIA_STATUS":
                OnMediaStatus(payload);
                break;
        }

        if (payload["requestId"] is JsonValue requestId && requestId.TryGetValue<int>(out var id) && id != 0 && _pending.TryRemove(id, out var reply))
            reply.TrySetResult(payload);
    }

    private void OnMediaStatus(JsonObject payload)
    {
        if ((payload["status"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is not { } status) return;
        if (status["mediaSessionId"] is JsonValue session && session.TryGetValue<int>(out var sessionId)) _mediaSessionId = sessionId;
        if (status["media"]?["duration"] is JsonValue duration && duration.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && seconds > 0)
            Duration = TimeSpan.FromSeconds(seconds);
        var rate = status["playbackRate"] is JsonValue r && r.TryGetValue<double>(out var playbackRate) ? playbackRate : 1;
        if (status["currentTime"] is JsonValue time && time.TryGetValue<double>(out var at) && double.IsFinite(at))
            SetPosition(TimeSpan.FromSeconds(at), rate);

        var state = status["playerState"]?.GetValue<string>();
        State = state switch
        {
            "PLAYING" => CastPlayerState.Playing,
            "PAUSED" => CastPlayerState.Paused,
            "BUFFERING" => CastPlayerState.Buffering,
            "LOADING" => CastPlayerState.Loading,
            _ => CastPlayerState.Idle,
        };
        if (state == "IDLE" && status["idleReason"]?.GetValue<string>() is { } reason && reason != "INTERRUPTED")
            EndOnce(reason);
    }

    private void EndOnce(string reason)
    {
        if (_ended) return;
        _ended = true;
        State = CastPlayerState.Idle;
        Ended?.Invoke(this, reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_life.IsCancellationRequested) return;
        try
        {
            if (_transportId is not null) await SendAsync(ConnectionNs, _transportId, new JsonObject { ["type"] = "CLOSE" }).WaitAsync(TimeSpan.FromSeconds(2));
            await SendAsync(ConnectionNs, ReceiverId, new JsonObject { ["type"] = "CLOSE" }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cast close: {ex.Message}");
        }
        _life.Cancel();
        _ssl.Dispose();
        _tcp.Dispose();
    }
}

/// <summary>
/// One Cast message: the protocol's CastMessage protobuf (version, source, destination, namespace, a UTF-8 payload),
/// sent with its length in front. Only the string payload is used, so it's written and read by hand.
/// </summary>
internal sealed record CastMessage(string Source, string Destination, string Namespace, string Payload)
{
    public static byte[] Encode(string source, string destination, string ns, string payload)
    {
        using var body = new MemoryStream();
        WriteVarintField(body, 1, 0); // protocol_version CASTV2_1_0
        WriteStringField(body, 2, source);
        WriteStringField(body, 3, destination);
        WriteStringField(body, 4, ns);
        WriteVarintField(body, 5, 0); // payload_type STRING
        WriteStringField(body, 6, payload);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, (int)body.Length);
        body.ToArray().CopyTo(frame, 4);
        return frame;
    }

    public static CastMessage? Decode(byte[] body)
    {
        string source = "", destination = "", ns = "", payload = "";
        var at = 0;
        while (at < body.Length)
        {
            var key = ReadVarint(body, ref at);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0:
                    ReadVarint(body, ref at);
                    break;
                case 2:
                    var length = (int)ReadVarint(body, ref at);
                    if (length < 0 || at + length > body.Length) return null;
                    var text = field is >= 2 and <= 4 or 6 ? Encoding.UTF8.GetString(body, at, length) : null;
                    switch (field)
                    {
                        case 2: source = text!; break;
                        case 3: destination = text!; break;
                        case 4: ns = text!; break;
                        case 6: payload = text!; break;
                    }
                    at += length;
                    break;
                default:
                    return null; // nothing else is used by the protocol
            }
        }
        return new CastMessage(source, destination, ns, payload);
    }

    private static void WriteVarintField(Stream to, int field, ulong value)
    {
        WriteVarint(to, (ulong)(field << 3));
        WriteVarint(to, value);
    }

    private static void WriteStringField(Stream to, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(to, (ulong)((field << 3) | 2));
        WriteVarint(to, (ulong)bytes.Length);
        to.Write(bytes);
    }

    private static void WriteVarint(Stream to, ulong value)
    {
        while (value >= 0x80)
        {
            to.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        to.WriteByte((byte)value);
    }

    private static ulong ReadVarint(byte[] from, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (at >= from.Length) throw new IOException("Malformed message from the TV.");
            var b = from[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return value;
        }
        throw new IOException("Malformed message from the TV.");
    }
}
