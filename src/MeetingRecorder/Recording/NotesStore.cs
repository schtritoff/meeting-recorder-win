using System.IO;
using System.Text.Json;

namespace MeetingRecorder.Recording;

internal static class NotesStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Save(string workingFolder, NotesSnapshot snapshot)
    {
        Validate(snapshot);
        var final = Path.Combine(workingFolder, "notes.json");
        var temporary = final + ".tmp";
        // Do not replace an authoritative file that cannot be read or validated.
        if (ReadIfPresent(final) is null && ReadIfPresent(temporary) is not null)
            File.Move(temporary, final);
        // Write to a unique file first: a failed first serialization must not leave a partial canonical
        // temp that strict validation would then block on every future retry.
        var unique = Path.Combine(workingFolder, "notes.json." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(unique, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, snapshot, Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(unique, temporary, overwrite: true);
        }
        catch
        {
            try { File.Delete(unique); } catch (IOException) { /* Keep the last good snapshot authoritative. */ }
            throw;
        }
        File.Move(temporary, final, overwrite: true);
    }

    public static NotesSnapshot? Load(string workingFolder)
    {
        var final = Path.Combine(workingFolder, "notes.json");
        return ReadIfPresent(final) ?? ReadIfPresent(final + ".tmp");
    }

    private static NotesSnapshot? ReadIfPresent(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var snapshot = JsonSerializer.Deserialize<NotesSnapshot>(file, Options);
            Validate(snapshot);
            return snapshot;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void Validate(NotesSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Version != 1 || snapshot.Rows is null)
            throw new InvalidDataException("Invalid notes snapshot.");
        var ids = new HashSet<Guid>();
        foreach (var row in snapshot.Rows)
            if (row is null || row.Id == Guid.Empty || !ids.Add(row.Id) || row.Text is null ||
                row.OffsetMs < 0 || (row.Text.Length > 0 && row.OffsetMs is null))
                throw new InvalidDataException("Invalid note row.");
    }
}
