using System.IO;

namespace MeetingRecorder;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(AppPaths.LogFile, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        }
    }
}
