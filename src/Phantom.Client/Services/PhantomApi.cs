using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phantom.Shared;

namespace Phantom.Services;

public class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Typed client for the Phantom server's REST API.</summary>
public class PhantomApi(HttpClient http, SettingsService settings)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Raised when the server rejects the token, so the app can return to sign-in.</summary>
    public event EventHandler? Unauthorized;

    private string Base => settings.ServerUrl;

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{Base}{path}");
        if (settings.Token is { } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var res = await http.SendAsync(Request(method, path, body), ct);
        if (res.IsSuccessStatusCode) return res;

        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized && !path.StartsWith("/api/auth") && !path.StartsWith("/api/profiles/switch"))
            Unauthorized?.Invoke(this, EventArgs.Empty);
        var message = string.IsNullOrWhiteSpace(text) || text.StartsWith('{') ? res.ReasonPhrase ?? res.StatusCode.ToString() : text.Trim('"');
        throw new ApiException(res.StatusCode, message);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, path, null, ct);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct = default)
    {
        using var res = await SendAsync(method, path, body, ct);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task SendNoContentAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var _ = await SendAsync(method, path, body, ct);
    }

    // ---------- Server & auth ----------

    public async Task<ServerInfoDto> ProbeAsync(string serverUrl, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        return (await http.GetFromJsonAsync<ServerInfoDto>($"{serverUrl.TrimEnd('/')}/api/server/info", Json, cts.Token))!;
    }

    public Task<ServerInfoDto> ServerInfoAsync(CancellationToken ct = default) => GetAsync<ServerInfoDto>("/api/server/info", ct);
    public Task<bool> SetupRequiredAsync() => GetAsync<bool>("/api/auth/setup-required");
    public Task<AuthResponse> LoginAsync(string user, string password) => SendAsync<AuthResponse>(HttpMethod.Post, "/api/auth/login", new LoginRequest(user, password));
    public Task<AuthResponse> RegisterAsync(string user, string password) => SendAsync<AuthResponse>(HttpMethod.Post, "/api/auth/register", new RegisterRequest(user, password));
    public Task<UserDto> MeAsync() => GetAsync<UserDto>("/api/auth/me");

    public Task<StreamStatusDto> StreamStatusAsync(int movieId, int? releaseId, CancellationToken ct = default) =>
        GetAsync<StreamStatusDto>($"/api/stream/{movieId}/status{(releaseId is { } r ? $"?releaseId={r}" : "")}", ct);
    public Task<UserDto> UpdateAccountAsync(UpdateAccountRequest req) => SendAsync<UserDto>(HttpMethod.Put, "/api/auth/account", req);

    // Pairing: a TV shows a code, a signed-in device approves it (see PairingController on the server).
    public Task<PairingStartResponse> StartPairingAsync(string deviceName, CancellationToken ct = default) =>
        SendAsync<PairingStartResponse>(HttpMethod.Post, "/api/auth/pair/start", new PairingStartRequest(deviceName), ct);

    /// <summary>The sign-in once the code is approved; null while still waiting. Throws (404) once the code expired.</summary>
    public async Task<AuthResponse?> PollPairingAsync(string pairingId, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, $"/api/auth/pair/{pairingId}", null, ct);
        return res.StatusCode == HttpStatusCode.Accepted ? null : await res.Content.ReadFromJsonAsync<AuthResponse>(Json, ct);
    }

    public Task<PairingApprovedResponse> ApprovePairingAsync(string code) =>
        SendAsync<PairingApprovedResponse>(HttpMethod.Post, "/api/auth/pair/approve", new PairingApproveRequest(code));

    public Task<List<UserDto>> ProfilesAsync() => GetAsync<List<UserDto>>("/api/profiles");
    public Task<UserDto> CreateProfileAsync(CreateProfileRequest req) => SendAsync<UserDto>(HttpMethod.Post, "/api/profiles", req);
    public Task<AuthResponse> SwitchProfileAsync(int id, string? pin) => SendAsync<AuthResponse>(HttpMethod.Post, "/api/profiles/switch", new SwitchProfileRequest(id, pin));
    public Task DeleteProfileAsync(int id) => SendNoContentAsync(HttpMethod.Delete, $"/api/profiles/{id}");

    // Accounts (admin): every account signs in with its own username and password, and has its own profiles.
    public Task<List<UserDto>> UsersAsync() => GetAsync<List<UserDto>>("/api/users");
    public Task CreateAccountAsync(string user, string password) => SendAsync<AuthResponse>(HttpMethod.Post, "/api/auth/register", new RegisterRequest(user, password));
    public Task SetAdminAsync(int id, bool value) => SendNoContentAsync(HttpMethod.Put, $"/api/users/{id}/admin?value={value}");
    public Task DeleteUserAsync(int id) => SendNoContentAsync(HttpMethod.Delete, $"/api/users/{id}");

    // ---------- Library ----------

    public Task<List<HubDto>> HubsAsync(CancellationToken ct = default) => GetAsync<List<HubDto>>("/api/library/hubs", ct);
    public Task<LibraryFiltersDto> FiltersAsync() => GetAsync<LibraryFiltersDto>("/api/library/filters");
    public Task<SearchResultDto> SearchAsync(string q, CancellationToken ct = default) => GetAsync<SearchResultDto>($"/api/library/search?q={Uri.EscapeDataString(q)}", ct);

    public Task<PagedResult<MovieSummaryDto>> MoviesAsync(MovieQuery q, CancellationToken ct = default)
    {
        var parts = new List<string>
        {
            $"sort={q.Sort}", $"descending={q.Descending}", $"page={q.Page}", $"pageSize={q.PageSize}",
        };
        void Add(string key, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } s) parts.Add($"{key}={Uri.EscapeDataString(s)}");
        }
        Add("search", q.Search);
        Add("genre", q.Genre);
        Add("year", q.Year);
        Add("decade", q.Decade);
        Add("quality", q.Quality);
        Add("contentRating", q.ContentRating);
        Add("unwatched", q.Unwatched);
        Add("inProgress", q.InProgress);
        Add("startsWith", q.StartsWith);
        Add("actor", q.Actor);
        Add("director", q.Director);
        return GetAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?" + string.Join('&', parts), ct);
    }

    public Task<MovieDetailDto> MovieAsync(int id) => GetAsync<MovieDetailDto>($"/api/movies/{id}");
    public Task<List<MovieSummaryDto>> RelatedAsync(int id) => GetAsync<List<MovieSummaryDto>>($"/api/movies/{id}/related");
    public Task MarkWatchedAsync(int id, bool watched) => SendNoContentAsync(HttpMethod.Post, $"/api/movies/{id}/{(watched ? "watched" : "unwatched")}");
    public Task RateAsync(int id, double? rating) => SendNoContentAsync(HttpMethod.Put, $"/api/movies/{id}/rating", new SetRatingRequest(rating));

    // ---------- Playback ----------

    public Task<StreamInfoDto> StreamInfoAsync(int movieId, int? releaseId, CancellationToken ct = default) =>
        GetAsync<StreamInfoDto>($"/api/stream/{movieId}/info" + (releaseId is { } r ? $"?releaseId={r}" : ""), ct);

    public Task ReportTimelineAsync(TimelineUpdate update) => SendNoContentAsync(HttpMethod.Post, "/api/playback/timeline", update);
    public Task<List<NowPlayingDto>> SessionsAsync() => GetAsync<List<NowPlayingDto>>("/api/playback/sessions");
    public Task<List<MovieSummaryDto>> HistoryAsync() => GetAsync<List<MovieSummaryDto>>("/api/playback/history");

    public async Task<string> SubtitleTextAsync(string url, CancellationToken ct = default)
    {
        using var res = await http.GetAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.Conflict) throw new ApiException(res.StatusCode, "Subtitle is still downloading.");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    // ---------- Lists ----------

    public Task<List<MovieSummaryDto>> WatchlistAsync() => GetAsync<List<MovieSummaryDto>>("/api/watchlist");
    public Task SetWatchlistAsync(int movieId, bool on) => SendNoContentAsync(on ? HttpMethod.Put : HttpMethod.Delete, $"/api/watchlist/{movieId}");

    public Task<List<CollectionDto>> CollectionsAsync() => GetAsync<List<CollectionDto>>("/api/collections");
    public Task<CollectionDetailDto> CollectionAsync(int id) => GetAsync<CollectionDetailDto>($"/api/collections/{id}");

    public Task<List<PlaylistDto>> PlaylistsAsync() => GetAsync<List<PlaylistDto>>("/api/playlists");
    public Task<PlaylistDetailDto> PlaylistAsync(int id) => GetAsync<PlaylistDetailDto>($"/api/playlists/{id}");
    public Task<PlaylistDto> CreatePlaylistAsync(string name) => SendAsync<PlaylistDto>(HttpMethod.Post, "/api/playlists", new CreatePlaylistRequest(name));
    public Task DeletePlaylistAsync(int id) => SendNoContentAsync(HttpMethod.Delete, $"/api/playlists/{id}");
    public Task AddToPlaylistAsync(int id, int movieId) => SendNoContentAsync(HttpMethod.Post, $"/api/playlists/{id}/items", new AddItemRequest(movieId));
    public Task RemoveFromPlaylistAsync(int id, int movieId) => SendNoContentAsync(HttpMethod.Delete, $"/api/playlists/{id}/items/{movieId}");
    public Task ReorderPlaylistAsync(int id, IReadOnlyList<int> ids) => SendNoContentAsync(HttpMethod.Put, $"/api/playlists/{id}/order", new ReorderRequest(ids));

    // ---------- Server downloads ----------

    public Task<List<ServerDownloadDto>> ServerDownloadsAsync() => GetAsync<List<ServerDownloadDto>>("/api/downloads");
    public Task<ServerDownloadDto> QueueServerDownloadAsync(int movieId, int? releaseId) =>
        SendAsync<ServerDownloadDto>(HttpMethod.Post, "/api/downloads", new CreateServerDownloadRequest(movieId, releaseId));
    public Task PauseServerDownloadAsync(int id) => SendNoContentAsync(HttpMethod.Post, $"/api/downloads/{id}/pause");
    public Task ResumeServerDownloadAsync(int id) => SendNoContentAsync(HttpMethod.Post, $"/api/downloads/{id}/resume");
    public Task DeleteServerDownloadAsync(int id, bool deleteFiles) => SendNoContentAsync(HttpMethod.Delete, $"/api/downloads/{id}?deleteFiles={deleteFiles}");

    // ---------- Admin ----------

    public Task<List<SourceDto>> SourcesAsync() => GetAsync<List<SourceDto>>("/api/sources");
    public Task<MetadataStatusDto> MetadataStatusAsync() => GetAsync<MetadataStatusDto>("/api/server/metadata");
    public Task<MetadataStatusDto> RefreshAllMetadataAsync() => SendAsync<MetadataStatusDto>(HttpMethod.Post, "/api/server/metadata/refresh", null);
    public Task<SourceDto> SetSourceEnabledAsync(int id, bool value) => SendAsync<SourceDto>(HttpMethod.Put, $"/api/sources/{id}/enabled?value={value}", null);
    public Task SyncSourceAsync(int id) => SendNoContentAsync(HttpMethod.Post, $"/api/sources/{id}/sync");
    public Task<ServerSettingsDto> ServerSettingsAsync() => GetAsync<ServerSettingsDto>("/api/server/settings");
    public Task SaveServerSettingsAsync(ServerSettingsDto s) => SendNoContentAsync(HttpMethod.Put, "/api/server/settings", s);
}
