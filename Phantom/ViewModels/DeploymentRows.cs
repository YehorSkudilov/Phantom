using CommunityToolkit.Mvvm.ComponentModel;
using Phantom.Shared;

namespace Phantom.ViewModels;

// What Manage server's Deployments tab shows, worked out from the server's Deployments DTOs (Phantom.Shared/Deployments.cs)
// so the page only binds.

/// <summary>A workflow run: its state, how far along it is and, while it runs, its jobs.</summary>
public record DeploymentRunRow(
    long Id,
    string Url,
    string Title,
    string Detail,
    string Status,
    Color StatusColor,
    bool IsRunning,
    bool CanRerun,
    double Progress,
    string ProgressText,
    IReadOnlyList<DeploymentJobRow> Jobs)
{
    public static DeploymentRunRow From(RunDto run, DateTimeOffset now)
    {
        var running = run.Status != "completed";
        var started = run.StartedUtc ?? run.CreatedUtc;
        var elapsed = Math.Max(0, (now - started).TotalSeconds);
        var detail = $"{run.Title} · {Deploy.Words(run.Event)}{(run.Actor is { } a ? $" by {a}" : "")} · {Deploy.Ago(run.CreatedUtc, now)}";
        if (!running && run.StartedUtc is { } s && run.UpdatedUtc > s) detail += $" · took {Deploy.Duration((run.UpdatedUtc - s).TotalSeconds)}";

        // By time when the workflow usually takes about the same, else by the jobs and steps done.
        var progress = 0.0;
        var text = "";
        if (running)
        {
            var byTime = run.TypicalSeconds is { } typical ? Math.Min(0.97, elapsed / typical) : 0;
            progress = run.Status == "in_progress" ? Math.Max(byTime, run.Progress?.Fraction ?? 0) : 0;
            var parts = new List<string> { $"{Math.Round(progress * 100)}%", $"{Deploy.Clock(elapsed)} elapsed" };
            if (run.TypicalSeconds is { } t)
                parts.Add(t > elapsed ? $"about {Deploy.Duration(t - elapsed)} left" : "taking longer than usual");
            if (run.Progress is { JobsTotal: > 0 } p)
                parts.Add($"jobs {p.JobsDone}/{p.JobsTotal} · steps {p.StepsDone}/{p.StepsTotal}");
            text = string.Join("  ·  ", parts);
        }

        var (status, color) = Outcome(run);
        return new DeploymentRunRow(
            run.Id,
            run.Url,
            $"{run.Name} #{run.RunNumber}{(run.Attempt > 1 ? $" (attempt {run.Attempt})" : "")}",
            detail,
            status,
            color,
            running,
            !running && run.Conclusion is "failure" or "timed_out" or "cancelled",
            progress,
            text,
            run.Jobs?.Select(j => DeploymentJobRow.From(j, now)).ToList() ?? []);
    }

    private static (string, Color) Outcome(RunDto run) => run.Status switch
    {
        "in_progress" => ("Running", Deploy.Color("PrimaryLight")),
        "waiting" => ("Waiting for approval", Deploy.Color("Warning")),
        not "completed" => ("Queued", Deploy.Color("TextSecondary")),
        _ => run.Conclusion switch
        {
            "success" => ("Succeeded", Deploy.Color("Success")),
            "failure" or "timed_out" or "startup_failure" => ("Failed", Deploy.Color("Danger")),
            "cancelled" => ("Cancelled", Deploy.Color("TextSecondary")),
            "skipped" => ("Skipped", Deploy.Color("TextSecondary")),
            var other => (Deploy.Words(other ?? "done"), Deploy.Color("TextSecondary")),
        },
    };
}

