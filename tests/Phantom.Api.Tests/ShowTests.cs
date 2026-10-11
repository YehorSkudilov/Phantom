using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Api.Services.Sources;
using Phantom.Shared;

namespace Phantom.Api.Tests;

public class EpisodeParsingTests
{
    [Theory]
    [InlineData("The.Office.US.S02E05.1080p.WEB.h264-GRP", "The Office US", null, 2, 5, null, "1080p")]
    [InlineData("Severance S01E01-E02 720p", "Severance", null, 1, 1, 2, "720p")]
    [InlineData("Doctor.Who.2005.S10E03.HDTV.x264", "Doctor Who", 2005, 10, 3, null, null)]
    [InlineData("Taskmaster 1x04 Episode Name", "Taskmaster", null, 1, 4, null, null)]
    [InlineData("Breaking Bad Season 2 Complete 1080p BluRay", "Breaking Bad", null, 2, null, null, "1080p")]
    [InlineData("Fargo.S03.720p.WEB", "Fargo", null, 3, null, null, "720p")]
    [InlineData("The Wire S01-S05 Complete Series", "The Wire", null, null, null, null, null)]
    [InlineData("Seinfeld Complete Series DVDRip", "Seinfeld", null, null, null, null, null)]
    public void Parses_tv_release_names(string name, string title, int? year, int? season, int? episode, int? last, string? quality)
    {
        var parsed = ReleaseNameParser.ParseEpisode(name);
        Assert.NotNull(parsed);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(last, parsed.LastEpisode);
        Assert.Equal(quality, parsed.Quality);
    }

    [Theory]
    [InlineData("The Office US", "The Office")]
    [InlineData("Doctor Who 2005", "Doctor Who")]
    [InlineData("Shameless (UK)", "Shameless")]
    [InlineData("Severance", null)]
    public void Metadata_lookups_also_try_the_name_without_a_country_or_year(string title, string? shorter) =>
        Assert.Equal(shorter is null ? [title] : [title, shorter], Phantom.Api.Services.Metadata.TvMazeClient.TitleVariants(title));

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GROUP")]
    [InlineData("S01E02")] // no show name
    [InlineData("1920x1080 Wallpaper Pack")]
    public void Movies_and_nameless_episodes_are_not_tv(string name) => Assert.Null(ReleaseNameParser.ParseEpisode(name));

    [Theory]
    [InlineData("Show.S02E05.720p.mkv", null, 2, 5)]
    [InlineData("Season 2/05 - The Title.mkv", null, 2, 5)]
    [InlineData("Show S02/Show - E07.mkv", null, 2, 7)]
    [InlineData("Episode 3.mkv", 4, 4, 3)]
    [InlineData("Show/Extras/Making Of.mkv", 1, null, null)]
    public void Finds_the_episode_of_a_file_in_a_pack(string path, int? packSeason, int? season, int? episode) =>
        Assert.Equal((season, episode), ReleaseNameParser.EpisodeOfFile(path, packSeason));
}

public class TvSourceTests
{
    [Fact]
    public void Null_and_text_numbers_from_sources_dont_fail_the_sync()
    {
        // "The requested operation requires an element of type 'Number', but the target element has type 'Null'."
        var (knaben, _, _) = KnabenSource.Parse("""
            {"total":{"value":null},"hits":[
              {"title":"Some Show S01E02 1080p","hash":"BBCDEF0123456789ABCDEF0123456789ABCDEF01","categoryId":[2000000,null],
               "seeders":null,"peers":null,"bytes":null}
            ]}
            """);
        Assert.Equal(0, Assert.Single(knaben).Seeders);

        var (yts, _) = YtsSource.Parse("""
            {"status":"ok","data":{"movie_count":null,"movies":[{"title":"Old Film","year":null,"runtime":null,
              "torrents":[{"hash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01","quality":"720p","seeds":"12","peers":null,"size_bytes":"3000000000"}]}]}}
            """);
        var film = Assert.Single(yts);
        Assert.Equal((null, 12, 3_000_000_000L), (film.Year, film.Seeders, film.SizeBytes));
    }

