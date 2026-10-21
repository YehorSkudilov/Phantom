using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Phantom.Shared;
using YamlDotNet.RepresentationModel;

namespace Phantom.Api.Deployments;

/// <summary>
/// What the Deployments tab shows, read from GitHub: the repository and whether it's public, the commits since the last
/// release (the last version bump commit, see <see cref="DeploymentsOptions.VersionCommitPattern"/>), the workflows
/// that can be run with their inputs, and the runs with how far along they are.
/// </summary>
public sealed class DeploymentsService(GitHubActions github, RepoVisibilityKeeper keeper, TimeProvider time)
{
    private static readonly TimeSpan InputsFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TypicalFor = TimeSpan.FromMinutes(10);

    // Workflow inputs by path, and how long a workflow usually takes by id: both change rarely.
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<WorkflowInputDto>? Inputs)> _inputs = new();
    private readonly ConcurrentDictionary<long, (DateTimeOffset At, int? Seconds)> _typical = new();

    public DeploymentsOptions Options => github.Options;
    public KeeperStatus Keeper => keeper.Status;

    public async Task<DeploymentsOverviewDto> OverviewAsync(CancellationToken ct)
    {
        var repoJson = await github.RepositoryAsync(ct);
        var repo = Repository(repoJson);
        var runsTask = github.RunsAsync(50, ct);
        var workflowsTask = github.WorkflowsAsync(ct);
        var unpublishedTask = UnpublishedAsync(repo.DefaultBranch, ct);
        var runs = await runsTask;

        // GitHub's own workflows (Pages, Dependabot) have no file in the repository.
        var files = (await workflowsTask).Where(w => w.GetProperty("path").GetString()?.StartsWith(".github/workflows/", StringComparison.Ordinal) == true);
        var workflows = await Task.WhenAll(files.Select(async w =>
        {
            var id = w.GetProperty("id").GetInt64();
            var path = w.GetProperty("path").GetString()!;
            var state = w.GetProperty("state").GetString() ?? "";
            var inputs = await InputsAsync(path, repo.DefaultBranch, ct);
            var last = runs.FirstOrDefault(r => r.GetProperty("workflow_id").GetInt64() == id);
            return new WorkflowDto(
                id,
                w.GetProperty("name").GetString() ?? path,
                path,
                state,
                w.GetProperty("html_url").GetString() ?? "",
                IsPublish: Path.GetFileName(path).Equals("Publish.yml", StringComparison.OrdinalIgnoreCase),
                CanRun: state == "active" && inputs is not null,
                inputs ?? [],
                last.ValueKind == JsonValueKind.Object ? Run(last, null, null) : null,
                await TypicalSecondsAsync(id, ct));
        }));

        return new DeploymentsOverviewDto(
            repo,
            keeper.Status,
            Options.AutoPrivate,
            await unpublishedTask,
            [.. workflows.OrderByDescending(w => w.IsPublish).ThenBy(w => w.Name)]);
    }

    /// <summary>The newest runs; the unfinished ones with their jobs and steps.</summary>
    public async Task<RunsDto> RunsAsync(int count, CancellationToken ct)
    {
        var repoTask = github.RepositoryAsync(ct);
        var runs = await github.RunsAsync(count, ct);
        var result = await Task.WhenAll(runs.Select(async r =>
        {
            if (r.GetProperty("status").GetString() == "completed")
                return Run(r, null, null);
            var jobs = await github.JobsAsync(r.GetProperty("id").GetInt64(), ct);
            return Run(r, jobs, await TypicalSecondsAsync(r.GetProperty("workflow_id").GetInt64(), ct));
        }));
        return new RunsDto(Repository(await repoTask), keeper.Status, [.. result]);
    }

    /// <summary>One run with its jobs and steps, finished or not.</summary>
    public async Task<RunDto> RunAsync(long id, CancellationToken ct)
    {
        var run = await github.RunAsync(id, ct);
        var jobs = await github.JobsAsync(id, ct);
        return Run(run, jobs, await TypicalSecondsAsync(run.GetProperty("workflow_id").GetInt64(), ct));
    }

    /// <summary>
    /// <paramref name="inputs"/> less any the workflow doesn't take any more, read afresh from its file: a form filled in
    /// before the file changed would otherwise send them, and GitHub refuses the run ("Unexpected inputs provided").
    /// </summary>
    public async Task<Dictionary<string, string>> CurrentInputsAsync(long workflowId, string branch, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        var path = (await github.WorkflowAsync(workflowId, ct)).GetProperty("path").GetString() ?? "";
        _inputs.TryRemove(path, out _);
        if (await InputsAsync(path, branch, ct) is not { } known)
            return new(inputs);
        var names = known.Select(i => i.Name).ToHashSet();
        return inputs.Where(i => names.Contains(i.Key)).ToDictionary();
    }

    /// <summary>The commits on the default branch after the last version bump commit.</summary>
    private async Task<UnpublishedDto> UnpublishedAsync(string branch, CancellationToken ct)
    {
        const int perPage = 100, pages = 3;
        var pattern = new Regex(Options.VersionCommitPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var pending = new List<JsonElement>();
        JsonElement? release = null;
        string? version = null;
        for (var page = 1; page <= pages && release is null; page++)
        {
            var commits = await github.CommitsAsync(branch, perPage, page, ct);
            foreach (var c in commits)
            {
                var match = pattern.Match(FirstLine(c.GetProperty("commit").GetProperty("message").GetString()));
                if (match.Success)
                {
                    release = c;
                    version = match.Groups["version"].Success ? match.Groups["version"].Value : null;
                    break;
                }
                pending.Add(c);
            }
            if (commits.Count < perPage)
                break;
        }

        int files = 0, additions = 0, deletions = 0;
        string? compareUrl = null;
        if (release is { } r && pending.Count > 0)
        {
            var compare = await github.CompareAsync(r.GetProperty("sha").GetString()!, branch, ct);
            compareUrl = compare.GetProperty("html_url").GetString();
            if (compare.TryGetProperty("files", out var changed))
                foreach (var f in changed.EnumerateArray())
                {
                    files++;
                    additions += f.GetProperty("additions").GetInt32();
                    deletions += f.GetProperty("deletions").GetInt32();
                }
        }

        var list = pending.Select(Commit).ToList();
        return new UnpublishedDto(
            VersionFound: release is not null,
            Version: version,
            VersionCommit: release is { } v ? Commit(v) : null,
            Commits: list.Count(c => !c.IsMerge),
            Merges: list.Count(c => c.IsMerge),
            FilesChanged: files,
            Additions: additions,
            Deletions: deletions,
            Authors: [.. list.Where(c => !c.IsMerge).Select(c => c.Author).OfType<string>().Distinct()],
            CompareUrl: compareUrl,
            Scanned: release is null ? pending.Count : null,
            Recent: [.. list.Take(60)]);
    }

    private async Task<List<WorkflowInputDto>?> InputsAsync(string path, string branch, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_inputs.TryGetValue(path, out var cached) && now - cached.At < InputsFor)
            return cached.Inputs;
        List<WorkflowInputDto>? inputs;
        try
        {
            inputs = DispatchInputs(await github.FileAsync(path, branch, ct));
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or GitHubException { Status: 404 })
        {
            inputs = null;
        }
        _inputs[path] = (now, inputs);
        return inputs;
    }

    /// <summary>How long the workflow's last few successful runs took on average, or null without any.</summary>
    private async Task<int?> TypicalSecondsAsync(long workflowId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_typical.TryGetValue(workflowId, out var cached) && now - cached.At < TypicalFor)
            return cached.Seconds;
        var runs = await github.RunsAsync(5, ct, workflowId, "success");
        var took = runs
            .Select(r => (Started: Date(r, "run_started_at"), Updated: Date(r, "updated_at")))
            .Where(r => r.Started is not null && r.Updated is not null)
            .Select(r => (r.Updated!.Value - r.Started!.Value).TotalSeconds)
            .Where(s => s > 0)
            .ToList();
        int? seconds = took.Count > 0 ? (int)took.Average() : null;
        _typical[workflowId] = (now, seconds);
        return seconds;
    }

    /// <summary>
    /// A workflow's workflow_dispatch inputs, or null when it can't be run by hand. GitHub's run form has no section
    /// titles, so a workflow may stand one in with a one-option choice whose description is a ═══ banner; those come
    /// through as type "section".
    /// </summary>
    public static List<WorkflowInputDto>? DispatchInputs(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return null;

        switch (Child(root, "on") ?? Child(root, "true"))
        {
            case YamlScalarNode on:
                return on.Value == "workflow_dispatch" ? [] : null;
            case YamlSequenceNode on:
                return on.Children.OfType<YamlScalarNode>().Any(n => n.Value == "workflow_dispatch") ? [] : null;
            case YamlMappingNode on when on.Children.ContainsKey(new YamlScalarNode("workflow_dispatch")):
                if (Child(on, "workflow_dispatch") is not YamlMappingNode dispatch || Child(dispatch, "inputs") is not YamlMappingNode inputs)
                    return [];
                var list = new List<WorkflowInputDto>();
                foreach (var (key, value) in inputs.Children)
                {
                    var name = ((YamlScalarNode)key).Value ?? "";
                    var input = value as YamlMappingNode;
                    var description = Scalar(input, "description");
                    var type = Scalar(input, "type") ?? "string";
                    var options = input is not null && Child(input, "options") is YamlSequenceNode o
                        ? o.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? "").ToList()
                        : [];
                    if (type == "choice" && options.Count == 1 && description?.Contains('═') == true)
                        type = "section";
                    list.Add(new WorkflowInputDto(
                        name,
                        description?.Trim('═', ' '),
                        type,
                        string.Equals(Scalar(input, "required"), "true", StringComparison.OrdinalIgnoreCase),
                        Scalar(input, "default"),
                        options));
                }
                return list;
            default:
                return null;
        }
    }

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static string? Scalar(YamlMappingNode? map, string key) =>
        map is not null && Child(map, key) is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    private RunDto Run(JsonElement r, List<JsonElement>? jobs, int? typical)
    {
        var jobDtos = jobs?.Select(Job).ToList();
        return new RunDto(
            r.GetProperty("id").GetInt64(),
            r.GetProperty("workflow_id").GetInt64(),
            r.GetProperty("name").GetString() ?? "",
            r.TryGetProperty("display_title", out var t) ? t.GetString() ?? "" : "",
            r.GetProperty("event").GetString() ?? "",
            r.GetProperty("status").GetString() ?? "",
            Text(r, "conclusion"),
            Text(r, "head_branch") ?? "",
            r.GetProperty("head_sha").GetString() ?? "",
            r.TryGetProperty("triggering_actor", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "login") : null,
            a.ValueKind == JsonValueKind.Object ? Text(a, "avatar_url") : null,
            r.GetProperty("run_number").GetInt32(),
            r.TryGetProperty("run_attempt", out var at) ? at.GetInt32() : 1,
            Date(r, "created_at") ?? time.GetUtcNow(),
            Date(r, "run_started_at"),
            Date(r, "updated_at") ?? time.GetUtcNow(),
            r.GetProperty("html_url").GetString() ?? "",
            typical,
            jobDtos is null ? null : Progress(jobDtos),
            jobDtos);
    }

    private static JobDto Job(JsonElement j) => new(
        j.GetProperty("id").GetInt64(),
        j.GetProperty("name").GetString() ?? "",
        j.GetProperty("status").GetString() ?? "",
        Text(j, "conclusion"),
        Date(j, "started_at"),
        Date(j, "completed_at"),
        Text(j, "html_url") ?? "",
        j.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
            ? [.. steps.EnumerateArray().Select(s => new StepDto(
                s.GetProperty("number").GetInt32(),
                s.GetProperty("name").GetString() ?? "",
                s.GetProperty("status").GetString() ?? "",
                Text(s, "conclusion"),
                Date(s, "started_at"),
                Date(s, "completed_at")))]
            : []);

    /// <summary>How far along a run is by its jobs, a running job counting for the share of its steps done.</summary>
    private static ProgressDto Progress(List<JobDto> jobs)
    {
        var done = jobs.Count(j => j.Status == "completed");
        var running = jobs.Where(j => j.Status == "in_progress").ToList();
        var share = done + running.Sum(j => j.Steps.Count == 0 ? 0 : (double)j.Steps.Count(s => s.Status == "completed") / j.Steps.Count);
        var current = running.Select(j => (Job: j, Step: j.Steps.FirstOrDefault(s => s.Status == "in_progress")))
            .Select(x => x.Step is null ? x.Job.Name : $"{x.Job.Name} › {x.Step.Name}")
            .FirstOrDefault();
        return new ProgressDto(
            jobs.Count,
            done,
            running.Count,
            jobs.Sum(j => j.Steps.Count),
            jobs.Sum(j => j.Steps.Count(s => s.Status == "completed")),
            jobs.Count == 0 ? 0 : Math.Round(share / jobs.Count, 3),
            current);
    }

    private static RepositoryDto Repository(JsonElement r) => new(
        r.GetProperty("full_name").GetString() ?? "",
        r.GetProperty("html_url").GetString() ?? "",
        Text(r, "visibility") ?? (r.GetProperty("private").GetBoolean() ? "private" : "public"),
        r.GetProperty("private").GetBoolean(),
        Text(r, "default_branch") ?? "main",
        Date(r, "pushed_at"));

    private static CommitDto Commit(JsonElement c)
    {
        var commit = c.GetProperty("commit");
        var author = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var message = commit.GetProperty("message").GetString() ?? "";
        return new CommitDto(
            c.GetProperty("sha").GetString() ?? "",
            FirstLine(message),
            author.ValueKind == JsonValueKind.Object ? Text(author, "login") : commit.GetProperty("author").GetProperty("name").GetString(),
            author.ValueKind == JsonValueKind.Object ? Text(author, "avatar_url") : null,
            commit.GetProperty("author").TryGetProperty("date", out var d) ? d.GetDateTimeOffset() : null,
            Text(c, "html_url") ?? "",
            c.TryGetProperty("parents", out var p) && p.GetArrayLength() > 1);
    }

    private static string FirstLine(string? message) => (message ?? "").Split('\n', 2)[0].Trim();

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;
}
