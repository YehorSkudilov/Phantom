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
        throw new ApiException(res.StatusCode, Describe(res.StatusCode, text));
    }

    /// <summary>
    /// A failed response as a sentence for people: the server's own message when it sent one (plain text, a JSON string or
    /// a problem's "detail"), otherwise what the status means. Never the HTML error page a proxy answers with when the
    /// server is down ("502 Bad Gateway").
    /// </summary>
    internal static string Describe(HttpStatusCode status, string body)
    {
        var text = body.Trim();
        if (text.StartsWith('"') && text.EndsWith('"') && text.Length > 1)
        {
            try { text = JsonSerializer.Deserialize<string>(text) ?? ""; } catch (JsonException) { }
        }
        else if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                text = doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? ""
                    : doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? ""
                    : "";
            }
            catch (JsonException) { text = ""; }
        }
        // Something the server wrote for people: short, and not markup.
        if (text.Length is > 0 and < 300 && !text.Contains('<')) return text;

        return (int)status switch
        {
            400 => "That didn't work. Check what you entered and try again.",
            401 => "Your sign-in has expired. Sign in again.",
            403 => "You don't have permission to do that.",
            404 => "That couldn't be found. It may have been removed.",
            409 => "That already exists.",
            429 => "The server is busy. Try again in a moment.",
            500 => "Something went wrong on the server. Try again, and if it keeps happening, check the server's logs.",
            501 => "The server can't do that: the feature isn't set up there.",
            502 or 503 or 504 => "The Phantom server isn't responding right now. It may be restarting — try again in a minute.",
            _ => $"The server couldn't do that (error {(int)status}).",
        };
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

    /// <summary>
    /// Why a stream URL the player couldn't play fails, as the server says (a conversion that failed answers with
    /// ffmpeg's reason), or null when it answers fine. The answer is only looked at, never read through.
    /// </summary>
    public async Task<string?> StreamErrorAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (res.IsSuccessStatusCode) return null;
            return Describe(res.StatusCode, await res.Content.ReadAsStringAsync(cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<ServerInfoDto> ProbeAsync(string serverUrl, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return (await http.GetFromJsonAsync<ServerInfoDto>($"{serverUrl.TrimEnd('/')}/api/server/info", Json, cts.Token))!;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // No answer in time: the server can't be reached (said as such), not a cancellation.
            throw new HttpRequestException("The server didn't answer.", ex);
        }
    }

    public Task<ServerInfoDto> ServerInfoAsync(CancellationToken ct = default) => GetAsync<ServerInfoDto>("/api/server/info", ct);
    public Task<bool> SetupRequiredAsync() => GetAsync<bool>("/api/auth/setup-required");
    public Task<AuthResponse> LoginAsync(string user, string password) => SendAsync<AuthResponse>(HttpMethod.Post, "/api/auth/login", new LoginRequest(user, password));
    public Task<AuthResponse> RegisterAsync(string user, string password) => SendAsync<AuthResponse>(HttpMethod.Post, "/api/auth/register", new RegisterRequest(user, password));
    public Task<UserDto> MeAsync() => GetAsync<UserDto>("/api/auth/me");

    public Task<StreamStatusDto> StreamStatusAsync(int movieId, int? releaseId, TimeSpan position, CancellationToken ct = default, int? episodeId = null) =>
        GetAsync<StreamStatusDto>($"{StreamBase(movieId, episodeId)}/status?position={position.TotalSeconds:0}{(releaseId is { } r ? $"&releaseId={r}" : "")}", ct);

    /// <summary>Where a movie's (or, with <paramref name="episodeId"/>, an episode's) stream endpoints are.</summary>
    private static string StreamBase(int movieId, int? episodeId) => episodeId is { } e ? $"/api/stream/episode/{e}" : $"/api/stream/{movieId}";
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
    public Task<UserDto> UpdateProfileAsync(int id, UpdateProfileRequest req) => SendAsync<UserDto>(HttpMethod.Put, $"/api/profiles/{id}", req);
    public Task DeleteProfileAsync(int id) => SendNoContentAsync(HttpMethod.Delete, $"/api/profiles/{id}");

    // Accounts (admin): every account signs in with its own username and password, and has its own profiles.
    public Task<List<UserDto>> UsersAsync() => GetAsync<List<UserDto>>("/api/users");
    public Task CreateAccountAsync(string user, string password) => SendAsync<AuthResponse>(HttpMethod.Post, "/api/auth/register", new RegisterRequest(user, password));
    public Task SetAdminAsync(int id, bool value) => SendNoContentAsync(HttpMethod.Put, $"/api/users/{id}/admin?value={value}");
    public Task DeleteUserAsync(int id) => SendNoContentAsync(HttpMethod.Delete, $"/api/users/{id}");

    // ---------- Library ----------

    public Task<List<HubDto>> HubsAsync(CancellationToken ct = default) => GetAsync<List<HubDto>>("/api/library/hubs", ct);
    public Task<LibraryFiltersDto> FiltersAsync(MediaKind kind = MediaKind.Movie, bool all = false) =>
        GetAsync<LibraryFiltersDto>($"/api/library/filters?kind={kind}" + (all ? "&all=true" : ""));
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
        if (q.Kind != MediaKind.Movie) Add("kind", q.Kind);
        if (q.AllKinds) Add("allKinds", true);
        return GetAsync<PagedResult<MovieSummaryDto>>("/api/library/movies?" + string.Join('&', parts), ct);
    }

    public Task<MovieDetailDto> MovieAsync(int id) => GetAsync<MovieDetailDto>($"/api/movies/{id}");
    public Task<List<MovieSummaryDto>> RelatedAsync(int id) => GetAsync<List<MovieSummaryDto>>($"/api/movies/{id}/related");
    public Task MarkWatchedAsync(int id, bool watched) => SendNoContentAsync(HttpMethod.Post, $"/api/movies/{id}/{(watched ? "watched" : "unwatched")}");
    public Task RateAsync(int id, double? rating) => SendNoContentAsync(HttpMethod.Put, $"/api/movies/{id}/rating", new SetRatingRequest(rating));

    // ---------- Playback ----------

    public Task<StreamInfoDto> StreamInfoAsync(int movieId, int? releaseId, CancellationToken ct = default, int? episodeId = null) =>
        GetAsync<StreamInfoDto>($"{StreamBase(movieId, episodeId)}/info" + (releaseId is { } r ? $"?releaseId={r}" : ""), ct);

    // ---------- Shows ----------

    public Task<ShowDetailDto> ShowAsync(int id) => GetAsync<ShowDetailDto>($"/api/shows/{id}");
    public Task<List<EpisodeDto>> SeasonAsync(int showId, int season) => GetAsync<List<EpisodeDto>>($"/api/shows/{showId}/seasons/{season}");
    public Task<EpisodeDetailDto> EpisodeAsync(int id) => GetAsync<EpisodeDetailDto>($"/api/episodes/{id}");
    public Task MarkShowWatchedAsync(int id, bool watched) => SendNoContentAsync(HttpMethod.Post, $"/api/shows/{id}/{(watched ? "watched" : "unwatched")}");
    public Task MarkSeasonWatchedAsync(int showId, int season, bool watched) =>
        SendNoContentAsync(HttpMethod.Post, $"/api/shows/{showId}/seasons/{season}/{(watched ? "watched" : "unwatched")}");
    public Task MarkEpisodeWatchedAsync(int id, bool watched) => SendNoContentAsync(HttpMethod.Post, $"/api/episodes/{id}/{(watched ? "watched" : "unwatched")}");
    public Task SetShowWatchlistAsync(int id, bool on) => SendNoContentAsync(on ? HttpMethod.Put : HttpMethod.Delete, $"/api/shows/{id}/watchlist");

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

    /// <summary>
    /// A seek-bar preview frame, or null when the server has none for that point. Only a real JPEG counts: the server
    /// answers 204 (no body) when a newer preview replaced this one, and an empty image crashed the app on iOS when the
    /// image view tried to decode it.
    /// </summary>
    public async Task<byte[]?> ThumbnailAsync(string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NoContent) return null;
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return bytes is [0xFF, 0xD8, ..] && bytes.Length > 64 ? bytes : null;
    }

    /// <summary>
    /// Every seek preview the server has made for a video, by second (see the server's stream "thumbnails" endpoint),
    /// and whether that's all of them.
    /// </summary>
    public async Task<(Dictionary<int, byte[]> Frames, bool Complete)> ThumbnailPackAsync(string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return ([], false);
        var complete = res.Headers.TryGetValues("X-Thumbnails-Complete", out var values) && values.FirstOrDefault() == "1";
        var data = await res.Content.ReadAsByteArrayAsync(ct);
        var frames = new Dictionary<int, byte[]>();
        using var reader = new BinaryReader(new MemoryStream(data));
        try
        {
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var seconds = reader.ReadInt32();
                var jpeg = reader.ReadBytes(reader.ReadInt32());
                if (jpeg is [0xFF, 0xD8, ..]) frames[seconds] = jpeg;
            }
        }
        catch (EndOfStreamException)
        {
            // Cut short: keep what came.
        }
        return (frames, complete);
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

    /// <summary>Adds a whole show to a playlist.</summary>
    public Task AddShowToPlaylistAsync(int id, int showId) =>
        SendNoContentAsync(HttpMethod.Post, $"/api/playlists/{id}/items", new AddItemRequest(0, ShowId: showId));

    /// <summary>Adds episodes (one, or a season's) to a playlist, in this order.</summary>
    public Task AddEpisodesToPlaylistAsync(int id, IReadOnlyList<int> episodeIds) =>
        SendNoContentAsync(HttpMethod.Post, $"/api/playlists/{id}/items", new AddItemRequest(0, EpisodeIds: episodeIds));

    /// <summary>Takes entries out of a playlist by key ("m12", "s4", "e99").</summary>
    public Task RemovePlaylistEntriesAsync(int id, IEnumerable<string> keys) =>
        SendNoContentAsync(HttpMethod.Delete, $"/api/playlists/{id}/entries/{string.Join(',', keys)}");

    public Task ReorderPlaylistEntriesAsync(int id, IReadOnlyList<string> keys) =>
        SendNoContentAsync(HttpMethod.Put, $"/api/playlists/{id}/order", new ReorderRequest([], keys));

    /// <summary>The playlists holding any of these entries ("m12", "s4", "e99"), and which.</summary>
    public Task<List<PlaylistMembershipDto>> PlaylistsContainingAsync(IEnumerable<string> keys) =>
        GetAsync<List<PlaylistMembershipDto>>($"/api/playlists/containing?keys={string.Join(',', keys)}");

    public Task RateShowAsync(int id, double? rating) => SendNoContentAsync(HttpMethod.Put, $"/api/shows/{id}/rating", new SetRatingRequest(rating));
    public Task<List<MovieSummaryDto>> RelatedShowsAsync(int id) => GetAsync<List<MovieSummaryDto>>($"/api/shows/{id}/related");
    public Task<List<ReleaseDto>> SeasonReleasesAsync(int showId, int season) => GetAsync<List<ReleaseDto>>($"/api/shows/{showId}/seasons/{season}/releases");

    // ---------- Server downloads ----------

    public Task<List<ServerDownloadDto>> ServerDownloadsAsync() => GetAsync<List<ServerDownloadDto>>("/api/downloads");
    public Task<ServerDownloadDto> QueueServerDownloadAsync(int movieId, int? releaseId, int? episodeId = null) =>
        SendAsync<ServerDownloadDto>(HttpMethod.Post, "/api/downloads", new CreateServerDownloadRequest(movieId, releaseId, episodeId));
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
    public Task<StorageDto> StorageAsync() => GetAsync<StorageDto>("/api/server/storage");
    public Task ClearStreamCacheAsync(string infoHash) => SendNoContentAsync(HttpMethod.Delete, $"/api/server/storage/cache/{infoHash}");
    public Task<StorageDto> ClearIdleCachesAsync() => SendAsync<StorageDto>(HttpMethod.Post, "/api/server/storage/cache/clear", null);
    public Task<StorageDto> ClearPreviewsAsync() => SendAsync<StorageDto>(HttpMethod.Post, "/api/server/storage/previews/clear", null);
}
