using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using MeetingRecorder.Recording;

namespace MeetingRecorder.Processing;

internal static class SeriesRecordWriter
{
    public const string FileName = "combined-all-meetings.txt";

    public static void Write(string seriesFolder)
    {
        var children = Directory.EnumerateDirectories(seriesFolder)
            .Where(path => MeetingSeries.SessionTime(Path.GetFileName(path)) is not null)
            .Where(path => new DirectoryInfo(path).ResolveLinkTarget(false) is null)
            .Where(CompletedSession)
            .OrderBy(path => MeetingSeries.SessionTime(Path.GetFileName(path)))
            .ThenBy(path => Suffix(Path.GetFileName(path)))
            .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        var temp = Path.Combine(seriesFolder, $".series-record-{Guid.NewGuid():N}.tmp");
        var owned = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                var sessions = new[] { seriesFolder }.Concat(children);
                var number = 0;
                foreach (var folder in sessions)
                {
                    var source = Source(folder);
                    var content = File.ReadAllText(source);
                    writer.WriteLine($"=== Session {++number}: {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))} ===");
                    if (Path.GetExtension(source).Equals(".md", StringComparison.OrdinalIgnoreCase)) writer.WriteLine("Legacy Markdown:");
                    writer.WriteLine(content);
                    writer.WriteLine();
                }
            }
            File.Move(temp, Path.Combine(seriesFolder, FileName), overwrite: true);
            owned = false;
        }
        finally
        {
            if (owned) File.Delete(temp);
        }
    }

    private static BigInteger Suffix(string name) => name.Length == 15 ? BigInteger.One :
        BigInteger.Parse(name[16..], CultureInfo.InvariantCulture);

    private static bool Exists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("Expected a file: " + path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string? Recording(string folder)
    {
        foreach (var name in new[] { "recording.mp3", "recording.m4a" })
        {
            var path = Path.Combine(folder, name);
            if (Exists(path) && new FileInfo(path).Length > 0) return path;
        }
        return null;
    }

    private static bool CompletedSession(string folder)
    {
        var audio = Recording(folder);
        if (audio is null) return false;
        return new[] { OutputFolder.TranscriptName(audio), "transcript.txt", "transcript.md" }
            .Any(name => Exists(Path.Combine(folder, name)));
    }

    private static string Source(string folder)
    {
        var audio = Recording(folder) ?? "recording.mp3";
        foreach (var name in new[] { "combined.txt", OutputFolder.TranscriptName(audio), "transcript.txt", "transcript.md" })
        {
            var path = Path.Combine(folder, name);
            if (Exists(path)) return path;
        }
        throw new IOException("The meeting has no transcript.");
    }
}
