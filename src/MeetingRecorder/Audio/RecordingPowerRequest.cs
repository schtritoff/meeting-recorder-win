using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeetingRecorder.Audio;

internal enum RecordingPowerRequestType
{
    DisplayRequired = 0,
    SystemRequired = 1
}

internal interface IRecordingPowerRequestNative
{
    nint Create(string reason);
    void Set(nint handle, RecordingPowerRequestType type);
    void Clear(nint handle, RecordingPowerRequestType type);
    void Close(nint handle);
}

internal sealed class RecordingPowerRequest : IDisposable
{
    private readonly IRecordingPowerRequestNative native;
    private readonly string reason;
    private nint handle;

    internal RecordingPowerRequest(IRecordingPowerRequestNative native, string reason)
    {
        this.native = native;
        this.reason = reason;
    }

    internal bool IsHeld { get; private set; }

    internal void Acquire()
    {
        if (handle != 0) throw new InvalidOperationException("Power request already acquired.");
        handle = native.Create(reason);
        try
        {
            native.Set(handle, RecordingPowerRequestType.SystemRequired);
            IsHeld = true;
        }
        catch
        {
            native.Close(handle);
            handle = 0;
            throw;
        }
    }

    public void Dispose()
    {
        if (handle == 0) return;
        var current = handle;
        var held = IsHeld;
        handle = 0;
        IsHeld = false;
        try { if (held) native.Clear(current, RecordingPowerRequestType.SystemRequired); }
        finally { native.Close(current); }
    }
}

internal sealed class WindowsRecordingPowerRequestNative : IRecordingPowerRequestNative
{
    public nint Create(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = 0, Flags = 1, Reason = new ReasonUnion { SimpleReasonString = text } };
            var handle = PowerCreateRequest(ref context);
            if (handle == 0 || handle == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }
        finally { Marshal.FreeHGlobal(text); }
    }

    public void Set(nint handle, RecordingPowerRequestType type)
    {
        if (!PowerSetRequest(handle, type)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Clear(nint handle, RecordingPowerRequestType type)
    {
        if (!PowerClearRequest(handle, type)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Close(nint handle)
    {
        if (!CloseHandle(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public ReasonUnion Reason;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct ReasonUnion
    {
        [FieldOffset(0)] public nint SimpleReasonString;
        [FieldOffset(0)] public DetailedReason Detailed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DetailedReason
    {
        public nint LocalizedReasonModule;
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public nint ReasonStrings;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint handle, RecordingPowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint handle, RecordingPowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
