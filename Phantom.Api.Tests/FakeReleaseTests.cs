using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Api.Data;
using Phantom.Api.Services.Sources;
using Phantom.Api.Services.Torrents;
using Phantom.Shared;

namespace Phantom.Api.Tests;

public class FakeReleaseTests : IClassFixture<PhantomFactory>
{
    private readonly PhantomFactory _factory;

    public FakeReleaseTests(PhantomFactory factory) => _factory = factory;

    /// <summary>A release whose download found no video in it leaves the library, and a source can't bring it back.</summary>
    [Fact]
    public async Task A_fake_release_is_removed_and_stays_out()
    {
        var hash = new string('f', 40);
        int movieId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var movie = new Movie
            {
                Title = "Fake Film", SortTitle = "fake film", ExternalKey = "fakefilm",
                Releases = [new Release { Name = "Fake.Film.2026.1080p", InfoHash = hash }],
            };
            db.Movies.Add(movie);
            await db.SaveChangesAsync();
            movieId = movie.Id;
            db.ServerDownloads.Add(new ServerDownload
            {
                MovieId = movieId, ReleaseId = movie.Releases.First().Id, Status = DownloadStatus.Failed, Error = ServerDownloadService.NoVideoMessage,
            });
            await db.SaveChangesAsync();
        }

        // The download queue ticks every 2 s.
        for (var i = 0; i < 50; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            if (!await db.Releases.AnyAsync(r => r.InfoHash == hash)) break;
            await Task.Delay(200);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            Assert.False(await db.Releases.AnyAsync(r => r.InfoHash == hash));
            Assert.False(await db.ServerDownloads.AnyAsync(d => d.MovieId == movieId));
            Assert.True(await db.BlockedReleases.AnyAsync(b => b.InfoHash == hash));

            var source = new Source { Name = "Test", Type = SourceType.Yts };
            db.Sources.Add(source);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<LibraryImporter>().ImportAsync(source,
                [new SourceRelease { Title = "Fake Film", ReleaseName = "Fake.Film.2026.1080p", InfoHash = hash.ToUpperInvariant(), Seeders = 100 }],
                CancellationToken.None);
            Assert.False(await db.Releases.AnyAsync(r => r.InfoHash == hash || r.InfoHash == hash.ToUpperInvariant()));
        }
    }
}
