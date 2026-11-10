using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MeetingRecorder.Audio;
using MeetingRecorder.Native;
using MeetingRecorder.Processing;
using MeetingRecorder.Recording;

namespace MeetingRecorder;

public partial class MainWindow : Window
{
    private readonly CaptureSource mic = new(SourceKind.Microphone);
    private readonly CaptureSource system = new(SourceKind.System);
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer titleTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<string> deferred = [];
    private Recorder? recorder;
    private StripWindow? strip;
    private volatile bool closed;
    private bool processing;
    private bool closeRequested;
    private bool closeReady;
    private WorkingFolder? pending;
    private ProcessingResult? output;

    public MainWindow()
    {
        InitializeComponent();
        // Fluent's accent style comes from App's ThemeMode; windows built without it (tests) fall back to the plain button.
        var primary = new Style(typeof(Button), TryFindResource("AccentButtonStyle") as Style ?? (Style)FindResource(typeof(Button)));
        primary.Setters.Add(new Setter(ContentTemplateProperty, FindResource("IconLabel")));
        primary.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        primary.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 10, 12, 10)));
        primary.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        Resources["PrimaryButton"] = primary;
        meterTimer.Tick += (_, _) => UpdateMeters();
        titleTimer.Tick += (_, _) => PersistTitle();
        InitializeNotes();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (recorder is not null) return;
        try
        {
            NativeLibraries.EnsureLoaded();
            recorder = new Recorder(mic, system, () => new RecordingPowerRequest(
                new WindowsRecordingPowerRequestNative(), Ui.Get("RecordingPowerRequestReason")));
            mic.Start();
            system.Start();
            meterTimer.Start();
            RefreshRecovery();
            InitializeModel();
        }
        catch (Exception ex)
        {
            App.ShowError("StartupError", ex);
            Close();
        }
    }

    private void UpdateMeters()
    {
        MicMeter.Value = Math.Max(UiMath.Meter(mic.Peak), MicMeter.Value - .05);
        SystemMeter.Value = Math.Max(UiMath.Meter(system.Peak), SystemMeter.Value - .05);
        MicWarning.Visibility = mic.HasDevice ? Visibility.Collapsed : Visibility.Visible;
        SystemWarning.Visibility = system.HasDevice ? Visibility.Collapsed : Visibility.Visible;
        var elapsed = recorder?.IsRecording == true ? recorder.Elapsed : TimeSpan.Zero;
        TimerText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        PauseButton.Content = Ui.Get(recorder?.IsPaused == true ? "Resume" : "Pause");
        PauseButton.Tag = recorder?.IsPaused == true ? "\uE768" : "\uE769";
        RecordDot.Opacity = recorder?.IsPaused == true ? .35 : 1;
        strip?.Update(TimerText.Text, MicMeter.Value, SystemMeter.Value, recorder?.IsPaused == true);
    }

    private void TitleChanged(object sender, TextChangedEventArgs e)
    {
        if (selectedSeries is not null || recorder?.IsRecording != true) return;
        titleTimer.Stop();
        titleTimer.Start();
    }

    private void PersistTitle()
    {
        titleTimer.Stop();
        if (selectedSeries is null) recorder?.UpdateTitle(TitleBox.Text);
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        if (processing || recorder?.IsRecording == true) return;
        CancelOpenCode(); // Starting a recording leaves the Done result; its Continue in OpenCode is abandoned.
        if (selectedSeries is not null)
        {
            try
            {
                var series = MeetingSeries.Read(selectedSeries.Folder);
                series.CheckWritable();
            }
            catch (Exception ex) { App.ShowError("ContinueSelectionError", ex); return; }
        }
        if (!TryFlushNotes()) return; // Any older dirty draft is saved to its own folder before a new capture starts.
        try
        {
            recorder!.Start(TitleBox.Text, selectedSeries?.Folder);
            BeginNotes(recorder.Current!); // Fresh notes for the new capture; a continuation never loads series notes.
            StartButton.Visibility = ContinueButton.Visibility = Visibility.Collapsed;
            RecordingButtons.Visibility = RecordDot.Visibility = Visibility.Visible;
            StartedText.Text = Ui.Format("Started", recorder.Current!.Info.StartedAt);
            RecoveryPanel.IsEnabled = false;
        }
        catch (Exception ex) { App.ShowError("StartError", ex); }
    }

    internal void TogglePause()
    {
        try
        {
            if (recorder?.IsRecording != true) return;
            PersistTitle();
            TryFlushNotes(); // A notes save failure must not block pausing/resuming; the warning stays visible.
            if (recorder.IsPaused) recorder.Resume(); else recorder.Pause();
            UpdateMeters();
        }
        catch (Exception ex) { App.ShowError("PauseError", ex); }
    }

    internal void StopRecording()
    {
        if (recorder?.IsRecording != true) return;
        RestoreMain();
        LockNotes(); // Notes lock immediately; no editing once Stop begins.
        var notesSaved = TryFlushNotes(); // Flushes the unfinished row too; on failure the dirty draft and warning stay.
        Exception? failure = null;
        try { PersistTitle(); }
        catch (Exception ex)
        {
            // A metadata/title failure must not strand the recording with locked notes; audio stop is still attempted.
            failure = ex;
            LogBestEffort("Could not persist the meeting title on stop.", ex);
        }
        WorkingFolder? folder;
        try { folder = recorder.Stop(); } // Audio ALWAYS stops, even when the notes flush failed.
        catch (Exception ex)
        {
            if (recorder.IsRecording)
            {
                App.ShowError("StopError", ex);
                return;
            }
            failure ??= ex;
            folder = recorder.Current;
        }
        if (folder is null) { App.ShowError("StopError", failure ?? new InvalidOperationException("Not recording.")); return; }
        pending = folder;
        if (!notesSaved)
        {
            // No processing/model launch: audio, locked pane and dirty draft stay retryable via the error path.
            ShowProcessingError(notesFlushError!);
            return;
        }
        if (failure is not null) { ShowProcessingError(failure); return; }
        processingTask = ProcessMeeting(folder);
    }

    private void PauseClick(object sender, RoutedEventArgs e) => TogglePause();
    private void StopClick(object sender, RoutedEventArgs e) => StopRecording();
    private void CompactClick(object sender, RoutedEventArgs e)
    {
        if (recorder?.IsRecording != true) return;
        if (!TryFlushNotes()) return; // Keep the full window and warning visible instead of hiding the error; audio continues.
        strip ??= new StripWindow(this);
        strip.Show();
        Hide();
        strip.Activate();
    }

    internal void RestoreMain()
    {
        strip?.Hide();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.M || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        CompactClick(sender, e);
    }

    private void RefreshRecovery()
    {
        RecoveryPanel.Children.Clear();
        foreach (var folder in WorkingFolder.Scan(AppPaths.Recordings))
        {
            if (folder.Path == (recorder?.IsRecording == true ? recorder.Current?.Path : null) || deferred.Contains(folder.Path)) continue;
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(new TextBlock
            {
                Text = Ui.Format("RecoveryFound", folder.Info.StartedAt, folder.RecordedDuration().TotalMinutes)
                    + (folder.Info.State == MeetingState.Failed ? "\n" + Ui.Get("FailedEarlier") : ""),
                TextWrapping = TextWrapping.Wrap
            });
            var buttons = new WrapPanel();
            AddButton(Ui.Get("ProcessNow"), "\uE768", () => { RecoveryPanel.Children.Remove(panel); processingTask = ProcessMeeting(folder); });
            AddButton(Ui.Get("Later"), "\uE823", () => { deferred.Add(folder.Path); RecoveryPanel.Children.Remove(panel); });
            AddButton(Ui.Get("Discard"), "\uE74D", () =>
            {
                if (!ConfirmClose(Ui.Get("ConfirmDiscard"), "No")) return;
                folder.Discard();
                RecoveryPanel.Children.Remove(panel);
            });
            panel.Children.Add(buttons);
            RecoveryPanel.Children.Add(panel);

            void AddButton(string label, string glyph, Action action)
            {
                var button = new Button { Content = label, Tag = glyph, Margin = new Thickness(0, 8, 6, 0) };
                button.Click += (_, _) => { try { action(); } catch (Exception ex) { App.ShowError("RecoveryError", ex); } };
                buttons.Children.Add(button);
            }
        }
    }

    private void NewClick(object sender, RoutedEventArgs e)
    {
        if (processing || recorder?.IsRecording == true) return;
        if (!TryFlushNotes()) return; // A dirty draft is saved to its original folder first; failure keeps the text visible.
        CancelOpenCode(); // Leaving the Done result abandons its Continue in OpenCode; a late completion is ignored.
        ResetNotes(); // Never erases unsaved text; the raw earlier working folder stays recoverable on disk.
        pending = null;
        output = null;
        DonePanel.Visibility = ErrorPanel.Visibility = ProcessingPanel.Visibility = Visibility.Collapsed;
        RecordPanel.Visibility = StartButton.Visibility = ContinueButton.Visibility = Visibility.Visible;
        RecordingButtons.Visibility = RecordDot.Visibility = Visibility.Collapsed;
        SelectSeries(null);
        StartedText.Text = "";
        TimerText.Text = "00:00:00";
        RecoveryPanel.IsEnabled = true;
        RefreshRecovery();
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { App.ShowError("OpenError", ex, path); }
    }
    private void OpenTranscriptClick(object sender, RoutedEventArgs e) => OpenPath(OutputFolder.TranscriptToOpen(output!.SessionFolder));
    private void OpenFolderClick(object sender, RoutedEventArgs e) => OpenPath(output!.SeriesFolder);
    private void OpenMeetingsClick(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(AppPaths.MeetingsOutput); OpenPath(AppPaths.MeetingsOutput); }
        catch (Exception ex) { App.ShowError("OpenMeetingsError", ex); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeRequested)
        {
            e.Cancel = !closeReady;
            return;
        }
        if (recorder?.IsRecording == true)
        {
            e.Cancel = true;
            if (ConfirmClose(Ui.Get("ConfirmStop")))
                StopRecording();
        }
        else if (processing)
        {
            e.Cancel = true;
            if (ConfirmClose(Ui.Get("ConfirmProcessingClose")))
                _ = StopProcessingAndClose();
        }
        else if (!TryFlushNotes()) e.Cancel = true; // Unsaved notes cancel the close; the draft and warning stay visible.
    }

    private async Task StopProcessingAndClose()
    {
        if (closeRequested) return;
        closeRequested = true;
        IsEnabled = false;
        RemainingText.Text = Ui.Get("Stopping");
        lifetime.Cancel();
        try
        {
            await processingTask;
            await downloadTask;
        }
        finally
        {
            closeReady = true;
            // Queue even when work already finished: Close cannot be called recursively from Closing.
            await Dispatcher.InvokeAsync(Close);
        }
    }

    private bool ConfirmClose(string message, string cancelKey = "Cancel")
    {
        var dialog = new Window
        {
            Title = Title, Owner = this, Width = 380, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var yes = new Button { Content = Ui.Get("Yes"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        yes.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(yes);
        buttons.Children.Add(new Button { Content = Ui.Get(cancelKey), IsCancel = true, IsDefault = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        CancelOpenCode(); // Closing does not wait for OpenCode: the handoff is cancelled and its late result is ignored.
        meterTimer.Stop();
        titleTimer.Stop();
        StopNotesTimers(); // Stops the notes timers without discarding unsaved state.
        lifetime.Cancel();
        downloadCancellation?.Cancel();
        strip?.CloseForExit();
        try { Settings.Load().Save(); }
        catch (Exception ex) { Log.Error("Could not save settings on exit.", ex); }
        recorder?.Dispose();
        mic.Dispose();
        system.Dispose();
        engine?.Dispose();
        // A ready-window close can also interrupt a model download; let its HTTP use unwind first.
        _ = downloadTask.ContinueWith(_ => http.Dispose(), TaskScheduler.Default);
    }
}