/// <summary>One of a run's jobs: done, running (on which step) or waiting.</summary>
public record DeploymentJobRow(string Icon, Color IconColor, string Name, string Detail, double Progress, bool IsRunning, string Url)
{
    public static DeploymentJobRow From(JobDto job, DateTimeOffset now)
    {
        var done = job.Steps.Count(s => s.Status == "completed");
        var step = job.Steps.FirstOrDefault(s => s.Status == "in_progress");
        var running = job.Status == "in_progress";
        var parts = new List<string>();
        if (running && step is not null) parts.Add(step.Name);
        if (job.Steps.Count > 0) parts.Add($"step {Math.Min(done + (step is null ? 0 : 1), job.Steps.Count)} of {job.Steps.Count}");
        if (job.StartedUtc is { } started) parts.Add(Deploy.Clock(((job.CompletedUtc ?? now) - started).TotalSeconds));
        if (!running && job.Status != "completed") parts.Add("waiting");

        var (icon, color) = job switch
        {
            { Status: "in_progress" } => ("●", Deploy.Color("PrimaryLight")),
            { Status: not "completed" } => ("○", Deploy.Color("TextSecondary")),
            { Conclusion: "success" } => ("✓", Deploy.Color("Success")),
            { Conclusion: "skipped" } => ("–", Deploy.Color("TextSecondary")),
            { Conclusion: "cancelled" } => ("⊘", Deploy.Color("TextSecondary")),
            _ => ("✕", Deploy.Color("Danger")),
        };
        return new DeploymentJobRow(icon, color, job.Name, string.Join(" · ", parts),
            job.Steps.Count == 0 ? 0 : (double)done / job.Steps.Count, running && job.Steps.Count > 0, job.Url);
    }
}

/// <summary>A commit waiting to be published, or (<see cref="IsRelease"/>) the version bump commit before them.</summary>
public record DeploymentCommitRow(string Message, string Detail, bool IsMerge, bool IsRelease, string Url)
{
    public double Opacity => IsMerge ? 0.6 : 1;

    public static DeploymentCommitRow From(CommitDto c, bool release, DateTimeOffset now) => new(
        c.Message.Length > 0 ? c.Message : "(no message)",
        $"{c.Author ?? "unknown"} · {Deploy.Ago(c.DateUtc, now)} · {c.Sha[..Math.Min(7, c.Sha.Length)]}{(c.IsMerge ? " · merge" : "")}",
        c.IsMerge,
        release,
        c.Url);
}

/// <summary>
/// One of a workflow's options, as GitHub's Run workflow form has it. Values are strings, as GitHub takes them; a
/// "section" is a title, not an option.
/// </summary>
public partial class WorkflowInputRow : ObservableObject
{
    public WorkflowInputRow(WorkflowInputDto input)
    {
        Name = input.Name;
        Type = input.Type;
        Required = input.Required;
        Options = input.Options;
        Label = (input.Description is { Length: > 0 } d ? d : input.Name) + (input.Required && input.Type is not ("boolean" or "section") ? " *" : "");
        Note = input.Type == "section" ? input.Options.FirstOrDefault() ?? "" : "";
        value = input.Type == "boolean" ? (input.Default == "true" ? "true" : "false")
            : input.Default ?? (input.Type == "choice" ? input.Options.FirstOrDefault() ?? "" : "");
    }

    public string Name { get; }
    public string Type { get; }
    public bool Required { get; }
    public string Label { get; }
    public string Note { get; }
    public IReadOnlyList<string> Options { get; }

    public bool IsSection => Type == "section";
    public bool IsBoolean => Type == "boolean";
    public bool IsChoice => Type == "choice";
    public bool IsText => !IsSection && !IsBoolean && !IsChoice;
    public Keyboard Keyboard => Type == "number" ? Keyboard.Numeric : Keyboard.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    private string value;

    public bool IsChecked
    {
        get => Value == "true";
        set => Value = value ? "true" : "false";
    }
}

/// <summary>How the Deployments tab words times, and its colours.</summary>
internal static class Deploy
{
    public static string Ago(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is not { } v) return "never";
        var minutes = (now - v).TotalMinutes;
        if (minutes < 2) return "just now";
        if (minutes < 60) return $"{(int)minutes} min ago";
        if (minutes < 60 * 24) return $"{(int)(minutes / 60)} h ago";
        if (minutes < 60 * 24 * 30) return $"{(int)(minutes / 60 / 24)} d ago";
        return v.ToLocalTime().ToString("d MMM yyyy");
    }

    /// <summary>Seconds as "12:05".</summary>
    public static string Clock(double seconds)
    {
        var s = (int)Math.Max(0, seconds);
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"45 s", "12 min", "1 h 5 min".</summary>
    public static string Duration(double seconds)
    {
        var s = (int)Math.Round(Math.Max(0, seconds));
        if (s < 60) return $"{s} s";
        var minutes = (int)Math.Round(s / 60.0);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h" + (minutes % 60 > 0 ? $" {minutes % 60} min" : "");
    }

    /// <summary>"workflow_dispatch" → "Workflow dispatch".</summary>
    public static string Words(string value)
    {
        var spaced = value.Replace('_', ' ').ToLowerInvariant();
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    public static Color Color(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color ? color : Colors.Gray;
}
