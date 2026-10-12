using System.Globalization;
using System.Text.RegularExpressions;

namespace Phantom.Services;

public record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// MediaElement has no subtitle support, so the player overlays cues itself.
/// Parses SRT and WebVTT into a list of timed cues.
/// </summary>
public static partial class SubtitleParser
{
    [GeneratedRegex(@"(?<s>(\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})\s*-->\s*(?<e>(\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]+\}")]
    private static partial Regex TagRegex();

    public static IReadOnlyList<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        var blocks = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timingIndex = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timingIndex < 0) continue;
            var m = TimingRegex().Match(lines[timingIndex]);
            if (!m.Success) continue;
            var body = string.Join('\n', lines.Skip(timingIndex + 1)).Trim();
            if (body.Length == 0) continue;
            cues.Add(new SubtitleCue(ParseTime(m.Groups["s"].Value), ParseTime(m.Groups["e"].Value), TagRegex().Replace(body, "")));
        }
        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    private static TimeSpan ParseTime(string s)
    {
        s = s.Replace(',', '.');
        if (s.Count(c => c == ':') == 1) s = "00:" + s;
        var parts = s.Split(':');
        var hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var seconds = double.Parse(parts[2], CultureInfo.InvariantCulture);
        return new TimeSpan(0, hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Binary search for the cue showing at <paramref name="position"/>.</summary>
    public static SubtitleCue? CueAt(IReadOnlyList<SubtitleCue> cues, TimeSpan position)
    {
        int lo = 0, hi = cues.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cues[mid].Start <= position) lo = mid + 1;
            else hi = mid - 1;
        }
        // hi is the last cue starting at or before position; overlapping cues are rare, check a few back.
        for (var i = hi; i >= 0 && i > hi - 3; i--)
            if (cues[i].End >= position) return cues[i];
        return null;
    }
}
