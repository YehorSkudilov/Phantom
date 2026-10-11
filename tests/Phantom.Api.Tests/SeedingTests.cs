using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Tests;

public class SeedingTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "phantom-seed-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Starts the API on <see cref="_dataDir"/> (seeding runs at startup) and returns its source types.</summary>
    private List<SourceType> StartAndListSources(Action<PhantomDbContext>? change = null)
    {
        using var factory = new SeedingFactory(_dataDir);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        if (change is not null)
        {
            change(db);
            db.SaveChanges();
        }
        return db.Sources.Select(s => s.Type).OrderBy(t => t).ToList();
    }

    [Fact]
    public void New_server_gets_every_built_in_source()
    {
        Assert.Equal([SourceType.InternetArchive, SourceType.Yts, SourceType.PirateBay, SourceType.Knaben, SourceType.Eztv], StartAndListSources());
    }

    [Fact]
    public void A_missing_built_in_source_comes_back_on_restart_without_duplicates()
    {
        StartAndListSources(db => db.Sources.RemoveRange(db.Sources.Where(s => s.Type == SourceType.PirateBay)));
        Assert.Equal([SourceType.InternetArchive, SourceType.Yts, SourceType.PirateBay, SourceType.Knaben, SourceType.Eztv], StartAndListSources());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private sealed class SeedingFactory(string dataDir) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Phantom:DataDirectory", dataDir);
            builder.UseSetting("Phantom:SeedDefaultSources", "true");
            builder.UseSetting("Phantom:TorrentListenPort", Random.Shared.Next(40000, 50000).ToString());
            builder.UseSetting("Phantom:FfmpegPath", "/nonexistent/ffmpeg");
            builder.UseSetting("SwaggerAuth:Username", "test");
            builder.UseSetting("SwaggerAuth:Password", "test");
        }
    }
}
