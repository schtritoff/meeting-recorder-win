using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeetingRecorder.Recording;

internal record MeetingSeries(string Folder, string Title)
{
    public static MeetingSeries Read(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(path)) throw new IOException("The selected folder does not exist.");
        var name = Path.GetFileName(path);
        if (name.StartsWith('~')) throw new IOException("Staging folders cannot be selected.");
        var parent = Directory.GetParent(path)?.FullName;
        if (SessionTime(name) is not null && parent is not null && HasTranscript(parent)) path = parent;
        if (Path.GetFileName(path).StartsWith('~')) throw new IOException("Staging folders cannot be selected.");
        if (!Directory.Exists(path) || !HasTranscript(path))
            throw new IOException("The selected folder has no transcript.");

        name = Path.GetFileName(path);
        var fallback = SessionTime(name.Length >= 15 ? name[..15] : name) is not null &&
            name.Length > 16 && name[15] == '_' ? name[16..] : name;
        var title = fallback;
        try
        {
            // New recordings only have <audio>-transcript.txt; the legacy transcript.md (wins when both legacy files exist) and transcript.txt are read for earlier folders.
            var audioTranscript = AudioTranscript(path);
            var markdown = Path.Combine(path, "transcript.md");
            var value = audioTranscript is not null ? TextTitle(audioTranscript)
                : File.Exists(markdown) ? MarkdownTitle(markdown) : TextTitle(Path.Combine(path, "transcript.txt"));
            if (!string.IsNullOrWhiteSpace(value)) title = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new MeetingSeries(path, title);
    }

    private static string? MarkdownTitle(string file)
    {
        using var reader = File.OpenText(file);
        if (reader.ReadLine() != "---") return null;
        string? line;
        string? value = null;
        while ((line = reader.ReadLine()) is not null && line != "---")
        {
            if (value is null && line.StartsWith("title:", StringComparison.Ordinal))
                value = line[6..].Trim();
        }
        if (line != "---" || value is null) return null;
        return value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value) : value;
    }

    // The first line is the title, or "Meeting <date time>" when the recording had none.
    private static string? TextTitle(string file)
    {
        using var reader = File.OpenText(file);
        var line = reader.ReadLine()?.Trim();
        return line is null || Regex.IsMatch(line, @"^Meeting \d{4}-\d{2}-\d{2} \d{2}:\d{2}$") ? null : line;
    }

    public static IReadOnlyList<MeetingSeries> Recent(string meetingsRoot, int limit = 8)
    {
        var entries = new List<(MeetingSeries Series, DateTime Time)>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(meetingsRoot))
            {
                try
                {
                    if (Path.GetFileName(folder).StartsWith('~')) continue;
                    var series = Read(folder);
                    if (!string.Equals(series.Folder, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(folder);
                    var time = (name.Length == 15 || name.Length > 15 && name[15] == '_')
                        ? SessionTime(name[..15]) ?? DateTime.MinValue : DateTime.MinValue;
                    try
                    {
                        foreach (var child in Directory.EnumerateDirectories(folder))
                        {
                            var childTime = SessionTime(Path.GetFileName(child));
                            if (childTime is not null && childTime > time && IsCompletedSession(child))
                                time = childTime.Value;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    entries.Add((series, time));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return entries.OrderByDescending(entry => entry.Time)
            .ThenBy(entry => Path.GetFileName(entry.Series.Folder), StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 0, 8)).Select(entry => entry.Series).ToArray();
    }

    public void CheckWritable()
    {
        var probe = Path.Combine(Folder, $".meeting-recorder-{Guid.NewGuid():N}.tmp");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        File.Delete(probe);
    }

    internal static string? Recording(string folder) => new[] { "recording.mp3", "recording.m4a" }
        .Select(name => Path.Combine(folder, name))
        .FirstOrDefault(path => { var file = new FileInfo(path); return file.Exists && file.Length > 0; });

    internal static bool HasRecording(string folder) => Recording(folder) is not null;

    // The transcript named after the recording; "recording" is also tried so a folder without audio yet still reads.
    internal static string? AudioTranscript(string folder)
    {
        var path = Path.Combine(folder, Processing.OutputFolder.TranscriptName(Recording(folder) ?? "recording.mp3"));
        return File.Exists(path) ? path : null;
    }

    // Folders recorded earlier have transcript.txt or transcript.md instead.
    internal static string? LegacyTranscript(string folder) => new[] { "transcript.txt", "transcript.md" }
        .Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    internal static bool HasTranscript(string folder) => AudioTranscript(folder) is not null || LegacyTranscript(folder) is not null;

    internal static bool IsCompletedSession(string folder) => SessionTime(Path.GetFileName(folder)) is not null
        && HasTranscript(folder) && HasRecording(folder);

    internal static DateTime? SessionTime(string name)
    {
        if (name.Length < 15 || !DateTime.TryParseExact(name[..15], "yyyy-MM-dd_HHmm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        if (name.Length == 15) return time;
        if (name[15] != '_' || name.Length == 16 ||
            !name[16..].All(c => c is >= '0' and <= '9') ||
            !BigInteger.TryParse(name[16..], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix < 2)
            return null;
        return time;
    }
}