    [Fact]
    public void Knaben_takes_episodes_and_packs_from_its_tv_category()
    {
        var (releases, _, _) = KnabenSource.Parse("""
            {"total":{"value":2},"hits":[
              {"title":"Some Show S01E02 1080p WEB","hash":"BBCDEF0123456789ABCDEF0123456789ABCDEF01","categoryId":[2000000],"seeders":5,"peers":7},
              {"title":"Some Show Season 3 720p","hash":"CBCDEF0123456789ABCDEF0123456789ABCDEF01","categoryId":[2001000,2000000],"seeders":2}
            ]}
            """);
        Assert.Equal(2, releases.Count);
        Assert.All(releases, r => Assert.True(r.IsShow));
        Assert.Equal(("Some Show", 1, 2), (releases[0].Title, releases[0].Season, releases[0].Episode));
        Assert.Equal((3, (int?)null), (releases[1].Season, releases[1].Episode));
    }

    [Fact]
    public void Pirate_bay_tv_categories_become_show_releases()
    {
        var releases = PirateBaySource.Parse("""
            [{"name":"Some.Show.S04E10.1080p.WEB.h264","info_hash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01","category":"208","seeders":"9","imdb":"tt1234567"},
             {"name":"The Matrix (1999) 1080p","info_hash":"BBCDEF0123456789ABCDEF0123456789ABCDEF01","category":"207","seeders":"9"}]
            """);
        var show = Assert.Single(releases, r => r.IsShow);
        Assert.Equal(("Some Show", 4, 10, "tt1234567"), (show.Title, show.Season, show.Episode, show.ImdbId));
        Assert.False(Assert.Single(releases, r => !r.IsShow).IsShow);
    }

    [Fact]
    public void Eztv_reads_episode_numbers_and_imdb_ids()
    {
        var (releases, count) = EztvSource.Parse("""
            {"torrents_count":2,"torrents":[
              {"hash":"abcdef0123456789abcdef0123456789abcdef01","title":"Some Show S03E07 1080p WEB H264-GRP EZTV","imdb_id":"1234567",
               "season":"3","episode":"7","seeds":40,"peers":5,"size_bytes":"1500000000","magnet_url":"magnet:?xt=urn:btih:abc"},
              {"hash":"bbcdef0123456789abcdef0123456789abcdef01","title":"Not a show at all","season":"0","episode":"0"}
            ]}
            """);
        Assert.Equal(2, count);
        var r = Assert.Single(releases);
        Assert.Equal(("Some Show", 3, 7, "tt1234567", 1_500_000_000L, "1080p"), (r.Title, r.Season, r.Episode, r.ImdbId, r.SizeBytes, r.Quality));
        Assert.True(r.IsShow);
    }
}

