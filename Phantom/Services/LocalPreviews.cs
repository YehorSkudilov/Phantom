namespace Phantom.Services;

/// <summary>
/// Seek previews for a video on this device (a download played offline), taken from the file itself: there's no server
/// to make them. Android only for now; elsewhere offline videos have none.
/// </summary>
public static class LocalPreviews
{
#if ANDROID
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Android.Media.MediaMetadataRetriever? _retriever;
    private static string? _path;

    // Scaled frames came in Android 8.1.
    public static bool IsSupported => OperatingSystem.IsAndroidVersionAtLeast(27);

    /// <summary>The frame at the keyframe nearest <paramref name="seconds"/>, as a JPEG <paramref name="width"/> wide.</summary>
    public static async Task<byte[]?> FrameAsync(string path, int seconds, int width, CancellationToken ct)
    {
        if (!IsSupported) return null;
        await Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                if (_path != path || _retriever is null)
                {
                    _retriever?.Release();
                    _retriever = new Android.Media.MediaMetadataRetriever();
                    _path = null;
                    _retriever.SetDataSource(path);
                    _path = path;
                }
#pragma warning disable CA1416 // checked by IsSupported
                using var bitmap = _retriever.GetScaledFrameAtTime(seconds * 1_000_000L, Android.Media.Option.ClosestSync, width, width);
#pragma warning restore CA1416
                if (bitmap is null) return null;
                using var stream = new MemoryStream();
                bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 75, stream);
                return stream.ToArray();
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Local preview: {ex.Message}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Lets go of the file (the player closed).</summary>
    public static void Close()
    {
        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync();
            try
            {
                _retriever?.Release();
                _retriever = null;
                _path = null;
            }
            finally
            {
                Gate.Release();
            }
        });
    }
#else
    public static bool IsSupported => false;

    public static Task<byte[]?> FrameAsync(string path, int seconds, int width, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public static void Close() { }
#endif
}
