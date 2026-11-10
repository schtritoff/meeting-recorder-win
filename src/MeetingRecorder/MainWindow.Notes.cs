using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingRecorder.Processing;
using MeetingRecorder.Recording;

namespace MeetingRecorder;

// Right-hand notes pane, row editor and autosave. The constructor calls InitializeNotes; the
// recording lifecycle (Start/Stop/Pause/Retry/New/closing/compact/processing) hooks in MainWindow.*.cs.
public partial class MainWindow
{
    private const double NotesPaneWidth = 400; // Initial width only; the notes column then stretches with the window.
    private const double NotesLeftGrowth = 120; // Part of that growth taken from the window's left edge.
    private const double NotesMinWidth = 200;
    private const double ControlsWidth = 420; // The control column keeps its normal width while notes are open.

    private readonly DispatcherTimer notesDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer notesDeadline = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<NoteRowView> noteViews = [];
    private NotesDraft notesDraft = new();
    private WorkingFolder? notesFolder;
    private bool notesDirty;
    private bool notesLocked = true;
    private Exception? notesFlushError; // Last save failure, surfaced through the processing error path on Stop.
    private bool notesPaneVisible;
    private bool notesRendering;
    private double notesWidthBefore, notesLeftShift, notesMinWidthBefore;

    private void InitializeNotes()
    {
        NotesButton.Click += (_, _) =>
        {
            var show = NotesPane.Visibility != Visibility.Visible;
            SetNotesPaneVisible(show);
            NotesButton.IsChecked = show;
        };
        notesDebounce.Tick += (_, _) => NotesFlushTimerTick();
        notesDeadline.Tick += (_, _) => NotesFlushTimerTick();
        NotesWarning.Visibility = Visibility.Collapsed;
    }

    private void SetNotesPaneVisible(bool visible)
    {
        if (notesPaneVisible == visible) return;
        notesPaneVisible = visible;
        NotesPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var area = CurrentWorkArea();
        if (visible)
        {
            // Freeze the control column; every DIP added to the window afterwards goes to the star-sized notes column.
            var content = Content is FrameworkElement { ActualWidth: > 0 } root ? root.ActualWidth : Width;
            var controls = Math.Min(content, ControlsWidth);
            var (widthBefore, leftBefore) = (Width, Left);
            notesWidthBefore = widthBefore;
            notesMinWidthBefore = MinWidth;
            MainColumn.Width = new GridLength(controls);
            NotesColumn.Width = new GridLength(1, GridUnitType.Star);
            var minWidth = Math.Max(MinWidth, Width - content + controls + NotesMinWidth);
            var target = Math.Clamp(Width + NotesPaneWidth, minWidth, Math.Max(minWidth, area.Width));
            MinWidth = minWidth;
            Width = target;
            Left = UiMath.ClampPosition(Left - Math.Min(NotesLeftGrowth, Width - widthBefore), area.Left, area.Width, Width);
            notesLeftShift = double.IsFinite(leftBefore) ? leftBefore - Left : 0;
        }
        else
        {
            MainColumn.Width = new GridLength(1, GridUnitType.Star);
            NotesColumn.Width = GridLength.Auto;
            MinWidth = notesMinWidthBefore;
            Width = Math.Clamp(notesWidthBefore, MinWidth, Math.Max(MinWidth, area.Width));
            Left = UiMath.ClampPosition(Left + notesLeftShift, area.Left, area.Width, Width);
        }
        Top = UiMath.ClampPosition(Top, area.Top, area.Height, Height);
    }

