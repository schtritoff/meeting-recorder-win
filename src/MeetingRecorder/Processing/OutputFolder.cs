using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MeetingRecorder.Recording;

namespace MeetingRecorder.Processing;

internal static class OutputFolder
{
    public static string Name(DateTimeOffset start, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) title = "";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string((title ?? "").Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim(' ', '.');
        if (sanitized.Length > 80)
        {
            var boundary = StringInfo.ParseCombiningCharacters(sanitized).Last(index => index <= 80);
            sanitized = sanitized[..boundary].TrimEnd(' ', '.');
        }
        return start.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture)
            + (sanitized.Length == 0 ? "" : "_" + sanitized);
    }

    public static string CreateUnique(string parent, string name)
    {
        var candidate = UniquePath(parent, name);
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    public static string TranscriptName(string audioPath) =>
        Path.GetFileNameWithoutExtension(audioPath) + "-transcript.txt";

    public static string NotesName(string audioPath) => Path.GetFileNameWithoutExtension(audioPath) + "-notes.txt";

    public static string NotesPath(string folder) => NotesPath(folder, File.GetAttributes);

    internal static string NotesPath(string folder, Func<string, FileAttributes> attributes)
    {
        var matched = Path.Combine(folder, NotesName(MeetingSeries.Recording(folder) ?? "recording.mp3"));
        try { attributes(matched); return matched; }
        catch (FileNotFoundException) { return Path.Combine(folder, "notes.txt"); }
        catch (DirectoryNotFoundException) { return Path.Combine(folder, "notes.txt"); }
    }

    public static string TranscriptToOpen(string sessionFolder)
    {
        var combined = Path.Combine(sessionFolder, "combined.txt");
        if (File.Exists(combined)) return combined;
        return MeetingSeries.AudioTranscript(sessionFolder) ?? Path.Combine(sessionFolder, "transcript.txt");
    }

    public static string UniquePath(string parent, string name)
    {
        var path = Path.GetFullPath(Path.Combine(parent, name));
        var candidate = path;
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = path + "_" + suffix.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }
}
