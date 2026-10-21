namespace Phantom.Shared;

// ---------- Deployments (Manage server: the repository's GitHub Actions, see Phantom.Api/Deployments) ----------

public sealed record DeploymentsOverviewDto(RepositoryDto Repository, KeeperStatus Keeper, bool AutoPrivate, UnpublishedDto Unpublished, List<WorkflowDto> Workflows);

public sealed record RepositoryDto(string FullName, string Url, string Visibility, bool IsPrivate, string DefaultBranch, DateTimeOffset? PushedUtc);

/// <summary>
/// <paramref name="Commits"/> leaves out merge commits (counted in <paramref name="Merges"/>). Without a version bump
/// commit in the last few hundred, <paramref name="Scanned"/> says how many were looked at.
/// </summary>
public sealed record UnpublishedDto(
    bool VersionFound,
    string? Version,
    CommitDto? VersionCommit,
    int Commits,
    int Merges,
    int FilesChanged,
    int Additions,
    int Deletions,
    List<string> Authors,
    string? CompareUrl,
    int? Scanned,
    List<CommitDto> Recent);

public sealed record CommitDto(string Sha, string Message, string? Author, string? AuthorAvatar, DateTimeOffset? DateUtc, string Url, bool IsMerge);

public sealed record WorkflowDto(long Id, string Name, string Path, string State, string Url, bool IsPublish, bool CanRun, List<WorkflowInputDto> Inputs, RunDto? LastRun, int? TypicalSeconds);

/// <summary><paramref name="Type"/>: string, boolean, choice, number, environment, or section (a title, not an input).</summary>
public sealed record WorkflowInputDto(string Name, string? Description, string Type, bool Required, string? Default, List<string> Options);

public sealed record RunsDto(RepositoryDto Repository, KeeperStatus Keeper, List<RunDto> Runs);

/// <summary>
/// <paramref name="Status"/> is GitHub's: queued, waiting, requested, pending, in_progress or completed (then
/// <paramref name="Conclusion"/> says how: success, failure, cancelled, skipped…). <paramref name="Jobs"/> and
/// <paramref name="Progress"/> come with unfinished runs only (and with a single run asked for by id).
/// </summary>
public sealed record RunDto(
    long Id,
    long WorkflowId,
    string Name,
    string Title,
    string Event,
    string Status,
    string? Conclusion,
    string Branch,
    string Sha,
    string? Actor,
    string? ActorAvatar,
    int RunNumber,
    int Attempt,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset UpdatedUtc,
    string Url,
    int? TypicalSeconds,
    ProgressDto? Progress,
    List<JobDto>? Jobs);

public sealed record ProgressDto(int JobsTotal, int JobsDone, int JobsRunning, int StepsTotal, int StepsDone, double Fraction, string? Current);

public sealed record JobDto(long Id, string Name, string Status, string? Conclusion, DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc, string Url, List<StepDto> Steps);

public sealed record StepDto(int Number, string Name, string Status, string? Conclusion, DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc);

public sealed record RunWorkflowRequest(Dictionary<string, string>? Inputs);

/// <summary>
/// <paramref name="State"/>: off, starting, idle (private, nothing running), running (public while runs go) or error.
/// </summary>
public sealed record KeeperStatus(string State, string Message, DateTimeOffset? CheckedUtc, string? LastChange, DateTimeOffset? LastChangeUtc);
