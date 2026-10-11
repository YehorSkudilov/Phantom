using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Shared;

namespace Phantom.Api.Tests;

public class ReleaseCoverageTests
{
    /// <summary>The hash lookups give the same answers as the release-by-release rule they replace.</summary>
    [Fact]
    public void Matches_the_release_by_release_rule()
    {
        var releases = new[]
        {
            new Release { Name = "pack", InfoHash = "a", ShowId = 1, SeasonNumber = 2 },
            new Release { Name = "episode", InfoHash = "b", ShowId = 1, SeasonNumber = 3, EpisodeNumber = 4 },
        };
        var coverage = new ReleaseCoverage(releases);
        for (var season = 0; season <= 4; season++)
            for (var episode = 1; episode <= 5; episode++)
            {
                var e = new Episode { SeasonNumber = season, EpisodeNumber = episode };
                Assert.Equal(releases.Any(r => ShowLibraryService.Covers(r, e)), coverage.Covers(e));
            }

        // A release without a season (the whole series) covers everything, specials included.
        var wholeShow = new ReleaseCoverage([new Release { Name = "all", InfoHash = "c", ShowId = 1 }]);
        Assert.True(wholeShow.Covers(new Episode { SeasonNumber = 0, EpisodeNumber = 9 }));
        Assert.False(new ReleaseCoverage(Array.Empty<Release>()).Covers(new Episode { SeasonNumber = 1, EpisodeNumber = 1 }));
    }
}

/// <summary>The batched show queries (rows, New On TV, history) and compressed replies. Own database, so the other
/// show tests' watch state doesn't leak in.</summary>
public class ShowRowTests : IClassFixture<PhantomFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly PhantomFactory _factory;

    public ShowRowTests(PhantomFactory factory)
    {
        _factory = factory;
        Seed();
    }

    // "Old Show": season 1 as a pack, 2x1 on its own, 2x2 not found. "New Show": one episode, released later.
    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        if (db.Shows.Any()) return;
        db.Shows.Add(new Show
        {
            Title = "Old Show", SortTitle = "old show", ExternalKey = "oldshow", Rating = 7,
            Episodes =
            [
                new Episode { SeasonNumber = 1, EpisodeNumber = 1 },
                new Episode { SeasonNumber = 1, EpisodeNumber = 2 },
                new Episode { SeasonNumber = 2, EpisodeNumber = 1 },
                new Episode { SeasonNumber = 2, EpisodeNumber = 2 },
            ],
            Releases =
            [
                new Release { Name = "Old.Show.S01", InfoHash = new string('1', 40), SeasonNumber = 1, AddedAt = DateTime.UtcNow.AddDays(-10) },
                new Release { Name = "Old.Show.S02E01", InfoHash = new string('2', 40), SeasonNumber = 2, EpisodeNumber = 1, AddedAt = DateTime.UtcNow.AddDays(-9) },
            ],
        });
        db.Shows.Add(new Show
        {
            Title = "New Show", SortTitle = "new show", ExternalKey = "newshow", Rating = 6,
            Episodes = [new Episode { SeasonNumber = 1, EpisodeNumber = 1 }],
            Releases = [new Release { Name = "New.Show.S01E01", InfoHash = new string('3', 40), SeasonNumber = 1, EpisodeNumber = 1, AddedAt = DateTime.UtcNow }],
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

    private static async Task<MovieSummaryDto> RowAsync(HttpClient client, string title) =>
        (await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?kind=Show", Json))!.Items.Single(i => i.Title == title);

    [Fact]
    public async Task Rows_count_progress_over_the_episodes_that_can_be_played()
    {
        var client = await ClientAsync();
        var old = await RowAsync(client, "Old Show");
        var detail = (await client.GetFromJsonAsync<ShowDetailDto>($"/api/shows/{old.Id}", Json))!;
        var season1 = (await client.GetFromJsonAsync<List<EpisodeDto>>($"/api/shows/{old.Id}/seasons/1", Json))!;
        var season2 = (await client.GetFromJsonAsync<List<EpisodeDto>>($"/api/shows/{old.Id}/seasons/2", Json))!;
        Assert.Equal([(1, 2, 2), (2, 2, 1)], detail.Seasons.Select(s => (s.Number, s.EpisodeCount, s.AvailableCount)));

        // 1x1 and the unplayable 2x2 watched: 1 of the 3 playable episodes.
        await client.PostAsync($"/api/episodes/{season1[0].Id}/watched", null);
        await client.PostAsync($"/api/episodes/{season2[1].Id}/watched", null);
        old = await RowAsync(client, "Old Show");
        Assert.Equal((33.3, false, "2 seasons"), (old.ProgressPercent, old.Watched, old.Caption));

        await client.PostAsync($"/api/shows/{old.Id}/watched", null);
        old = await RowAsync(client, "Old Show");
        Assert.Equal((100.0, true), (old.ProgressPercent, old.Watched));

        // History has the show, by when it was last watched.
        Assert.Contains(await client.GetFromJsonAsync<List<MovieSummaryDto>>("/api/playback/history", Json) ?? [], i => i.Id == old.Id);
        await client.PostAsync($"/api/shows/{old.Id}/unwatched", null);
    }

    [Fact]
    public async Task Saving_episodes_of_a_pack_on_the_server_saves_each_on_its_own()
    {
        var client = await ClientAsync();
        var old = await RowAsync(client, "Old Show");
        var season1 = (await client.GetFromJsonAsync<List<EpisodeDto>>($"/api/shows/{old.Id}/seasons/1", Json))!;

        async Task<ServerDownloadDto> Save(int episodeId) => (await (await client.PostAsJsonAsync("/api/downloads",
            new CreateServerDownloadRequest(0, null, episodeId), Json)).Content.ReadFromJsonAsync<ServerDownloadDto>(Json))!;
        var first = await Save(season1[0].Id);
        var second = await Save(season1[1].Id);
        var again = await Save(season1[0].Id);

        // Both come from the season 1 pack, but each is a download of just that episode.
        Assert.Equal(first.ReleaseId, second.ReleaseId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(("Old Show · S01E01", "Old Show · S01E02"), (first.MovieTitle, second.MovieTitle));

        // Removing one leaves the other.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/downloads/{first.Id}?deleteFiles=true")).StatusCode);
        var left = await client.GetFromJsonAsync<List<ServerDownloadDto>>("/api/downloads", Json);
        Assert.Equal([second.Id], left!.Where(d => d.ReleaseId == first.ReleaseId).Select(d => d.Id));
        await client.DeleteAsync($"/api/downloads/{second.Id}?deleteFiles=true");
    }

    [Fact]
    public async Task New_on_tv_puts_the_show_with_the_newest_release_first()
    {
        var client = await ClientAsync();
        var hubs = await client.GetFromJsonAsync<List<HubDto>>("/api/library/hubs", Json);
        Assert.Equal(["New Show", "Old Show"], hubs!.Single(h => h.Key == "shows-recent").Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Json_replies_are_gzipped_when_asked_for()
    {
        var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/library/hubs");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var res = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("gzip", res.Content.Headers.ContentEncoding);
    }
}
