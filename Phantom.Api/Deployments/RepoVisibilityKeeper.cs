using Phantom.Shared;

namespace Phantom.Api.Deployments;

/// <summary>
/// Keeps the repository private except while its workflows run (see <see cref="DeploymentsOptions.AutoPrivate"/>):
/// <see cref="OpenAsync"/> makes it public before the admin starts a run, and every <see cref="Interval"/> it's made
/// private again once no run at all is queued, waiting or running - so a run that's still going keeps it public
/// however many others have finished. A run takes a few seconds to show up after it's started, so it waits
/// <see cref="Grace"/> after the admin's last action first. Nothing is kept in memory that matters: after a restart
/// (e.g. the API redeployed by one of these runs) it simply carries on checking.
/// </summary>
public sealed class RepoVisibilityKeeper(GitHubActions github, TimeProvider time, ILogger<RepoVisibilityKeeper> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastActionUtc = DateTimeOffset.MinValue;

    /// <summary>What the keeper last saw and did, for the admin.</summary>
    public KeeperStatus Status { get; private set; } = new("starting", "Checking the repository…", null, null, null);

    /// <summary>
    /// Makes the repository public (when <see cref="DeploymentsOptions.AutoPrivate"/> is on), then runs
    /// <paramref name="action"/> - starting or re-running a workflow - before the keeper can make it private again.
    /// </summary>
    public async Task OpenAsync(string why, Func<Task> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _lastActionUtc = time.GetUtcNow();
            if (github.Options.AutoPrivate)
            {
                var repo = await github.RepositoryAsync(ct);
                if (repo.GetProperty("private").GetBoolean())
                {
                    await github.SetPrivateAsync(false, ct);
                    log.LogInformation("Made {Repository} public to {Why}", github.Options.Repository, why);
                    Status = Status with { State = "running", Message = $"Made public to {why}.", LastChange = $"Made public to {why}", LastChangeUtc = time.GetUtcNow() };
                }
            }
            await action();
            _lastActionUtc = time.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!github.Options.IsConfigured)
        {
            Status = new("off", "Not set up: no Deployments__GitHubToken or Deployments__Repository.", null, null, null);
            return;
        }
        if (!github.Options.AutoPrivate)
        {
            Status = new("off", "Automatic private is off (Deployments__AutoPrivate=false).", null, null, null);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var repo = await github.RepositoryAsync(ct);
            if (repo.GetProperty("private").GetBoolean())
            {
                Status = Status with { State = "idle", Message = "Private. It's made public when something is run from here.", CheckedUtc = now };
                return;
            }

            var active = await github.ActiveRunsAsync(ct);
            if (active.Count > 0)
            {
                Status = Status with
                {
                    State = "running",
                    Message = active.Count == 1 ? "Public while 1 run is going; private once it's finished." : $"Public while {active.Count} runs are going; private once the last one has finished.",
                    CheckedUtc = now,
                };
                return;
            }
            if (now - _lastActionUtc < Grace)
            {
                Status = Status with { State = "running", Message = "Public: waiting for the run that was just started to show up.", CheckedUtc = now };
                return;
            }

            await github.SetPrivateAsync(true, ct);
            log.LogInformation("Made {Repository} private: no workflow runs left", github.Options.Repository);
            Status = new("idle", "Private. It's made public when something is run from here.", now, "Made private: every run had finished", now);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Couldn't check {Repository}'s visibility: {Error}", github.Options.Repository, e.Message);
            Status = Status with { State = "error", Message = $"Couldn't check the repository: {e.Message}", CheckedUtc = time.GetUtcNow() };
        }
        finally
        {
            _gate.Release();
        }
    }
}
