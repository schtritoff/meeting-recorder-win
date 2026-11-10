using System.IO;

namespace MeetingRecorder;

internal static class AppPaths
{
    public static string Root => Environment.GetEnvironmentVariable("MEETING_RECORDER_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder");
    public static string Models => Path.Combine(Root, "models");
    public static string Native => Path.Combine(Root, "native", "0.4.1");
    public static string Recordings => Path.Combine(Root, "recordings");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "log.txt");
    public static string MeetingsOutput => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meetings");
    public static string ModelFile => Path.Combine(Models, "diktafon-dictate-hr-1-q5_k_m.gguf");
}
