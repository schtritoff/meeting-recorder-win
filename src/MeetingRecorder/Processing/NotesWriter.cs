using MeetingRecorder.Recording;

namespace MeetingRecorder.Processing;

internal static class NotesWriter
{
    public static string BuildNotes(string? title, DateTimeOffset start, DateTimeOffset end,
        TimeSpan duration, IReadOnlyList<NoteRow> rows, int? session = null) =>
        BuildCombined(title, start, end, duration, [], rows, session);

    public static string BuildCombined(string? title, DateTimeOffset start, DateTimeOffset end,
        TimeSpan duration, IReadOnlyList<Segment> segments, IReadOnlyList<NoteRow> rows, int? session = null)
    {
        var lines = TranscriptWriter.TextHeader(title, start, end, duration, session);
        var entries = segments.Select(segment => (Offset: segment.Start, Kind: 0, Text: segment.Text))
            .Concat(rows.Where(row => !string.IsNullOrWhiteSpace(row.Text))
                .Select(row => (Offset: TimeSpan.FromMilliseconds(row.OffsetMs!.Value), Kind: 1, row.Text)))
            .OrderBy(entry => entry.Offset).ThenBy(entry => entry.Kind);
        foreach (var entry in entries)
            lines.Add($"[{TranscriptWriter.Timestamp(entry.Offset)}] {(entry.Kind == 0 ? "Transcript" : "Note")}: "
                + entry.Text.ReplaceLineEndings("\r\n"));
        return string.Join("\r\n", lines) + "\r\n";
    }
}