public class ShowApiTests : IClassFixture<PhantomFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly PhantomFactory _factory;

    public ShowApiTests(PhantomFactory factory)
    {
        _factory = factory;
        Seed();
    }

    // A show with two seasons: season 1 has a pack, season 2 only its first episode, so 2x2 can't be played.
    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        if (db.Shows.Any()) return;
        db.Shows.Add(new Show
        {
            Title = "Test Show",
            SortTitle = "test show",
            Year = 2020,
            ExternalKey = "testshow",
            Rating = 8.5,
            Genres = [new Genre { Name = "Drama" }],
            Credits = [new Credit { Name = "Jane Actor", Kind = CreditKind.Cast }],
            Episodes =
            [
                new Episode { SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot" },
                new Episode { SeasonNumber = 1, EpisodeNumber = 2 },
                new Episode { SeasonNumber = 2, EpisodeNumber = 1 },
                new Episode { SeasonNumber = 2, EpisodeNumber = 2 },
            ],
            Releases =
            [
                new Release { Name = "Test.Show.S01.1080p", InfoHash = new string('e', 40), SeasonNumber = 1, Quality = "1080p", Seeders = 4 },
                new Release { Name = "Test.Show.S02E01.720p", InfoHash = new string('f', 40), SeasonNumber = 2, EpisodeNumber = 1, Quality = "720p", Seeders = 4 },
            ],
        });
        db.SaveChanges();
    }

    private async Task<HttpClient> ClientAsync()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("owner", "password1"));
        if (res.StatusCode != HttpStatusCode.OK)
            res = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "password1"));
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    private async Task<int> ShowIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?kind=Show", Json))!.Items.Single().Id;

    [Fact]
    public async Task Library_lists_shows_separately_from_movies()
    {
        var client = await ClientAsync();
        var shows = await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?kind=Show", Json);
        var show = Assert.Single(shows!.Items);
        Assert.Equal((MediaKind.Show, "2 seasons"), (show.Kind, show.Caption));
        Assert.Empty((await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies", Json))!.Items);

        var filters = await client.GetFromJsonAsync<LibraryFiltersDto>("/api/library/filters?kind=Show", Json);
        Assert.Equal(["Drama"], filters!.Genres);
    }

    [Fact]
    public async Task Show_page_has_seasons_with_what_can_be_played()
    {
        var client = await ClientAsync();
        var detail = await client.GetFromJsonAsync<ShowDetailDto>($"/api/shows/{await ShowIdAsync(client)}", Json);
        Assert.Equal([(1, 2, 2), (2, 2, 1)], detail!.Seasons.Select(s => (s.Number, s.EpisodeCount, s.AvailableCount)));

        var season2 = await client.GetFromJsonAsync<List<EpisodeDto>>($"/api/shows/{detail.Id}/seasons/2", Json);
        Assert.Equal([true, false], season2!.Select(e => e.Available));
    }

    [Fact]
    public async Task Next_episode_follows_what_was_watched()
    {
        var client = await ClientAsync();
        var showId = await ShowIdAsync(client);
        var season1 = (await client.GetFromJsonAsync<List<EpisodeDto>>($"/api/shows/{showId}/seasons/1", Json))!;

        await client.PostAsync($"/api/episodes/{season1[0].Id}/unwatched", null);
        await client.PostAsync($"/api/episodes/{season1[1].Id}/unwatched", null);
        Assert.Equal("S01E01", (await client.GetFromJsonAsync<ShowDetailDto>($"/api/shows/{showId}", Json))!.NextEpisode!.Code);

        // Part-way through 1x2: that's what to carry on with, and the show is on Continue Watching.
        await client.PostAsJsonAsync("/api/playback/timeline",
            new TimelineUpdate(0, null, 60_000, 600_000, PlayerState.Paused, "test", season1[1].Id), Json);
        Assert.Equal("S01E02", (await client.GetFromJsonAsync<ShowDetailDto>($"/api/shows/{showId}", Json))!.NextEpisode!.Code);
        var hubs = await client.GetFromJsonAsync<List<HubDto>>("/api/library/hubs", Json);
        var item = hubs!.Single(h => h.Key == "continue").Items.Single();
        Assert.Equal((MediaKind.Show, "S1 · E2", 10.0), (item.Kind, item.Caption, item.ProgressPercent));

        // Finished it (90%): next is 2x1, from its own release.
        await client.PostAsJsonAsync("/api/playback/timeline",
            new TimelineUpdate(0, null, 590_000, 600_000, PlayerState.Stopped, "test", season1[1].Id), Json);
        var next = (await client.GetFromJsonAsync<ShowDetailDto>($"/api/shows/{showId}", Json))!.NextEpisode!;
        Assert.Equal("S02E01", next.Code);

        var episode = await client.GetFromJsonAsync<EpisodeDetailDto>($"/api/episodes/{season1[0].Id}", Json);
        Assert.Equal(("Test Show", "S01E02"), (episode!.ShowTitle, episode.Next!.Code));
        Assert.Equal("Season 1 pack", Assert.Single(episode.Releases).Covers);
    }

    [Fact]
    public async Task Search_and_watchlist_include_shows()
    {
        var client = await ClientAsync();
        var showId = await ShowIdAsync(client);

        var search = await client.GetFromJsonAsync<SearchResultDto>("/api/library/search?q=test", Json);
        Assert.Contains(search!.Movies, i => i.Kind == MediaKind.Show && i.Id == showId);
        Assert.Contains("Jane Actor", (await client.GetFromJsonAsync<SearchResultDto>("/api/library/search?q=jane", Json))!.People);

        await client.PutAsync($"/api/shows/{showId}/watchlist", null);
        Assert.Contains(await client.GetFromJsonAsync<List<MovieSummaryDto>>("/api/watchlist", Json) ?? [], i => i.Kind == MediaKind.Show);
        await client.DeleteAsync($"/api/shows/{showId}/watchlist");
        Assert.Empty(await client.GetFromJsonAsync<List<MovieSummaryDto>>("/api/watchlist", Json) ?? []);
    }

    [Fact]
    public async Task Unplayable_episodes_have_no_stream()
    {
        var client = await ClientAsync();
        var season2 = (await client.GetFromJsonAsync<List<EpisodeDto>>($"/api/shows/{await ShowIdAsync(client)}/seasons/2", Json))!;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/stream/episode/{season2[1].Id}/status")).StatusCode);
    }
}

public class ShowImportTests : IClassFixture<PhantomFactory>
{
    private readonly PhantomFactory _factory;

    public ShowImportTests(PhantomFactory factory) => _factory = factory;

    [Fact]
    public async Task Episodes_of_one_show_land_together_and_list_their_episodes()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var importer = scope.ServiceProvider.GetRequiredService<LibraryImporter>();
        var source = new Source { Name = "Test", Type = SourceType.Eztv };
        db.Sources.Add(source);
        await db.SaveChangesAsync();

        SourceRelease Episode(string hash, int season, int? episode, string? imdb = null) => new()
        {
            Title = "Imported Show", ReleaseName = $"Imported.Show.S{season:00}E{episode:00}", InfoHash = new string(hash[0], 40),
            Seeders = 5, IsShow = true, Season = season, Episode = episode, ImdbId = imdb,
        };
        var result = await importer.ImportAsync(source, [Episode("a", 1, 1, "tt7654321"), Episode("b", 1, 2), Episode("c", 2, null)], CancellationToken.None);

        Assert.Equal(1, result.NewShows);
        var show = await db.Shows.Include(s => s.Episodes).Include(s => s.Releases).SingleAsync(s => s.Title == "Imported Show");
        Assert.Equal("tt7654321", show.ImdbId);
        Assert.Equal([(1, 1), (1, 2)], show.Episodes.OrderBy(e => e.EpisodeNumber).Select(e => (e.SeasonNumber, e.EpisodeNumber)));
        Assert.Equal(3, show.Releases.Count);
        Assert.Contains(show.Releases, r => r is { SeasonNumber: 2, EpisodeNumber: null });
    }
}

public class LegacyDatabaseTests
{
    [Fact]
    public void A_database_made_before_migrations_is_upgraded_in_place()
    {
        // A database as EnsureCreated made it, from the first migration's model, with a movie in it.
        var dir = Directory.CreateTempSubdirectory("phantom-legacy-").FullName;
        var options = new DbContextOptionsBuilder<PhantomDbContext>().UseSqlite($"Data Source={Path.Combine(dir, "phantom.db")}").Options;
        using (var legacy = new PhantomDbContext(options))
        {
            legacy.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .Migrate(legacy.Database.GetMigrations().First());
            legacy.Database.ExecuteSqlRaw("""DROP TABLE "__EFMigrationsHistory" """);
            legacy.Database.ExecuteSqlRaw("""INSERT INTO "Movies" ("Title", "SortTitle", "AddedAt") VALUES ('Old Movie', 'old movie', '2020-01-01')""");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var factory = new PhantomFactory { DataDir = dir };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        Assert.Equal("Old Movie", db.Movies.Single().Title);
        Assert.Empty(db.Shows);
        Assert.Empty(db.Database.GetPendingMigrations());
    }
}
