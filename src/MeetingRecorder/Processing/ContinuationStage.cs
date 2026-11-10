using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MeetingRecorder.Processing;

internal sealed class ContinuationStage : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileIdentity identity;
    private SafeFileHandle? parentLink;

    private ContinuationStage(SafeFileHandle handle, FileIdentity identity) => (this.handle, this.identity) = (handle, identity);

    public static ContinuationStage Create(string path)
    {
        var status = RtlDosPathNameToNtPathName_U_WithStatus(path, out var name, IntPtr.Zero, IntPtr.Zero);
        if (status < 0) throw NtError("Could not resolve continuation stage path", status);
        try
        {
            var namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(name, namePointer, false);
                var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), ObjectName = namePointer, Attributes = 0x40 };
                status = NtCreateFile(out var handle, 0x110080, ref attributes, out _, IntPtr.Zero, 0x80, 3, 2, 0x200021, IntPtr.Zero, 0);
                if (status < 0) { handle?.Dispose(); throw NtError("Could not create continuation stage", status); }
                return Inspect(handle);
            }
            finally { Marshal.FreeHGlobal(namePointer); }
        }
        finally { RtlFreeUnicodeString(ref name); }
    }

    public static ContinuationStage Pin(string path, bool forDeletion = true)
    {
        SafeFileHandle? link = null;
        if (!forDeletion)
        {
            link = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (link.IsInvalid) { link.Dispose(); throw Error("Could not pin continuation series path"); }
        }
        var handle = CreateFile(path, forDeletion ? 0x10080u : 0x80000000u, 3, IntPtr.Zero, 3, forDeletion ? 0x02200000u : 0x02000000u, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); link?.Dispose(); throw Error("Could not pin continuation stage"); }
        try
        {
            var pin = Inspect(handle);
            pin.parentLink = link;
            return pin;
        }
        catch { link?.Dispose(); throw; }
    }

    private static ContinuationStage Inspect(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) { handle.Dispose(); throw Error("Could not inspect continuation stage"); }
        if ((info.Attributes & 0x410) != 0x10) { handle.Dispose(); throw new IOException("Continuation stage is not a normal directory."); }
        return new ContinuationStage(handle, new FileIdentity(info.Volume, info.IndexHigh, info.IndexLow));
    }

    public bool SameAs(ContinuationStage other) => identity == other.identity;

    public void Delete(string path)
    {
        foreach (var name in new[] { "transcript.md", "transcript.txt", "recording-transcript.txt", "recording.mp3", "recording.m4a", "notes.txt", "recording-notes.txt", "combined.txt" })
            File.Delete(Path.Combine(path, name));
        var disposition = 1;
        if (!SetFileInformationByHandle(handle, 4, ref disposition, sizeof(int))) throw Error("Could not remove continuation stage");
    }

    public void Dispose() { handle.Dispose(); parentLink?.Dispose(); }

    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));
    private static IOException NtError(string message, int status) => new(message, new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
    private readonly record struct FileIdentity(uint Volume, uint High, uint Low);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status, Information; }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlDosPathNameToNtPathName_U_WithStatus(string dosName, out UnicodeString ntName, IntPtr filePart, IntPtr relativeName);
    [DllImport("ntdll.dll")]
    private static extern void RtlFreeUnicodeString(ref UnicodeString name);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock ioStatus,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref int information, int size);
}
