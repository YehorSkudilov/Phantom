using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Phantom.Api.Deployments;

/// <summary>
/// Manage server's Deployments tab in the app: this repository's GitHub Actions, run from the admin. Off until
/// Deployments__GitHubToken and Deployments__Repository are set (the Publish API workflow writes them from the
/// ADMIN_GITHUB_TOKEN secret and the repository's own name).
/// </summary>
public sealed class DeploymentsOptions
{
    public const string Section = "Deployments";

    /// <summary>
    /// A fine-grained personal access token for the repository with Actions: read and write, Administration: read and
    /// write (to switch it between public and private), Contents: read and Metadata: read.
    /// </summary>
    public string GitHubToken { get; set; } = "";

    /// <summary>owner/name, e.g. YehorSkudilov/Phantom.</summary>
    public string Repository { get; set; } = "";

    /// <summary>
    /// Runs on a public repository cost no Actions minutes, so the repository is made public before anything is run
    /// from the admin and private again once the last run (any run, however it was started) has finished.
    /// </summary>
    public bool AutoPrivate { get; set; } = true;

    /// <summary>A commit whose first line matches this is a release: the commits after it aren't published yet.</summary>
    public string VersionCommitPattern { get; set; } = @"^chore: bump (game )?version to (?<version>\S+)";

    public bool IsConfigured => GitHubToken.Length > 0 && Repository.Split('/') is [{ Length: > 0 }, { Length: > 0 }];
}

/// <summary>GitHub refused or failed a call; the message is GitHub's and safe to show to an admin.</summary>
public sealed class GitHubException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The few GitHub REST calls the Deployments tab needs, as the token in <see cref="DeploymentsOptions"/>.</summary>
public sealed class GitHubActions(IHttpClientFactory http, IOptions<DeploymentsOptions> options)
{
    public const string ClientName = "github-deployments";

    private readonly DeploymentsOptions _options = options.Value;

    public DeploymentsOptions Options => _options;
    private string Repo => $"repos/{_options.Repository.Trim()}";

    public Task<JsonElement> RepositoryAsync(CancellationToken ct) => GetAsync(Repo, ct);

    public Task SetPrivateAsync(bool isPrivate, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, Repo, new { @private = isPrivate }, ct);

    public async Task<List<JsonElement>> WorkflowsAsync(CancellationToken ct)
    {
        var body = await GetAsync($"{Repo}/actions/workflows?per_page=100", ct);
        return [.. body.GetProperty("workflows").EnumerateArray()];
    }

    /// <summary>The newest runs of every workflow, or of one.</summary>
    public async Task<List<JsonElement>> RunsAsync(int count, CancellationToken ct, long? workflowId = null, string? status = null)
    {
        var path = workflowId is { } id ? $"{Repo}/actions/workflows/{id}/runs" : $"{Repo}/actions/runs";
        path += $"?per_page={count}" + (status is null ? "" : $"&status={status}");
        var body = await GetAsync(path, ct);
        return [.. body.GetProperty("workflow_runs").EnumerateArray()];
    }

    /// <summary>Runs that haven't finished: queued, waiting for approval or running.</summary>
    public async Task<List<JsonElement>> ActiveRunsAsync(CancellationToken ct) =>
        (await RunsAsync(50, ct)).Where(r => r.GetProperty("status").GetString() != "completed").ToList();

    public async Task<List<JsonElement>> JobsAsync(long runId, CancellationToken ct)
    {
        var body = await GetAsync($"{Repo}/actions/runs/{runId}/jobs?per_page=100", ct);
        return [.. body.GetProperty("jobs").EnumerateArray()];
    }

    public Task<JsonElement> RunAsync(long runId, CancellationToken ct) => GetAsync($"{Repo}/actions/runs/{runId}", ct);

    public Task DispatchAsync(long workflowId, string gitRef, IReadOnlyDictionary<string, string> inputs, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Repo}/actions/workflows/{workflowId}/dispatches", new { @ref = gitRef, inputs }, ct);

    public Task CancelAsync(long runId, CancellationToken ct) => SendAsync(HttpMethod.Post, $"{Repo}/actions/runs/{runId}/cancel", null, ct);

    /// <summary>Runs the failed jobs again, or the whole run when <paramref name="failedOnly"/> is false.</summary>
    public Task RerunAsync(long runId, bool failedOnly, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Repo}/actions/runs/{runId}/{(failedOnly ? "rerun-failed-jobs" : "rerun")}", null, ct);

    /// <summary>The newest commits on <paramref name="branch"/>, newest first.</summary>
    public async Task<List<JsonElement>> CommitsAsync(string branch, int perPage, int page, CancellationToken ct)
    {
        var body = await GetAsync($"{Repo}/commits?sha={Uri.EscapeDataString(branch)}&per_page={perPage}&page={page}", ct);
        return [.. body.EnumerateArray()];
    }

    /// <summary>What <paramref name="head"/> has that <paramref name="base"/> hasn't (commits oldest first, at most 250).</summary>
    public Task<JsonElement> CompareAsync(string @base, string head, CancellationToken ct) =>
        GetAsync($"{Repo}/compare/{Uri.EscapeDataString(@base)}...{Uri.EscapeDataString(head)}?per_page=250", ct);

    /// <summary>A text file in the repository, e.g. a workflow's YAML.</summary>
    public async Task<string> FileAsync(string path, string gitRef, CancellationToken ct)
    {
        var body = await GetAsync($"{Repo}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}?ref={Uri.EscapeDataString(gitRef)}", ct);
        var content = body.GetProperty("content").GetString() ?? "";
        return Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, path, null, ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var _ = await SendRawAsync(method, path, body, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            throw new GitHubException(503, "Deployments aren't set up: the API has no Deployments__GitHubToken or Deployments__Repository.");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.GitHubToken.Trim());
        if (body is not null)
            request.Content = JsonContent.Create(body);

        var client = http.CreateClient(ClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new GitHubException(502, $"Couldn't reach GitHub: {e.Message}");
        }
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            string? message = null;
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
            catch (JsonException)
            {
                // Not GitHub's JSON error (e.g. a proxy's page).
            }
            var status = (int)response.StatusCode;
            throw new GitHubException(status, status switch
            {
                401 => "GitHub refused the token (Deployments__GitHubToken): it's wrong or has expired.",
                403 or 404 when message is "Resource not accessible by personal access token" or "Not Found" =>
                    $"The token can't do that on {_options.Repository}: it needs Actions (read and write), Administration (read and write) and Contents (read) on this repository.",
                _ => $"GitHub: {message ?? response.ReasonPhrase}",
            });
        }
    }

    public static void Configure(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("admin-deployments");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
}
