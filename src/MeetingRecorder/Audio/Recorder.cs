using System.Diagnostics;
using System.IO;
using MeetingRecorder.Recording;

namespace MeetingRecorder.Audio;

internal static class RecorderMath
{
    public static long ExpectedSamples(TimeSpan elapsed) => (long)(elapsed.TotalSeconds * 16000);
}

internal sealed class Recorder : IDisposable
{
    private readonly object gate = new();
    private readonly CaptureSource mic;
    private readonly CaptureSource system;
    private readonly Stopwatch clock = new();
    private readonly Stopwatch pauses = new();
    private TrackWriter? micWriter;
    private TrackWriter? systemWriter;
    private System.Threading.Timer? timer;
    private WorkingFolder? current;
    private bool recording;
    private bool paused;
    private bool disposed;
    private long? lastWriteError;
    private readonly Func<RecordingPowerRequest>? powerRequestFactory;
    private RecordingPowerRequest? powerRequest;

    public Recorder(CaptureSource mic, CaptureSource system, Func<RecordingPowerRequest>? powerRequestFactory = null)
    {
        this.mic = mic;
        this.system = system;
        this.powerRequestFactory = powerRequestFactory;
        mic.Samples += WriteMic;
        system.Samples += WriteSystem;
    }

    public bool IsRecording { get { lock (gate) return recording; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    public TimeSpan Elapsed { get { lock (gate) return clock.Elapsed; } }
    public WorkingFolder? Current { get { lock (gate) return current; } }

    public void Start(string? title, string? continues = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (recording) throw new InvalidOperationException("Already recording.");
            powerRequest = powerRequestFactory?.Invoke();
            try { powerRequest?.Acquire(); }
            catch { powerRequest?.Dispose(); powerRequest = null; throw; }
            try
            {
                current = WorkingFolder.Create(AppPaths.Recordings, DateTimeOffset.Now);
                micWriter = systemWriter = null;
                current.Save(current.Info with { Title = title, Continues = continues is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(continues)) });
                micWriter = new TrackWriter(current.MicWav);
                systemWriter = new TrackWriter(current.SystemWav);
                mic.Start();
                system.Start();
                pauses.Reset();
                paused = false;
                lastWriteError = null;
                clock.Restart();
                timer = new System.Threading.Timer(_ => Pad(), null, 250, 250);
                recording = true;
            }
            catch
            {
                try { powerRequest?.Dispose(); }
                catch (Exception ex)
                {
                    try { Log.Error("Could not release the recording power request after Start failed.", ex); }
                    catch (Exception) { }
                }
                powerRequest = null;
                clock.Reset();
                timer?.Dispose();
                timer = null;
                try { micWriter?.Dispose(); }
                catch (Exception ex) { LogWriteFailure(ex); }
                try { systemWriter?.Dispose(); }
                catch (Exception ex) { LogWriteFailure(ex); }
                try { current?.Discard(); }
                catch (Exception ex) { LogWriteFailure(ex); }
                micWriter = systemWriter = null;
                current = null;
                throw;
            }
        }
    }

    private void WriteMic(float[] samples) => Write(samples, microphone: true);
    private void WriteSystem(float[] samples) => Write(samples, microphone: false);

    private void Write(float[] samples, bool microphone)
    {
        lock (gate)
        {
            if (recording && !paused)
            {
                try { (microphone ? micWriter : systemWriter)!.Write(samples, RecorderMath.ExpectedSamples(clock.Elapsed)); }
                catch (Exception ex) { LogWriteFailure(ex); }
            }
        }
    }

    private void Pad()
    {
        lock (gate)
        {
            if (!recording || paused) return;
            var expected = RecorderMath.ExpectedSamples(clock.Elapsed);
            try { micWriter!.PadTo(expected); }
            catch (Exception ex) { LogWriteFailure(ex); }
            try { systemWriter!.PadTo(expected); }
            catch (Exception ex) { LogWriteFailure(ex); }
        }
    }

    private void LogWriteFailure(Exception ex)
    {
        if (lastWriteError is long last && Stopwatch.GetElapsedTime(last).TotalSeconds < 5) return;
        lastWriteError = Stopwatch.GetTimestamp();
        try { Log.Error("Recording write failed; recording continues and writes will be retried.", ex); }
        catch (Exception) { /* A logging failure must not escape a capture or timer callback. */ }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (!recording || paused) return;
            clock.Stop();
            paused = true;
            pauses.Start();
            Save();
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (!recording || !paused) return;
            pauses.Stop();
            paused = false;
            clock.Start();
            Save();
        }
    }

    public void UpdateTitle(string? title)
    {
        lock (gate)
        {
            if (recording) current!.Save(current.Info with { Title = title, PausedMs = pauses.ElapsedMilliseconds });
        }
    }

    private void Save() => current!.Save(current.Info with { PausedMs = pauses.ElapsedMilliseconds });

    public WorkingFolder Stop()
    {
        lock (gate)
        {
            if (!recording) throw new InvalidOperationException("Not recording.");
            clock.Stop();
            pauses.Stop();
            recording = false;
            paused = false;
            try
            {
                timer?.Dispose();
                timer = null;
                var expected = RecorderMath.ExpectedSamples(clock.Elapsed);
                try { micWriter!.PadTo(expected); systemWriter!.PadTo(expected); }
                finally
                {
                    try { micWriter!.Dispose(); }
                    finally { systemWriter!.Dispose(); }
                }
                current!.Save(current.Info with { StoppedAt = DateTimeOffset.Now, PausedMs = pauses.ElapsedMilliseconds, State = MeetingState.Processing });
                return current;
            }
            finally
            {
                var request = powerRequest;
                powerRequest = null;
                request?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            mic.Samples -= WriteMic;
            system.Samples -= WriteSystem;
            try { if (recording) Stop(); }
            finally
            {
                var request = powerRequest;
                powerRequest = null;
                request?.Dispose();
            }
        }
    }
}
