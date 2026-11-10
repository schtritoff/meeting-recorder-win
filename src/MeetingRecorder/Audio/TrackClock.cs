namespace MeetingRecorder.Audio;

internal static class TrackClock
{
    public const int SampleRate = 16000;
    public const int ToleranceSamples = 3200;

    public static long SilenceToInsert(long written, long expectedNow, int incoming)
    {
        var gap = expectedNow - incoming - written;
        return gap > ToleranceSamples ? gap : 0;
    }
}
