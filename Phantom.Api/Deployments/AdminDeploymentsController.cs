using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phantom.Shared;

namespace Phantom.Api.Deployments;

/// <summary>
/// Manage server's Deployments tab in the app: what's waiting to be published, and the repository's GitHub Actions,
/// run, cancelled and re-run by an admin. Running anything makes the repository public first, and
/// <see cref="RepoVisibilityKeeper"/> makes it private again when the last run has finished.
/// </summary>
[ApiController]
[Route("api/admin/deployments")]
[Authorize(Roles = "Admin")]
public class AdminDeploymentsController(DeploymentsService deployments, GitHubActions github, RepoVisibilityKeeper keeper, ILogger<AdminDeploymentsController> log) : ControllerBase
{
    [HttpGet]
    public Task<ActionResult<DeploymentsOverviewDto>> Overview(CancellationToken ct) => GitHub(() => deployments.OverviewAsync(ct));

    [HttpGet("runs")]
    public Task<ActionResult<RunsDto>> Runs([FromQuery] int count = 20, CancellationToken ct = default) =>
        GitHub(() => deployments.RunsAsync(Math.Clamp(count, 1, 50), ct));

    [HttpGet("runs/{id:long}")]
    public Task<ActionResult<RunDto>> Run(long id, CancellationToken ct) => GitHub(() => deployments.RunAsync(id, ct));

    /// <summary>Starts a workflow on the default branch. Inputs are strings, as GitHub takes them ("true"/"false" for a boolean).</summary>
    [HttpPost("workflows/{id:long}/run")]
    public Task<ActionResult<bool>> RunWorkflow(long id, RunWorkflowRequest request, CancellationToken ct) => GitHub(async () =>
    {
        var repo = await github.RepositoryAsync(ct);
        var branch = repo.GetProperty("default_branch").GetString() ?? "main";
        var inputs = await deployments.CurrentInputsAsync(id, branch, request.Inputs ?? [], ct);
        await keeper.OpenAsync("start a run", () => github.DispatchAsync(id, branch, inputs, ct), ct);
        log.LogInformation("Admin {Admin} ran workflow {Workflow} on {Branch}", User.Identity?.Name, id, branch);
        return true;
    });

    [HttpPost("runs/{id:long}/cancel")]
    public Task<ActionResult<bool>> Cancel(long id, CancellationToken ct) => GitHub(async () =>
    {
        await github.CancelAsync(id, ct);
        log.LogInformation("Admin {Admin} cancelled run {Run}", User.Identity?.Name, id);
        return true;
    });

    /// <summary>Runs a finished run again: only its failed jobs when it failed, all of it otherwise.</summary>
    [HttpPost("runs/{id:long}/rerun")]
    public Task<ActionResult<bool>> Rerun(long id, CancellationToken ct) => GitHub(async () =>
    {
        var run = await github.RunAsync(id, ct);
        var failedOnly = run.GetProperty("conclusion").GetString() is "failure" or "timed_out";
        await keeper.OpenAsync("re-run a run", () => github.RerunAsync(id, failedOnly, ct), ct);
        log.LogInformation("Admin {Admin} re-ran run {Run} (failed jobs only: {FailedOnly})", User.Identity?.Name, id, failedOnly);
        return true;
    });

    /// <summary>GitHub's refusals and failures become a problem the admin can read.</summary>
    private async Task<ActionResult<T>> GitHub<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (GitHubException e)
        {
            return Problem(detail: e.Message, statusCode: e.Status is 503 or 422 ? e.Status : StatusCodes.Status502BadGateway);
        }
    }
}
