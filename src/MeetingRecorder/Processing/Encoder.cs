using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingRecorder.Processing;

internal static class Encoder
{
    static Encoder() => MediaFoundationApi.Startup();

    public static string Encode(string mixedWav, string outputFolder)
    {
        var mp3 = Path.Combine(outputFolder, "recording.mp3");
        foreach (var sampleRate in new[] { 16000, 22050, 24000 })
        {
            try
            {
                var bitrates = MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_MP3, sampleRate, 1);
                if (bitrates.Length == 0) continue;
                using var reader = new WaveFileReader(mixedWav);
                MediaFoundationEncoder.EncodeToMp3(Resample(reader, sampleRate), mp3, bitrates.Min());
                return mp3;
            }
            catch (Exception ex)
            {
                // Windows installations differ in their available MP3 encoder formats.
                try { Log.Error($"MP3 encoding failed at {sampleRate} Hz", ex); }
                catch (Exception) { }
                try { File.Delete(mp3); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var aac = Path.Combine(outputFolder, "recording.m4a");
        using var aacReader = new WaveFileReader(mixedWav);
        MediaFoundationEncoder.EncodeToAac(Resample(aacReader, 48000), aac, 96000);
        return aac;
    }

    private static IWaveProvider Resample(WaveFileReader reader, int sampleRate)
    {
        if (reader.WaveFormat.Channels != 1)
            throw new InvalidDataException("Mixed audio must be mono.");
        var samples = reader.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != sampleRate)
            samples = new WdlResamplingSampleProvider(samples, sampleRate);
        return samples.ToWaveProvider16();
    }
}
