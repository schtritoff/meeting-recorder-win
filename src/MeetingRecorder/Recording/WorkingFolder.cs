using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecorder.Audio;

namespace MeetingRecorder.Recording;

internal enum MeetingState { Recording, Processing, Failed }

internal record PreparedContinuation(string StagingFolder, int SessionNumber, bool HasNotes = false);

internal sealed record MeetingInfo(string? Title, DateTimeOffset StartedAt, DateTimeOffset? StoppedAt, long PausedMs, MeetingState State, string? Continues = null, PreparedContinuation? Prepared = null);

internal sealed class WorkingFolder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private WorkingFolder(string path, MeetingInfo info)
    {
        Path = path;
        Info = info;
    }

    public string Path { get; }
    public string MicWav => System.IO.Path.Combine(Path, "mic.wav");
    public string SystemWav => System.IO.Path.Combine(Path, "system.wav");
    public string MixedWav => System.IO.Path.Combine(Path, "mixed.wav");
    public MeetingInfo Info { get; private set; }

    public static WorkingFolder Create(string recordingsRoot, DateTimeOffset now)
    {
        var basePath = System.IO.Path.Combine(recordingsRoot, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var path = basePath;
        for (var suffix = 2; Directory.Exists(path) || File.Exists(path); suffix++)
            path = basePath + "-" + suffix.ToString(CultureInfo.InvariantCulture);
        Directory.CreateDirectory(path);
        var folder = new WorkingFolder(path, new MeetingInfo(null, now, null, 0, MeetingState.Recording));
        folder.Save(folder.Info);
        return folder;
    }

    public void Save(MeetingInfo info)
    {
        var final = System.IO.Path.Combine(Path, "meeting.json");
        var temporary = final + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, info, JsonOptions);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, final, overwrite: true);
        Info = info;
    }

    public static IReadOnlyList<WorkingFolder> Scan(string recordingsRoot)
    {
        if (!Directory.Exists(recordingsRoot)) return [];
        var folders = new List<WorkingFolder>();
        string[] paths;
        try { paths = Directory.GetDirectories(recordingsRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryLog($"Could not scan recordings in {recordingsRoot}: {ex.Message}");
            return folders;
        }
        foreach (var path in paths)
        {
            if (File.Exists(System.IO.Path.Combine(path, "completed"))) continue;
            try
            {
                var info = JsonSerializer.Deserialize<MeetingInfo>(File.ReadAllText(System.IO.Path.Combine(path, "meeting.json")), JsonOptions);
                if (info is null || info.StartedAt == default) throw new JsonException("Meeting state has no valid start time.");
                folders.Add(new WorkingFolder(path, info));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                TryLog($"Unreadable meeting state in {path}: {ex.Message}");
                try
                {
                    if (!Directory.EnumerateFiles(path, "*.wav").Any()) continue;
                }
                catch (Exception audioError) when (audioError is IOException or UnauthorizedAccessException)
                {
                    TryLog($"Could not inspect audio in {path}; retaining recovery entry: {audioError.Message}");
                }
                var name = System.IO.Path.GetFileName(path);
                var started = DateTimeOffset.Now;
                if (name.Length >= 15 && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timestamp)) started = new DateTimeOffset(timestamp);
                else
                {
                    try { started = Directory.GetCreationTimeUtc(path); }
                    catch (Exception timeError) when (timeError is IOException or UnauthorizedAccessException)
                    { TryLog($"Could not read creation time in {path}: {timeError.Message}"); }
                }
                folders.Add(new WorkingFolder(path, new MeetingInfo(null, started, null, 0, MeetingState.Failed)));
            }
        }
        return folders.OrderBy(folder => folder.Info.StartedAt).ToArray();
    }

    public TimeSpan RecordedDuration()
    {
        long samples = 0;
        foreach (var track in new[] { MicWav, SystemWav })
        {
            if (!File.Exists(track)) continue;
            try
            {
                samples = Math.Max(samples, WavRepair.ReadRecordedSamples(track));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
            {
                TryLog($"Could not read recording duration {track}: {ex.Message}");
            }
        }
        return TimeSpan.FromSeconds(samples / 16000d);
    }

    public DateTimeOffset EstimatedEnd() => Info.StoppedAt ?? Info.StartedAt + RecordedDuration() + TimeSpan.FromMilliseconds(Info.PausedMs);

    public void MarkCompleted()
    {
        using var marker = new FileStream(System.IO.Path.Combine(Path, "completed"), FileMode.Create, FileAccess.Write, FileShare.None);
        marker.WriteByte(1);
        marker.Flush(flushToDisk: true);
    }

    public void DeleteCompleted()
    {
        // Keep the marker until all audio/state is gone, even if recursive cleanup partly fails.
        foreach (var file in Directory.GetFiles(Path))
            if (System.IO.Path.GetFileName(file) != "completed") File.Delete(file);
        foreach (var directory in Directory.GetDirectories(Path)) Directory.Delete(directory, recursive: true);
        File.Delete(System.IO.Path.Combine(Path, "completed"));
        Directory.Delete(Path);
    }

    private static void TryLog(string message)
    {
        try { Log.Info(message); }
        catch (Exception) { /* Recovery must still work when logging is unavailable. */ }
    }

    public void Discard()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
