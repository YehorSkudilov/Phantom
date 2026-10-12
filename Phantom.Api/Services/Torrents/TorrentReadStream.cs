namespace Phantom.Api.Services.Torrents;

/// <summary>
/// MonoTorrent allows a single active stream per torrent, but players open several HTTP
/// connections at once (probing the end of an MP4 for its index, seeking, ...). Each request
/// gets one of these lightweight views with its own position; reads are serialised onto the
/// shared torrent stream, and each seek re-points the streaming piece picker.
/// </summary>
public sealed class SharedTorrentStream(Stream inner, string filePath, Action? onRead = null) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The file in the torrent this streams.</summary>
    public string FilePath => filePath;

    public long Length => inner.Length;

    public async ValueTask<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        // Every read counts as being watched: one long request can feed a player for the whole film.
        onRead?.Invoke();
        await _lock.WaitAsync(ct);
        try
        {
            if (inner.Position != position) inner.Seek(position, SeekOrigin.Begin);
            return await inner.ReadAsync(buffer, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        inner.Dispose();
        _lock.Dispose();
    }
}

/// <param name="downloaded">
/// For a reader that mustn't wait on the swarm (a seek preview): whether a range is downloaded already. A read of one
/// that isn't fails at once instead of holding the shared stream (everyone else's reads wait behind it) and pointing
/// the download at its part of the file, away from what's being watched.
/// </param>
public sealed class TorrentReadStream(SharedTorrentStream shared, Func<long, int, bool>? downloaded = null) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => shared.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= Length) return 0;
        if (downloaded is not null && !downloaded(_position, buffer.Length))
            throw new IOException("That part of the video isn't downloaded yet.");
        var read = await shared.ReadAtAsync(_position, buffer, cancellationToken);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
