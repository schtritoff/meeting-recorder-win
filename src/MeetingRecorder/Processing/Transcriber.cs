using System.IO;
using NAudio.Wave;

namespace MeetingRecorder.Processing;

internal record Segment(TimeSpan Start, string Text);

internal static class Transcriber
{
    public const float SilenceRms = 0.003f;
    private const float TargetPeak = 0.5f;
    private const float MaxGain = 10f;

    public static List<Segment> Run(string mixedWav, Func<float[], string> recognize,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var reader = new WaveFileReader(mixedWav);
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.SampleRate != 16000
            || format.Channels != 1 || format.BitsPerSample != 16)
            throw new InvalidDataException("Mixed audio must be a 16 kHz mono 16-bit PCM WAV file.");

        var segments = new List<Segment>();
        var total = reader.SampleCount;
        if (total == 0)
        {
            progress?.Report(1d);
            return segments;
        }

        var provider = reader.ToSampleProvider();
        var buffer = new float[Chunker.MaxChunk];
        var buffered = 0;
        long consumed = 0;
        while (consumed < total)
        {
            ct.ThrowIfCancellationRequested();
            var target = (int)Math.Min(Chunker.MaxChunk, total - consumed);
            while (buffered < target)
            {
                var read = provider.Read(buffer, buffered, target - buffered);
                if (read == 0)
                    throw new EndOfStreamException("Mixed audio ended before all samples were read.");
                buffered += read;
            }

            var count = consumed + buffered < total ? Chunker.FindCut(buffer.AsSpan(0, buffered)) : buffered;
            var chunk = buffer.AsSpan(0, count).ToArray();
            Boost(chunk);
            if (Chunker.Rms(chunk) >= SilenceRms)
            {
                var text = recognize(chunk).Trim();
                if (text.Length > 0)
                    segments.Add(new Segment(TimeSpan.FromSeconds(consumed / 16000d), text));
            }

            consumed += count;
            buffered -= count;
            Array.Copy(buffer, count, buffer, 0, buffered);
            progress?.Report((double)consumed / total);
        }
        return segments;
    }

    // Quiet recordings are lifted for the recognizer only (never the saved audio). This is level, not
    // speech detection: louder background noise can now pass the gate too.
    private static void Boost(float[] chunk)
    {
        var peak = 0f;
        foreach (var sample in chunk) peak = Math.Max(peak, Math.Abs(sample));
        if (peak == 0 || peak >= TargetPeak) return;
        var gain = Math.Min(MaxGain, TargetPeak / peak);
        for (var i = 0; i < chunk.Length; i++) chunk[i] *= gain;
    }
}
