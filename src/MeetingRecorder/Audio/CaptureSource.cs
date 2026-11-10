using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingRecorder.Audio;

internal enum SourceKind { Microphone, System }

internal sealed class CaptureSource(SourceKind kind) : IDisposable, IMMNotificationClient
{
    private readonly object gate = new();
    private readonly DataFlow flow = kind == SourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;
    private MMDeviceEnumerator? enumerator;
    private MMDevice? device;
    private WasapiCapture? capture;
    private System.Threading.Timer? retry;
    private volatile bool started;
    private volatile bool disposed;
    private float peak;
    private long lastBuffer;

    public event Action<float[]>? Samples;
    public bool HasDevice => Volatile.Read(ref capture) is not null;
    public float Peak => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastBuffer)).TotalMilliseconds <= 300
        ? Volatile.Read(ref peak) : 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // A live source's restart may be joining a callback that is waiting on Recorder's lock.
        if (started) return;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) return;
            started = true;
            retry = new System.Threading.Timer(_ => ScheduleRestart(), null, 0, 1000);
        }
    }

    private void ScheduleRestart(WasapiCapture? failed = null) =>
        _ = Task.Run(async () =>
        {
            if (failed is not null) await Task.Delay(1000);
            Restart(failed);
        });

    private void Restart(WasapiCapture? failed)
    {
        lock (gate)
        {
            if (disposed || !started) return;
            if (failed is not null && !ReferenceEquals(failed, capture)) return;
            MMDevice? next = null;
            var replacing = false;
            try
            {
                if (enumerator is null)
                {
                    var candidate = new MMDeviceEnumerator();
                    try { candidate.RegisterEndpointNotificationCallback(this); }
                    catch { candidate.Dispose(); throw; }
                    enumerator = candidate;
                }
                next = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
                if (failed is null && capture is not null && next.ID == device?.ID)
                    return;

                TryLog($"Restarting {kind} capture on {next.FriendlyName} ({next.ID}).");
                replacing = true;
                ReleaseCapture();
                device = next;
                next = null;
                var instance = kind == SourceKind.Microphone
                    ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
                Volatile.Write(ref capture, instance);
                var buffer = new BufferedWaveProvider(instance.WaveFormat)
                {
                    ReadFully = false,
                    BufferDuration = TimeSpan.FromSeconds(5),
                    DiscardOnBufferOverflow = true
                };
                var converted = new WdlResamplingSampleProvider(new MonoProvider(buffer.ToSampleProvider()), 16000);
                instance.DataAvailable += (_, args) =>
                {
                    if (!ReferenceEquals(instance, Volatile.Read(ref capture))) return;
                    try
                    {
                        buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                        var output = new float[4096];
                        int count;
                        while ((count = converted.Read(output, 0, output.Length)) > 0)
                        {
                            var samples = output.AsSpan(0, count).ToArray();
                            var maximum = 0f;
                            foreach (var sample in samples) maximum = Math.Max(maximum, Math.Abs(sample));
                            Volatile.Write(ref peak, maximum);
                            Interlocked.Exchange(ref lastBuffer, Stopwatch.GetTimestamp());
                            DeliverSamples(samples);
                        }
                    }
                    catch (Exception ex)
                    {
                        TryLog($"{kind} capture buffer failed.", ex);
                        ScheduleRestart(instance);
                    }
                };
                instance.RecordingStopped += (_, args) =>
                {
                    if (args.Exception is null) return;
                    TryLog($"{kind} capture stopped unexpectedly.", args.Exception);
                    ScheduleRestart(instance);
                };
                instance.StartRecording();
            }
            catch (Exception ex)
            {
                TryLog($"{kind} capture unavailable; retrying in 1 second.", ex);
                if (replacing || failed is not null) ReleaseCapture();
            }
            finally { next?.Dispose(); }
        }
    }

    private void DeliverSamples(float[] samples)
    {
        var subscribers = Samples;
        if (subscribers is null) return;
        foreach (Action<float[]> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(samples); }
            catch (Exception ex) { TryLog($"{kind} sample subscriber failed; capture continues.", ex); }
        }
    }

    private void ReleaseCapture()
    {
        var old = Interlocked.Exchange(ref capture, null);
        try { old?.Dispose(); }
        catch (Exception ex) { TryLog($"Could not dispose {kind} capture.", ex); }
        try { device?.Dispose(); }
        catch (Exception ex) { TryLog($"Could not dispose {kind} device.", ex); }
        device = null;
        Volatile.Write(ref peak, 0);
    }

    private static void TryLog(string message, Exception? ex = null)
    {
        try
        {
            if (ex is null) Log.Info(message);
            else Log.Error(message, ex);
        }
        catch (Exception) { /* Logging must not prevent device recovery. */ }
    }

    public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
    {
        if (dataFlow == flow) ScheduleRestart();
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ScheduleRestart();
    public void OnDeviceAdded(string deviceId) => ScheduleRestart();
    public void OnDeviceRemoved(string deviceId) => ScheduleRestart();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            retry?.Dispose();
            try { enumerator?.UnregisterEndpointNotificationCallback(this); }
            catch (Exception ex) { TryLog($"Could not unregister {kind} device notifications.", ex); }
            ReleaseCapture();
            enumerator?.Dispose();
            enumerator = null;
        }
    }

    private sealed class MonoProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] input = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            if (input.Length < count * channels) input = new float[count * channels];
            var frames = source.Read(input, 0, count * channels) / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++) sum += input[frame * channels + channel];
                buffer[offset + frame] = sum / channels;
            }
            return frames;
        }
    }
}
