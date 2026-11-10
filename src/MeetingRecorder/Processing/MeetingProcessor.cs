using System.IO;
using System.Text;
using MeetingRecorder.Audio;
using MeetingRecorder.Recording;

namespace MeetingRecorder.Processing;

internal enum ProcessingStep { Mixing, Transcribing, Saving }

internal record ProcessingResult(string SessionFolder, string SeriesFolder, bool ContinuedTargetMissing, string? SeriesRecordError = null);

internal sealed class MeetingProcessor(Func<float[], string> recognize, string meetingsRoot)
{
    public ProcessingResult Process(WorkingFolder wf, IProgress<(ProcessingStep step, double fraction)> progress, CancellationToken ct)
    {
        string? output = null;
        string? ownedPartial = null;
        string? seriesFolder = null;
        bool missing = false;
        ContinuationStage? stagePin = null;
        ContinuationStage? parentPin = null;
        bool stagePinClosed = false;
        try
        {
            wf.Save(wf.Info with { State = MeetingState.Processing });
            ct.ThrowIfCancellationRequested();
            MeetingSeries? series = null;
            if (wf.Info.Continues is { } target)
            {
                if (!Path.IsPathFullyQualified(target) || !string.Equals(target, Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid continuation path.");
                try
                {
                    var attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Continuation target is not a directory.");
                }
                catch (FileNotFoundException) { missing = true; }
                catch (DirectoryNotFoundException) { missing = true; }
                if (missing && wf.Info.Prepared is not null) throw new IOException("Prepared continuation target is missing.");
                if (!missing)
                {
                    series = MeetingSeries.Read(target);
                    if (!string.Equals(series.Folder, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("Continuation target is not a series root.");
                    series.CheckWritable();
                    seriesFolder = target;
                }
            }
            else if (wf.Info.Prepared is not null) throw new IOException("Prepared continuation has no series.");

            if (wf.Info.Prepared is { } prepared)
            {
                ValidatePrepared(seriesFolder!, prepared);
                ct.ThrowIfCancellationRequested();
                output = Publish(seriesFolder!, prepared.StagingFolder, wf.Info.StartedAt);
            }
            else
            {
                var notes = NotesStore.Load(wf.Path)?.Rows ?? [];
                var hasNotes = notes.Any(row => !string.IsNullOrWhiteSpace(row.Text));
                progress.Report((ProcessingStep.Mixing, 0));
                if (File.Exists(wf.MicWav)) WavRepair.Fix(wf.MicWav);
                if (File.Exists(wf.SystemWav)) WavRepair.Fix(wf.SystemWav);
                Mixer.Mix(wf.MicWav, wf.SystemWav, wf.MixedWav);
                progress.Report((ProcessingStep.Mixing, 1));

                var segments = Transcriber.Run(wf.MixedWav, recognize, new TranscriptionProgress(progress), ct);
                ct.ThrowIfCancellationRequested();
                if (series is not null) parentPin = ContinuationStage.Pin(series.Folder, forDeletion: false);
                int? session = series is null ? null : 2 + Directory.EnumerateDirectories(series.Folder).Count(MeetingSeries.IsCompletedSession);
                if (series is null) output = OutputFolder.CreateUnique(meetingsRoot, OutputFolder.Name(wf.Info.StartedAt, wf.Info.Title));
                else
                {
                    output = Path.Combine(series.Folder, "~" + Guid.NewGuid().ToString("N"));
                    stagePin = ContinuationStage.Create(output);
                }
                ownedPartial = output;
                var title = series?.Title ?? wf.Info.Title;
                var (end, duration) = (wf.EstimatedEnd(), wf.RecordedDuration());
                progress.Report((ProcessingStep.Saving, 0));
                ct.ThrowIfCancellationRequested();
                var audio = Encoder.Encode(wf.MixedWav, output);
                File.WriteAllText(Path.Combine(output, OutputFolder.TranscriptName(audio)), TranscriptWriter.BuildText(title, wf.Info.StartedAt, end,
                    duration, segments, session), new UTF8Encoding(false));
                if (hasNotes)
                {
                    File.WriteAllText(Path.Combine(output, OutputFolder.NotesName(audio)), NotesWriter.BuildNotes(title, wf.Info.StartedAt, end,
                        duration, notes, session), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(output, "combined.txt"), NotesWriter.BuildCombined(title, wf.Info.StartedAt, end,
                        duration, segments, notes, session), new UTF8Encoding(false));
                }
                stagePin?.Dispose();
                stagePinClosed = stagePin is not null;
                progress.Report((ProcessingStep.Saving, 1));
                ct.ThrowIfCancellationRequested();
                if (series is not null)
                {
                    wf.Save(wf.Info with { Prepared = new(output, session!.Value, hasNotes) });
                    ownedPartial = null;
                    ValidatePrepared(series.Folder, wf.Info.Prepared!);
                    output = Publish(series.Folder, output, wf.Info.StartedAt);
                }
            }
        }
        catch (Exception ex)
        {
            if (ownedPartial is not null)
            {
                try
                {
                    if (stagePin is null) Directory.Delete(ownedPartial, recursive: true);
                    else if (!stagePinClosed) stagePin.Delete(ownedPartial);
                    else
                    {
                        using var current = ContinuationStage.Pin(ownedPartial);
                        if (stagePin.SameAs(current)) current.Delete(ownedPartial);
                    }
                }
                catch (Exception cleanupError) { TryLog("Could not remove partial output " + ownedPartial, cleanupError); }
            }
            try { wf.Save(wf.Info with { State = MeetingState.Failed }); }
            catch (Exception stateError) { TryLog("Could not mark meeting failed " + wf.Path, stateError); }
            TryLog("Meeting processing failed " + wf.Path, ex);
            throw;
        }
        finally
        {
            stagePin?.Dispose();
            parentPin?.Dispose();
        }

        string? recordError = null;
        try { SeriesRecordWriter.Write(seriesFolder ?? output!); }
        catch (Exception ex)
        {
            recordError = ex.Message;
            TryLog("Could not update the series record", ex);
        }
        try { wf.MarkCompleted(); }
        catch (Exception markerError)
        {
            TryLog("Could not mark working folder completed; keeping audio and completed output " + wf.Path, markerError);
            return new(output!, seriesFolder ?? output!, missing, recordError);
        }
        try { wf.DeleteCompleted(); }
        catch (Exception cleanupError) { TryLog("Could not remove working folder " + wf.Path, cleanupError); }
        return new(output!, seriesFolder ?? output!, missing, recordError);
    }

    private static void ValidatePrepared(string series, PreparedContinuation prepared)
    {
        var stage = prepared.StagingFolder;
        if (prepared.SessionNumber < 2 || string.IsNullOrEmpty(stage) || !Path.IsPathFullyQualified(stage) ||
            !string.Equals(stage, Path.TrimEndingDirectorySeparator(Path.GetFullPath(stage)), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(stage), series, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(stage).StartsWith('~')) throw new IOException("Invalid prepared continuation path.");
        var attributes = File.GetAttributes(stage);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0 ||
            !MeetingSeries.HasRecording(stage) ||
            !(MeetingSeries.AudioTranscript(stage) is not null || File.Exists(Path.Combine(stage, "transcript.txt"))))
            throw new IOException("Prepared continuation is incomplete or unsafe.");
        if (prepared.HasNotes && new[] { OutputFolder.NotesPath(stage), Path.Combine(stage, "combined.txt") }.Any(path =>
            !File.Exists(path) || new FileInfo(path).Length == 0))
            throw new IOException("Prepared continuation notes are incomplete.");
    }

    private static string Publish(string series, string stage, DateTimeOffset start)
    {
        var name = OutputFolder.Name(start, null);
        while (true)
        {
            var destination = OutputFolder.UniquePath(series, name);
            try { Directory.Move(stage, destination); return destination; }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
        }
    }

    private static void TryLog(string message, Exception ex)
    {
        // Reporting a failure must not replace the original exception.
        try { Log.Error(message, ex); }
        catch (Exception) { }
    }

    private sealed class TranscriptionProgress(IProgress<(ProcessingStep step, double fraction)> progress) : IProgress<double>
    {
        public void Report(double value) => progress.Report((ProcessingStep.Transcribing, value));
    }
}
