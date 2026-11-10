using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MeetingRecorder;

// Chime and taskbar flash when background work finishes while the user is elsewhere.
internal static class Attention
{
    public static void Notify(Window window)
    {
        if (window.IsActive) return;
        try
        {
            PlaySound("Notification.Default", 0, SndAlias | SndAsync | SndNoDefault);
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = new WindowInteropHelper(window).Handle,
                Flags = FlashAll | FlashUntilForeground
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex) { Log.Error("Could not notify about finished processing.", ex); }
    }

    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndAlias = 0x00010000;
    private const uint FlashAll = 0x3, FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public nint Window; public uint Flags; public uint Count; public uint Timeout; }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, nint module, uint flags);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
