namespace MeetingRecorder.Recording;

internal sealed record NoteRow(Guid Id, long? OffsetMs, string Text);
internal sealed record NotesSnapshot
{
    public NotesSnapshot(int version, IReadOnlyList<NoteRow> rows)
    {
        Version = version;
        Rows = rows is null ? null! : Array.AsReadOnly(rows.ToArray());
    }

    public int Version { get; }
    public IReadOnlyList<NoteRow> Rows { get; }
}

internal sealed class NotesDraft
{
    private readonly List<NoteRow> rows;

    public NotesDraft(NotesSnapshot? snapshot = null)
    {
        if (snapshot is not null) NotesStore.Validate(snapshot);
        rows = snapshot?.Rows.ToList() ?? [];
    }

    public IReadOnlyList<NoteRow> Rows => rows.AsReadOnly();

    public Guid AddRow(Guid? after = null)
    {
        var index = after is null ? rows.Count : rows.FindIndex(row => row.Id == after.Value) + 1;
        if (index == 0 && after is not null) throw new ArgumentException("Row not found.", nameof(after));
        var row = new NoteRow(Guid.NewGuid(), null, "");
        rows.Insert(index, row);
        return row.Id;
    }

    public void SetText(Guid id, string text, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(text);
        var index = rows.FindIndex(row => row.Id == id);
        if (index < 0) throw new ArgumentException("Row not found.", nameof(id));
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        var row = rows[index];
        rows[index] = row with { Text = text, OffsetMs = row.OffsetMs ?? (text.Length > 0 ? (long)elapsed.TotalMilliseconds : null) };
    }

    public NotesSnapshot Snapshot() => new(1, Array.AsReadOnly(rows.ToArray()));
}
