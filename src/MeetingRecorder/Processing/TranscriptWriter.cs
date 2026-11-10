using System.Globalization;
using System.Text.RegularExpressions;

namespace MeetingRecorder.Processing;

internal static class TranscriptWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Plain text that opens cleanly in Notepad. The first line is the title, which MeetingSeries reads back.
    public static string BuildText(string? title, DateTimeOffset start, DateTimeOffset end,
        TimeSpan duration, IReadOnlyList<Segment> segments, int? session = null)
    {
        var lines = TextHeader(title, start, end, duration, session);
        lines.AddRange(Body(segments, "No speech detected."));
        return string.Join("\r\n", lines) + "\r\n";
    }

    internal static List<string> TextHeader(string? title, DateTimeOffset start, DateTimeOffset end,
        TimeSpan duration, int? session = null)
    {
        title = CleanTitle(title);
        var lines = new List<string>
        {
            Heading(title, start),
            $"Date: {start.ToString("yyyy-MM-dd, HH:mm", Invariant)}-{end.ToString("HH:mm", Invariant)} ({Minutes(duration)} min)",
            ""
        };
        if (session is not null) lines.Insert(2, "Session: " + session.Value.ToString(Invariant));
        return lines;
    }

    internal static string Timestamp(TimeSpan offset) => offset.TotalHours >= 1
        ? ((int)offset.TotalHours).ToString("00", Invariant) + ":" + offset.ToString(@"mm\:ss", Invariant)
        : offset.ToString(@"mm\:ss", Invariant);

    private static string CleanTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "" : title.Replace('\r', ' ').Replace('\n', ' ');

    private static string Heading(string title, DateTimeOffset start) =>
        title.Length == 0 ? "Meeting " + start.ToString("yyyy-MM-dd HH:mm", Invariant) : title;

    private static string Minutes(TimeSpan duration) =>
        Math.Max(1, (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero)).ToString(Invariant);

    private static IEnumerable<string> Body(IReadOnlyList<Segment> segments, string noSpeech)
    {
        if (segments.Count == 0)
        {
            yield return noSpeech;
            yield break;
        }
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) yield return "";
            yield return $"[{Timestamp(segments[i].Start)}] " + Regex.Replace(segments[i].Text.Trim(), @"\s*[\r\n]+\s*", " ");
        }
    }
}
