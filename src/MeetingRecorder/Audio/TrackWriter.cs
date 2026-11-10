using System.Diagnostics;
using NAudio.Wave;

namespace MeetingRecorder.Audio;

internal sealed class TrackWriter : IDisposable
{
    private readonly object gate = new();
    private readonly WaveFileWriter writer;
    private readonly byte[] silence = new byte[8192];
    private long samplesWritten;
    private long lastFlush = Stopwatch.GetTimestamp();
    private bool disposed;

    public TrackWriter(string path) =>
        writer = new WaveFileWriter(path, new WaveFormat(TrackClock.SampleRate, 16, 1));

    public long SamplesWritten
    {
        get { lock (gate) return samplesWritten; }
    }

    public void Write(ReadOnlySpan<float> samples, long expectedNow)
    {
        lock (gate)
        {
            if (disposed)
                return;

            WriteSilence(TrackClock.SilenceToInsert(samplesWritten, expectedNow, samples.Length));
            foreach (var sample in samples)
            {
                writer.WriteSample(Math.Clamp(sample, -1f, 1f));
                samplesWritten++;
            }
            FlushIfDue();
        }
    }

    public void PadTo(long expectedNow)
    {
        lock (gate)
        {
            if (disposed)
                return;

            WriteSilence(expectedNow - TrackClock.ToleranceSamples - samplesWritten);
            FlushIfDue();
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            if (disposed)
                return;

            writer.Flush();
            lastFlush = Stopwatch.GetTimestamp();
        }
    }

    private void WriteSilence(long count)
    {
        while (count > 0)
        {
            var samples = (int)Math.Min(count, silence.Length / 2);
            writer.Write(silence, 0, samples * 2);
            samplesWritten += samples;
            count -= samples;
        }
    }

    private void FlushIfDue()
    {
        if (Stopwatch.GetElapsedTime(lastFlush) >= TimeSpan.FromSeconds(5))
            Flush();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            writer.Dispose();
        }
    }
}
