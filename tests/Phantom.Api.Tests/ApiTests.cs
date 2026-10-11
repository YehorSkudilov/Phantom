using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Tests;

public sealed class PhantomFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "phantom-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Phantom:DataDirectory", DataDir);
        builder.UseSetting("Phantom:SeedDefaultSources", "false");
        builder.UseSetting("Phantom:TorrentListenPort", Random.Shared.Next(40000, 50000).ToString());
        builder.UseSetting("Phantom:FfmpegPath", "/nonexistent/ffmpeg");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
    }
}

public class ApiTests : IClassFixture<PhantomFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly PhantomFactory _factory;

    public ApiTests(PhantomFactory factory)
    {
        _factory = factory;
        SeedMovies();
    }

    private void SeedMovies()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        if (db.Movies.Any()) return;
        var horror = new Genre { Name = "Horror" };
        db.Movies.AddRange(
            Movie("Nosferatu", 1922, "G", horror, "a"),
            Movie("Night of the Living Dead", 1968, "R", horror, "b"),
            Movie("The General", 1926, null, new Genre { Name = "Comedy" }, "c"));
        db.SaveChanges();

        static Movie Movie(string title, int year, string? rating, Genre genre, string hash) => new()
        {
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            Year = year,
            ContentRating = rating,
            ExternalKey = title,
            Genres = [genre],
            Releases = [new Release { Name = title, InfoHash = new string(hash[0], 40), Quality = "SD", Seeders = 3 }],
        };
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("owner", "password1"));
        if (res.StatusCode != HttpStatusCode.OK)
            res = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("owner", "password1"));
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    [Fact]
    public async Task Library_requires_authentication()
    {
        var res = await _factory.CreateClient().GetAsync("/api/library/movies");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task First_account_is_admin_and_open_registration_is_closed()
    {
        var admin = await AdminClientAsync();
        var me = await admin.GetFromJsonAsync<UserDto>("/api/auth/me", Json);
        Assert.True(me!.IsAdmin);

        var stranger = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest("mallory", "password1"));
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
    }

    [Fact]
    public async Task Browse_filters_and_sorts()
    {
        var client = await AdminClientAsync();
        var page = await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?genre=Horror&sort=Year&descending=false", Json);
        Assert.Equal(["Nosferatu", "Night of the Living Dead"], page!.Items.Select(m => m.Title));

        var filters = await client.GetFromJsonAsync<LibraryFiltersDto>("/api/library/filters", Json);
        Assert.Contains("Comedy", filters!.Genres);
    }

    [Fact]
    public async Task Timeline_sets_resume_point_then_marks_watched_at_90_percent()
    {
        var client = await AdminClientAsync();
        var id = (await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?search=General", Json))!.Items.Single().Id;

        await client.PostAsJsonAsync("/api/playback/timeline", new TimelineUpdate(id, null, 30_000, 100_000, PlayerState.Playing, "test"), Json);
        var hubs = await client.GetFromJsonAsync<List<HubDto>>("/api/library/hubs", Json);
        var resume = hubs!.Single(h => h.Key == "continue").Items.Single();
        Assert.Equal(30, resume.ProgressPercent);

        await client.PostAsJsonAsync("/api/playback/timeline", new TimelineUpdate(id, null, 95_000, 100_000, PlayerState.Stopped, "test"), Json);
        var detail = await client.GetFromJsonAsync<MovieDetailDto>($"/api/movies/{id}", Json);
        Assert.True(detail!.Playback.Watched);
        Assert.Equal(1, detail.Playback.ViewCount);
        Assert.Equal(0, detail.Playback.PositionMs);
    }

    [Fact]
    public async Task Managed_profile_with_pin_hides_restricted_titles()
    {
        var owner = await AdminClientAsync();
        var created = await owner.PostAsJsonAsync("/api/profiles", new CreateProfileRequest("kid" + Guid.NewGuid().ToString("N")[..6], "4321", "PG", null), Json);
        var profile = await created.Content.ReadFromJsonAsync<UserDto>(Json);

        var wrongPin = await owner.PostAsJsonAsync("/api/profiles/switch", new SwitchProfileRequest(profile!.Id, "0000"), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPin.StatusCode);

        var switched = await owner.PostAsJsonAsync("/api/profiles/switch", new SwitchProfileRequest(profile.Id, "4321"), Json);
        var auth = await switched.Content.ReadFromJsonAsync<AuthResponse>(Json);
        var kid = _factory.CreateClient();
        kid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        var titles = (await kid.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies", Json))!.Items.Select(m => m.Title).ToList();
        Assert.Contains("Nosferatu", titles);
        Assert.Contains("The General", titles); // unrated titles stay visible
        Assert.DoesNotContain("Night of the Living Dead", titles);
    }

    [Fact]
    public async Task Playlists_keep_order_and_watchlist_round_trips()
    {
        var client = await AdminClientAsync();
        var ids = (await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?sort=Title&descending=false", Json))!.Items.Select(m => m.Id).ToList();

        var playlist = await (await client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest("Marathon"), Json)).Content.ReadFromJsonAsync<PlaylistDto>(Json);
        foreach (var id in ids) await client.PostAsJsonAsync($"/api/playlists/{playlist!.Id}/items", new AddItemRequest(id), Json);
        var reversed = ids.AsEnumerable().Reverse().ToList();
        await client.PutAsJsonAsync($"/api/playlists/{playlist!.Id}/order", new ReorderRequest(reversed), Json);
        var detail = await client.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlist.Id}", Json);
        Assert.Equal(reversed, detail!.Items.Select(i => i.Id));

        await client.PutAsync($"/api/watchlist/{ids[0]}", null);
        Assert.Contains(await client.GetFromJsonAsync<List<MovieSummaryDto>>("/api/watchlist", Json) ?? [], m => m.Id == ids[0]);
        await client.DeleteAsync($"/api/watchlist/{ids[0]}");
        Assert.DoesNotContain(await client.GetFromJsonAsync<List<MovieSummaryDto>>("/api/watchlist", Json) ?? [], m => m.Id == ids[0]);
    }

    [Fact]
    public async Task Server_download_is_queued_once_per_release()
    {
        var client = await AdminClientAsync();
        var id = (await client.GetFromJsonAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?search=Nosferatu", Json))!.Items.Single().Id;
        var a = await (await client.PostAsJsonAsync("/api/downloads", new CreateServerDownloadRequest(id, null), Json)).Content.ReadFromJsonAsync<ServerDownloadDto>(Json);
        var b = await (await client.PostAsJsonAsync("/api/downloads", new CreateServerDownloadRequest(id, null), Json)).Content.ReadFromJsonAsync<ServerDownloadDto>(Json);
        Assert.Equal(a!.Id, b!.Id);

        var del = await client.DeleteAsync($"/api/downloads/{a.Id}?deleteFiles=true");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task Torznab_source_requires_url_and_hides_api_key()
    {
        var client = await AdminClientAsync();
        var bad = await client.PostAsJsonAsync("/api/sources", new UpsertSourceRequest("x", SourceType.Torznab, null, null, null, null, false, 60), Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/sources", new UpsertSourceRequest("Indexer", SourceType.Torznab, "http://127.0.0.1:9/torznab", "secret", "2000", null, false, 60), Json);
        var body = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body);
        Assert.True(JsonSerializer.Deserialize<SourceDto>(body, Json)!.HasApiKey);
    }
}
