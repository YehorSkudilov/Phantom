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
        builder.UseSetting("SwaggerAuth:Username", "test");
        builder.UseSetting("SwaggerAuth:Password", "test");
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
    public async Task Health_is_public()
    {
        var res = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Swagger_requires_basic_auth()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("test:test"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
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
    public async Task Each_account_has_its_own_sign_in_and_profile_names()
    {
        // The admin creates a second account; it signs in with its own credentials.
        var admin = await AdminClientAsync();
        var name = "family" + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "secret12"), Json)).StatusCode);
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(name, "secret12"), Json);
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>(Json);
        var family = _factory.CreateClient();
        family.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        // Both accounts can have a "Kids" profile; one account can't have two.
        var profile = "Kids" + Guid.NewGuid().ToString("N")[..4];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/profiles", new CreateProfileRequest(profile, null, null, null), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await family.PostAsJsonAsync("/api/profiles", new CreateProfileRequest(profile, null, null, null), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await family.PostAsJsonAsync("/api/profiles", new CreateProfileRequest(profile, null, null, null), Json)).StatusCode);

        // Each account only sees its own profiles.
        var familyProfiles = await family.GetFromJsonAsync<List<UserDto>>("/api/profiles", Json);
        Assert.Equal([name, profile], familyProfiles!.Select(p => p.Username));
    }

    [Fact]
    public async Task A_tv_signs_in_with_a_code_approved_on_another_device()
    {
        var tv = _factory.CreateClient();
        var start = await (await tv.PostAsJsonAsync("/api/auth/pair/start", new PairingStartRequest("Living room TV"), Json))
            .Content.ReadFromJsonAsync<PairingStartResponse>(Json);
        Assert.Equal(6, start!.Code.Length);
        Assert.Equal(HttpStatusCode.Accepted, (await tv.GetAsync($"/api/auth/pair/{start.PairingId}")).StatusCode);

        var phone = await AdminClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsJsonAsync("/api/auth/pair/approve", new PairingApproveRequest("ZZZZZZ"), Json)).StatusCode);
        var approved = await phone.PostAsJsonAsync("/api/auth/pair/approve", new PairingApproveRequest(start.Code.ToLowerInvariant()), Json);
        Assert.Equal("Living room TV", (await approved.Content.ReadFromJsonAsync<PairingApprovedResponse>(Json))!.DeviceName);

        var auth = await (await tv.GetAsync($"/api/auth/pair/{start.PairingId}")).Content.ReadFromJsonAsync<AuthResponse>(Json);
        Assert.Equal("owner", auth!.User.Username);
        // Handed out once.
        Assert.Equal(HttpStatusCode.NotFound, (await tv.GetAsync($"/api/auth/pair/{start.PairingId}")).StatusCode);
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
    public async Task Sources_are_built_in_and_can_only_be_switched_on_or_off()
    {
        var client = await AdminClientAsync();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync("/api/sources", new { Name = "x" }, Json)).StatusCode);

        // The test server skips seeding, so add one directly.
        int id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var source = new Source { Name = "YTS", Type = SourceType.Yts, Enabled = false };
            db.Sources.Add(source);
            db.SaveChanges();
            id = source.Id;
        }
        Assert.False((await client.DeleteAsync($"/api/sources/{id}")).IsSuccessStatusCode);

        var updated = await (await client.PutAsync($"/api/sources/{id}/enabled?value=true", null)).Content.ReadFromJsonAsync<SourceDto>(Json);
        Assert.True(updated!.Enabled);
    }


}
