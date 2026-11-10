using System.IO;
using NAudio.Wave;

namespace MeetingRecorder.Processing;

internal static class Mixer
{
    internal const int BlockSamples = 16000 * 10;

    public static long Mix(string micWav, string systemWav, string outWav)
    {
        using var micReader = OpenTrack(micWav);
        using var systemReader = OpenTrack(systemWav);
        var mic = micReader?.ToSampleProvider();
        var system = systemReader?.ToSampleProvider();
        using var writer = new WaveFileWriter(outWav, new WaveFormat(16000, 16, 1));
        var micSamples = new float[BlockSamples];
        var systemSamples = new float[BlockSamples];
        long total = 0;

        while (true)
        {
            var micCount = mic?.Read(micSamples, 0, BlockSamples) ?? 0;
            var systemCount = system?.Read(systemSamples, 0, BlockSamples) ?? 0;
            var count = Math.Max(micCount, systemCount);
            if (count == 0)
                return total;

            for (var i = 0; i < count; i++)
                micSamples[i] = Math.Clamp((i < micCount ? micSamples[i] : 0f)
                    + (i < systemCount ? systemSamples[i] : 0f), -1f, 1f);
            writer.WriteSamples(micSamples, 0, count);
            total += count;
        }
    }

    private static WaveFileReader? OpenTrack(string path)
    {
        if (!File.Exists(path))
            return null;

        var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        if (format.Encoding == WaveFormatEncoding.Pcm && format.SampleRate == 16000
            && format.Channels == 1 && format.BitsPerSample == 16)
            return reader;

        reader.Dispose();
        throw new InvalidDataException("Working tracks must be 16 kHz mono 16-bit PCM WAV files.");
    }
}
