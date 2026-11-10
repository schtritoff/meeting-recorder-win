using System.Runtime.InteropServices;

namespace MeetingRecorder.Native;

internal static class TranscribeNative
{
    private const string Library = "transcribe";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, nint message, nint userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_log_set(LogCallback callback, nint userData);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_init_backends([MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_status_string(int status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_model_load_params_init(nint parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_run_params_init(nint parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint load, nint sessionParameters, out nint session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_run(nint session, nint pcm, int count, nint parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint transcribe_full_text(nint session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_session_free(nint session);

    internal static void Check(int status)
    {
        if (status != 0)
            throw new InvalidOperationException($"Transcription failed: {Marshal.PtrToStringUTF8(transcribe_status_string(status))} ({status}).");
    }

    internal static bool HasResult(int status) => status is 0 or 18 or 19;

    internal static string ReadRunResult(int status, Func<string> readText)
    {
        if (!HasResult(status)) Check(status);
        if (status != 0)
        {
            try { Log.Warning($"Transcription returned partial text ({status}); keeping the available result."); }
            catch (Exception) { /* A logging failure must not discard partial speech. */ }
        }
        return readText();
    }
}

internal sealed class SpeechEngine : IDisposable
{
    private readonly object gate = new();
    private nint session;
    private nint language;

    public unsafe SpeechEngine(string modelPath)
    {
        NativeLibraries.EnsureLoaded();
        byte* load = stackalloc byte[256];
        TranscribeNative.transcribe_model_load_params_init((nint)load);
        Marshal.WriteInt32((nint)load, 8, 1); // CPU backend in transcribe.cpp v0.4.1.
        TranscribeNative.Check(TranscribeNative.transcribe_open(modelPath, (nint)load, IntPtr.Zero, out session));
        try
        {
            language = Marshal.StringToCoTaskMemUTF8("hr");
        }
        catch
        {
            TranscribeNative.transcribe_session_free(session);
            throw;
        }
    }

    public unsafe string Recognize(ReadOnlySpan<float> pcm16kMono)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(session == IntPtr.Zero, this);
            byte* run = stackalloc byte[256];
            TranscribeNative.transcribe_run_params_init((nint)run);
            Marshal.WriteIntPtr((nint)run, 32, language); // UTF-8 language pointer in transcribe.cpp v0.4.1.
            int status;
            fixed (float* pcm = pcm16kMono)
                status = TranscribeNative.transcribe_run(session, (nint)pcm, pcm16kMono.Length, (nint)run);
            return TranscribeNative.ReadRunResult(status,
                () => Marshal.PtrToStringUTF8(TranscribeNative.transcribe_full_text(session))?.Trim() ?? "");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (session == IntPtr.Zero)
                return;
            TranscribeNative.transcribe_session_free(session);
            session = IntPtr.Zero;
            Marshal.FreeCoTaskMem(language);
            language = IntPtr.Zero;
        }
    }
}
