using System.IO;
using System.Runtime.InteropServices;

namespace MeetingRecorder.Native;

internal static class NativeLibraries
{
    private static readonly object Gate = new();
    private static readonly TranscribeNative.LogCallback LogCallback = WriteNativeLog;
    private static bool resolverRegistered;
    private static bool loaded;

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (loaded)
                return;

            var assembly = typeof(NativeLibraries).Assembly;
            var directory = AppPaths.Native;
            Directory.CreateDirectory(directory);
            foreach (var name in assembly.GetManifestResourceNames()
                         .Where(name => name.StartsWith("native/", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                using var resource = assembly.GetManifestResourceStream(name)!;
                var target = Path.Combine(directory, Path.GetFileName(name));
                if (File.Exists(target) && new FileInfo(target).Length == resource.Length)
                    continue;

                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = File.Create(temporary))
                        resource.CopyTo(output);
                    try
                    {
                        File.Move(temporary, target, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        && File.Exists(target) && new FileInfo(target).Length == resource.Length)
                    {
                        // Another process may already have loaded the correctly sized DLL.
                    }
                }
                finally
                {
                    File.Delete(temporary);
                }
            }

            if (!resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "transcribe"
                    ? NativeLibrary.Load(Path.Combine(directory, "transcribe.dll"))
                    : IntPtr.Zero);
                resolverRegistered = true;
                TranscribeNative.transcribe_log_set(LogCallback, IntPtr.Zero);
            }
            TranscribeNative.Check(TranscribeNative.transcribe_init_backends(directory));
            loaded = true;
        }
    }

    private static void WriteNativeLog(int level, nint message, nint userData)
    {
        if (level is not (2 or 3))
            return;
        try
        {
            Log.Info(Marshal.PtrToStringUTF8(message)?.TrimEnd() ?? "");
        }
        catch (Exception)
        {
            // Never let a logging failure escape through the native callback.
        }
    }
}
