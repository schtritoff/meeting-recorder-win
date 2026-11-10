namespace MeetingRecorder.Processing;

internal static class Chunker
{
    public const int MaxChunk = 30 * 16000;
    public const int MinCut = 20 * 16000;
    public const int Window = 1600;

    public static int FindCut(ReadOnlySpan<float> chunk)
    {
        var end = Math.Min(chunk.Length, MaxChunk);
        var best = end;
        var bestEnergy = double.MaxValue;
        for (var i = MinCut; i + Window <= end; i += Window / 2)
        {
            double energy = 0;
            for (var j = i; j < i + Window; j++)
                energy += chunk[j] * chunk[j];
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                best = i + Window / 2;
            }
        }
        return best;
    }

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return 0;

        double energy = 0;
        foreach (var sample in samples)
            energy += sample * sample;
        return (float)Math.Sqrt(energy / samples.Length);
    }
}