    // Same monitor enumeration as StripWindow, but in WPF DIPs like Left/Top/Width: with an HWND use
    // the window's device transform, before that scale each monitor's physical pixels by its own DPI
    // (WPF on .NET is per-monitor v2 aware, so GetMonitorInfo pixels are not DIPs at scaled displays).
    private Rect CurrentWorkArea()
    {
        Matrix? fromDevice = null;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) fromDevice = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformFromDevice;
        var areas = new List<Rect>();
        MonitorEnum callback = (nint monitor, nint dc, ref NativeRect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                Rect area;
                if (fromDevice is { } transform)
                    area = new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)),
                        transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
                else
                    area = UiMath.PhysicalRectToDips(
                        new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
                        MonitorScale(monitor));
                if ((info.Flags & 1) != 0) areas.Insert(0, area); else areas.Add(area);
            }
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        if (areas.Count == 0) return SystemParameters.WorkArea; // Already DIPs.
        var center = new Point(Left + Width / 2, Top + Height / 2);
        return areas.FirstOrDefault(area => area.Contains(center), areas[0]);
    }

    private static double MonitorScale(nint monitor)
    {
        if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        var system = GetDpiForSystem();
        return system > 0 ? system / 96.0 : 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorEnum(nint monitor, nint dc, ref NativeRect bounds, nint data);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private void BeginNotes(WorkingFolder folder)
    {
        if (notesDirty) throw new InvalidOperationException("Unsaved notes must be flushed before starting new notes.");
        notesDraft = new NotesDraft();
        notesFolder = folder;
        notesDirty = false;
        notesLocked = false;
        StopNotesTimers();
        ClearNotesWarning();
        RenderNotes();
        AddNoteView(notesDraft.AddRow(), 0, readOnly: false);
        RenumberNoteNames();
    }

    private void LockNotes()
    {
        notesLocked = true;
        foreach (var view in noteViews) view.Box.IsReadOnly = true;
    }

    private void ResetNotes()
    {
        if (notesDirty) throw new InvalidOperationException("Unsaved notes must be flushed before reset.");
        StopNotesTimers();
        notesDraft = new NotesDraft();
        notesFolder = null;
        notesLocked = true;
        ClearNotesWarning();
        RenderNotes(); // Clears rows and timestamps; pane visibility is preserved.
    }

    private void FlushNotes()
    {
        if (notesFolder is null || !notesDirty) return;
        foreach (var view in noteViews) // The unfinished current row is flushed too.
            notesDraft.SetText(view.Id, view.Box.Text, recorder?.Elapsed ?? TimeSpan.Zero);
        NotesStore.Save(notesFolder.Path, notesDraft.Snapshot());
        notesDirty = false;
        StopNotesTimers();
        ClearNotesWarning();
    }

    private bool TryFlushNotes()
    {
        if (notesFolder is null || !notesDirty) return true;
        try
        {
            FlushNotes();
            notesFlushError = null;
            return true;
        }
        catch (Exception ex)
        {
            // Nonmodal: audio is never blocked by a modal save error; the dirty draft is retained for retry.
            notesFlushError = ex;
            LogBestEffort("Could not save notes; the text is kept and saving will be retried.", ex);
            NotesWarning.Text = Ui.Format("NotesSaveFailed", ex.Message);
            NotesWarning.Visibility = Visibility.Visible;
            return false;
        }
    }

    // Log.Error appends to a file and can itself throw; logging must never replace the original error.
    private static void LogBestEffort(string message, Exception ex)
    {
        try { Log.Error(message, ex); } catch (Exception) { /* Best effort only. */ }
    }

    private void LoadNotesForProcessing(WorkingFolder folder)
    {
        if (notesDirty) throw new InvalidOperationException("Unsaved notes must be flushed first.");
        LockNotes();
        if (notesFolder is not null && notesFolder.Path == folder.Path) return; // Keep the live draft view.
        if (folder.Info.Prepared is null)
        {
            // Unprepared recovery: a valid snapshot is authoritative, missing is empty, corruption propagates.
            var snapshot = NotesStore.Load(folder.Path);
            notesDraft = snapshot is null ? new NotesDraft() : new NotesDraft(snapshot);
        }
        else notesDraft = new NotesDraft(); // Prepared publication reads validated outputs, not raw notes.
        notesFolder = folder;
        RenderNotes();
    }

    private void ShowProcessedNotes(string sessionFolder)
    {
        // Keep a real nonempty live/recovered draft view; an empty prepared placeholder may show published notes.
        if (notesFolder is not null && notesDraft.Rows.Any(row => !string.IsNullOrWhiteSpace(row.Text))) return;
        notesLocked = true;
        string text;
        try { text = File.ReadAllText(OutputFolder.NotesPath(sessionFolder)); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { text = ""; } // Missing optional output is normal.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBestEffort("Could not read the saved notes.", ex);
            NotesWarning.Text = Ui.Format("NotesReadFailed", ex.Message);
            NotesWarning.Visibility = Visibility.Visible;
            return; // No false empty export after a read error.
        }
        ClearNotesWarning();
        NotesSavedText.Text = text; // Exact saved content, line breaks included, in one native read-only TextBox.
        NotesSavedText.Visibility = Visibility.Visible;
        NotesRowsScroll.Visibility = Visibility.Collapsed;
    }

    private void RenderNotes()
    {
        NotesSavedText.Visibility = Visibility.Collapsed;
        NotesRowsScroll.Visibility = Visibility.Visible;
        notesRendering = true;
        try
        {
            NotesRows.Children.Clear();
            noteViews.Clear();
            for (var i = 0; i < notesDraft.Rows.Count; i++)
                AddNoteView(notesDraft.Rows[i].Id, i, readOnly: notesLocked);
        }
        finally { notesRendering = false; }
        RenumberNoteNames();
    }

    private NoteRowView AddNoteView(Guid id, int index, bool readOnly)
    {
        var row = notesDraft.Rows.First(item => item.Id == id);
        var stamp = new TextBlock
        {
            Width = 46, FontSize = 12, Opacity = .7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 0),
            Text = FormatStamp(row.OffsetMs)
        };
        var box = new TextBox
        {
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, // Shift+Enter is handled in HandleNoteKey; plain Enter starts a new bullet.
            IsReadOnly = readOnly,
            Text = row.Text // Set before wiring events so rendering never marks the draft dirty.
        };
        box.TextChanged += OnNoteTextChanged;
        box.PreviewKeyDown += OnNoteKeyDown;
        box.LostFocus += OnNoteLostFocus;
        DataObject.AddPastingHandler(box, OnNotePasting);
        var container = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.Children.Add(stamp);
        Grid.SetColumn(box, 1);
        container.Children.Add(box);
        var view = new NoteRowView { Id = id, Container = container, Stamp = stamp, Box = box };
        noteViews.Insert(index, view);
        NotesRows.Children.Insert(index, container);
        return view;
    }

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (notesRendering) return;
        var view = noteViews.FirstOrDefault(item => ReferenceEquals(item.Box, sender));
        if (view is null) return;
        var box = (TextBox)sender;
        notesDraft.SetText(view.Id, box.Text, recorder?.Elapsed ?? TimeSpan.Zero); // First input stamps the row.
        view.Stamp.Text = FormatStamp(notesDraft.Rows.First(row => row.Id == view.Id).OffsetMs); // Never rebuilds the TextBox/caret.
        MarkNotesDirty();
    }

    private void OnNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox box) e.Handled = HandleNoteKey(box, e.Key, Keyboard.Modifiers);
    }

    private bool HandleNoteKey(TextBox box, Key key, ModifierKeys modifiers)
    {
        if (key != Key.Enter || box.IsReadOnly || notesLocked) return false;
        if ((modifiers & ModifierKeys.Shift) == 0) HandleNoteEnter(box);
        else
        {
            var start = box.SelectionStart;
            box.SelectedText = Environment.NewLine; // Replaces the selection; TextChanged then stamps/saves as usual.
            box.Select(start + Environment.NewLine.Length, 0);
        }
        return true;
    }

    private void HandleNoteEnter(TextBox box)
    {
        var view = noteViews.FirstOrDefault(item => ReferenceEquals(item.Box, box));
        if (view is null || notesLocked) return;
        var index = noteViews.IndexOf(view);
        var created = AddNoteView(notesDraft.AddRow(view.Id), index + 1, readOnly: false);
        RenumberNoteNames();
        created.Box.Focus();
        created.Box.CaretIndex = 0; // New empty, unstamped row.
        created.Container.BringIntoView();
    }

    // Row TextBoxes would otherwise swallow the wheel, so the pane could not be scrolled with the mouse over a bullet.
    private void NotesWheel(object sender, MouseWheelEventArgs e)
    {
        NotesRowsScroll.ScrollToVerticalOffset(NotesRowsScroll.VerticalOffset + UiMath.WheelStep(e.Delta, SystemParameters.WheelScrollLines));
        e.Handled = true;
    }

    private void OnNotePasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || box.IsReadOnly) return;
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;
        if (!pasted.Contains('\n') && !pasted.Contains('\r')) return; // Normal single-line paste.
        if (ApplyNotePaste(box, pasted)) e.CancelCommand();
    }

    private bool ApplyNotePaste(TextBox box, string pasted)
    {
        if (box.IsReadOnly) return false;
        if (!pasted.Contains('\n') && !pasted.Contains('\r')) return false; // Single-line: keep default behavior.
        var lines = pasted.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line)).ToList(); // Blank pasted lines are ignored.
        if (lines.Count == 0) return true; // All-blank paste changes nothing.
        var view = noteViews.FirstOrDefault(item => ReferenceEquals(item.Box, box));
        if (view is null) return false;
        var clock = recorder?.Elapsed ?? TimeSpan.Zero; // One captured clock for all inserted rows.
        var prefix = box.Text[..box.SelectionStart];
        var suffix = box.Text[(box.SelectionStart + box.SelectionLength)..];
        var index = noteViews.IndexOf(view);
        var first = prefix + lines[0] + (lines.Count == 1 ? suffix : "");
        notesDraft.SetText(view.Id, first, clock); // The old row retains its old time.
        view.Box.Text = first;
        view.Stamp.Text = FormatStamp(notesDraft.Rows.First(row => row.Id == view.Id).OffsetMs);
        var lastId = view.Id;
        for (var i = 1; i < lines.Count; i++)
        {
            var text = lines[i] + (i == lines.Count - 1 ? suffix : ""); // Suffix lands on the final inserted row.
            lastId = notesDraft.AddRow(lastId);
            notesDraft.SetText(lastId, text, clock);
            index++;
            AddNoteView(lastId, index, readOnly: false);
        }
        RenumberNoteNames();
        MarkNotesDirty();
        var last = noteViews.First(item => item.Id == lastId);
        last.Box.Focus();
        last.Box.CaretIndex = last.Box.Text.Length;
        last.Container.BringIntoView();
        return true;
    }

    private void OnNoteLostFocus(object sender, RoutedEventArgs e)
    {
        if (notesDirty) TryFlushNotes(); // Flush on leaving an editor.
    }

    private void MarkNotesDirty()
    {
        notesDirty = true;
        notesDebounce.Stop();
        notesDebounce.Start(); // 250 ms debounce resets on each edit.
        if (!notesDeadline.IsEnabled) notesDeadline.Start(); // 1 s wall-clock deadline never resets.
    }

    private void NotesFlushTimerTick()
    {
        if (notesDirty) TryFlushNotes();
    }

    private void StopNotesTimers()
    {
        notesDebounce.Stop();
        notesDeadline.Stop();
    }

    private void ClearNotesWarning() => NotesWarning.Visibility = Visibility.Collapsed;

    private void RenumberNoteNames()
    {
        for (var i = 0; i < noteViews.Count; i++)
            AutomationProperties.SetName(noteViews[i].Box, Ui.Format("NoteRow", i + 1));
    }

    private static string FormatStamp(long? offsetMs) =>
        offsetMs is long ms ? TranscriptWriter.Timestamp(TimeSpan.FromMilliseconds(ms)) : "";

    private sealed class NoteRowView
    {
        public required Guid Id { get; init; }
        public required Grid Container { get; init; }
        public required TextBlock Stamp { get; init; }
        public required TextBox Box { get; init; }
    }
}
