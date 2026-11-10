using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using MeetingRecorder.Model;
using MeetingRecorder.Native;
using MeetingRecorder.Processing;
using MeetingRecorder.Recording;

namespace MeetingRecorder;

public partial class MainWindow
{
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? downloadCancellation;
    private ModelStore? model;
    private SpeechEngine? engine;
    private Task processingTask = Task.CompletedTask;
    private Task downloadTask = Task.CompletedTask;
    private bool downloading;

    private void InitializeModel()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MeetingRecorderForWindows/1.0");
        model = new ModelStore(http, AppPaths.ModelFile,
            "https://huggingface.co/infomiho/diktafon-dictate-hr-1-GGUF/resolve/main/diktafon-dictate-hr-1-q5_k_m.gguf",
            836664032, "108489d828f8794e0653cd359551053369bfb223fbb1bf2552dbf69aa39755c7");
        if (model.IsPresent) modelReady.TrySetResult();
        else downloadTask = DownloadModel();
    }

    private async Task DownloadModel()
    {
        downloadCancellation?.Dispose();
        downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        downloading = true;
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadButton.Content = Ui.Get("Cancel");
        DownloadButton.IsEnabled = true;
        DownloadBar.Value = 0;
        DownloadText.Text = Ui.Get("Connecting");
        Log.Info("Starting speech model download.");
        try
        {
            var progress = new DispatchProgress<(long done, long total)>(Dispatcher, value =>
            {
                if (closed || closeRequested) return;
                DownloadBar.Value = value.total > 0 ? (double)value.done / value.total : 0;
                DownloadText.Text = Ui.Format("DownloadProgress", value.done / 1_000_000d, value.total / 1_000_000d);
            });
            await Task.Run(() => model!.DownloadAsync(progress, downloadCancellation.Token));
            modelReady.TrySetResult();
            if (!closed) DownloadPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            if (!closed) DownloadText.Text = Ui.Get("DownloadCancelled");
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Speech model download failed.", ex);
            if (!closed) DownloadText.Text = Ui.Format("DownloadFailed", ex.Message, AppPaths.LogFile);
        }
        finally
        {
            downloading = false;
            if (!closed) { DownloadButton.Content = Ui.Get("Retry"); DownloadButton.IsEnabled = true; }
        }
    }

    private void DownloadClick(object sender, RoutedEventArgs e)
    {
        if (downloading)
        {
            DownloadButton.IsEnabled = false;
            downloadCancellation?.Cancel();
        }
        else downloadTask = DownloadModel();
    }

    private async Task ProcessMeeting(WorkingFolder folder)
    {
        if (processing || recorder?.IsRecording == true) return;
        // A dirty live draft is always saved to its ORIGINAL notes folder first; a failed flush refuses the switch.
        if (!TryFlushNotes()) return;
        CancelOpenCode(); // Processing replaces the Done result; an in-flight Continue in OpenCode is abandoned.
        processing = true;
        pending = folder;
        RecordPanel.Visibility = DonePanel.Visibility = ErrorPanel.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Visible;
        RecoveryPanel.IsEnabled = false;
        ProcessingTitle.Text = string.IsNullOrWhiteSpace(folder.Info.Title) ? Ui.Get("ProcessingRecording") : folder.Info.Title;
        MixingText.Text = "\u25cb " + Ui.Get("Mixing");
        TranscribingText.Text = "\u25cb " + Ui.Get("Transcribing");
        SavingText.Text = "\u25cb " + Ui.Get("Saving");
        ProcessingBar.Value = 0;
        ProcessingBar.IsIndeterminate = true;
        RemainingText.Text = folder.Info.Prepared is null ? Ui.Get(modelReady.Task.IsCompleted ? "LoadingModel" : "WaitingModel") : "";
        var clock = new Stopwatch();
        try
        {
            // Lock/load recovered notes read-only inside the error-handled boundary; raw corruption stays a real failure.
            // Prepared stages bypass the raw snapshot and model dependency, so a ready-stage retry still publishes.
            LoadNotesForProcessing(folder);
            FinishedText.Text = Ui.Format("RecordingFinished", folder.RecordedDuration().TotalMinutes);
            if (folder.Info.Prepared is null) await modelReady.Task.WaitAsync(lifetime.Token);
            var progress = new DispatchProgress<(ProcessingStep step, double fraction)>(Dispatcher, value =>
            {
                if (closed || closeRequested) return;
                ProcessingBar.IsIndeterminate = false;
                var fraction = ((int)value.step + value.fraction) / 3;
                ProcessingBar.Value = fraction;
                MixingText.Text = (value.step > ProcessingStep.Mixing || value.fraction >= 1 ? "\u2713 " : "\u25cc ") + Ui.Get("Mixing");
                TranscribingText.Text = value.step > ProcessingStep.Transcribing ? "\u2713 " + Ui.Get("Transcribing") :
                    value.step == ProcessingStep.Transcribing ? Ui.Format("TranscribingProgress", value.fraction) : "\u25cb " + Ui.Get("Transcribing");
                SavingText.Text = (value.step == ProcessingStep.Saving ? "\u25cc " : "\u25cb ") + Ui.Get("Saving");
                var estimate = fraction > .01 ? Ui.Format("TimeLeft", Math.Max(1, (int)Math.Ceiling(clock.Elapsed.TotalMinutes * (1 - fraction) / fraction))) : "";
                RemainingText.Text = estimate + Ui.Get("MinimizeHint");
            });
            clock.Start();
            output = await Task.Run(() =>
            {
                lifetime.Token.ThrowIfCancellationRequested();
                // The native engine starts its own worker threads, so only the process priority reaches them.
                // Recording never runs during processing; restore before any new recording starts.
                using var self = Process.GetCurrentProcess();
                self.PriorityClass = ProcessPriorityClass.BelowNormal;
                try
                {
                    if (folder.Info.Prepared is null) engine ??= new SpeechEngine(AppPaths.ModelFile);
                    return new MeetingProcessor(samples => engine!.Recognize(samples), AppPaths.MeetingsOutput).Process(folder, progress, lifetime.Token);
                }
                finally { self.PriorityClass = ProcessPriorityClass.Normal; }
            });
            if (closed || closeRequested) return;
            ShowDone(output);
            ShowProcessedNotes(output.SessionFolder); // Saved notes from the session folder when no live draft view.
            Attention.Notify(this);
        }
        catch (Exception ex)
        {
            if (closed || closeRequested) return;
            ShowProcessingError(ex);
            Attention.Notify(this);
        }
        finally
        {
            processing = false;
            if (!closed && !closeRequested) RecoveryPanel.IsEnabled = true;
        }
    }

    private void ShowDone(ProcessingResult result)
    {
        CancelOpenCode(); // A new result never inherits an earlier Continue in OpenCode; its late completion is ignored.
        output = result;
        pending = null;
        ProcessingPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        OutputText.Text = Ui.Format("OutputFiles", result.SessionFolder);
        ContinuationNotice.Text = result.ContinuedTargetMissing ? Ui.Get("ContinuationSavedSeparately") : "";
        ContinuationNotice.Visibility = result.ContinuedTargetMissing ? Visibility.Visible : Visibility.Collapsed;
        SeriesRecordNotice.Text = result.SeriesRecordError is null ? "" : Ui.Format("SeriesRecordFailed", result.SeriesRecordError);
        SeriesRecordNotice.Visibility = result.SeriesRecordError is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowProcessingError(Exception ex)
    {
        LogBestEffort("Could not process recording.", ex); // Error-report logging failure must not mask the original error.
        ProcessingPanel.Visibility = RecordPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = Ui.Format("ProcessingFailed", ex.Message, AppPaths.LogFile);
        RetryButton.IsEnabled = pending is not null;
    }

    private void RetryClick(object sender, RoutedEventArgs e)
    {
        if (pending is not null) processingTask = ProcessMeeting(pending);
    }
}

internal sealed class DispatchProgress<T>(Dispatcher dispatcher, Action<T> update) : IProgress<T>
{
    public void Report(T value)
    {
        if (!dispatcher.HasShutdownStarted) _ = dispatcher.InvokeAsync(() => update(value));
    }
}
