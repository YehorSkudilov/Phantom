using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phantom.Api;
using Phantom.Api.Services;

namespace Phantom.Api.Tests;

public class TranscoderTests
{
    [Fact]
    public async Task A_failed_conversion_says_so_before_writing_anything()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh")) return;
        // Stands in for an ffmpeg that can't read its input: an error line, no output.
        var fake = Path.Combine(Path.GetTempPath(), $"fake-ffmpeg-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(fake, "#!/bin/sh\necho 'http://127.0.0.1/x: Server returned 404 Not Found' >&2\nexit 1\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var transcoder = new Transcoder(Options.Create(new PhantomOptions { FfmpegPath = fake }), NullLogger<Transcoder>.Instance);
            using var output = new MemoryStream();
            var ex = await Assert.ThrowsAsync<TranscodeFailedException>(() =>
                transcoder.TranscodeAsync("http://127.0.0.1/x?access_token=secret", 0, null, null, output, CancellationToken.None));
            Assert.Contains("404 Not Found", ex.Message);
            Assert.Equal(0, output.Length);
        }
        finally
        {
            File.Delete(fake);
        }
    }
}
