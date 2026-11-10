using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MeetingRecorder;

/// <summary>
/// Real Windows Shell Link (.lnk) creation and inspection through the documented
/// Shell Link COM object, its IPropertyStore and standard IPersistFile.
/// </summary>
internal static class WindowsShortcuts
{
    internal const string AppId = "MeetingRecorder.MeetingRecorder";

    // Authoritative Windows SDK identifiers (shlguid.h, shobjidl.h, propkey.h).
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5D3"), 5); // PKEY_AppUserModel_ID
    private const ushort VtLpwstr = 0x1F;
    private const uint ClsctxInprocServer = 0x1;
    private const uint StgmRead = 0x0;

    internal sealed record ShellLinkInfo(string Target, string WorkingDirectory, string IconLocation, string? AppId);

    /// <summary>Returns the full path of an existing link matching target and AppID, or a newly created one.</summary>
    internal static string Create(string directory, string name, string target, string appId)
    {
        var fullTarget = Path.GetFullPath(target);
        foreach (var existing in Directory.EnumerateFiles(directory, "*.lnk"))
        {
            var info = Read(existing);
            if (info is not null
                && string.Equals(Normalize(info.Target), Normalize(fullTarget), StringComparison.OrdinalIgnoreCase)
                && string.Equals(info.AppId ?? "", appId, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(existing);
        }
        var stem = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var path = Path.Combine(directory, stem + ".lnk");
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(directory, stem + "_" + suffix.ToString(CultureInfo.InvariantCulture) + ".lnk");
        var link = (IShellLinkW)CreateShellLink();
        try
        {
            Check(link.SetPath(fullTarget));
            Check(link.SetWorkingDirectory(Path.GetDirectoryName(fullTarget) ?? ""));
            Check(link.SetIconLocation(fullTarget, 0));
            var store = (IPropertyStore)link;
            var key = AppUserModelIdKey;
            var value = new PropVariant { Vt = VtLpwstr, Data = Marshal.StringToCoTaskMemUni(appId) };
            try { Check(store.SetValue(ref key, ref value)); }
            finally { Marshal.FreeCoTaskMem(value.Data); }
            Check(store.Commit());
            Check(((IPersistFile)link).Save(path, true));
        }
        finally { Marshal.FinalReleaseComObject(link); }
        return Path.GetFullPath(path);
    }

    /// <summary>Reads a saved link's metadata; null when the file is not a readable Shell Link.</summary>
    internal static ShellLinkInfo? Read(string path)
    {
        var link = (IShellLinkW)CreateShellLink();
        try
        {
            Check(((IPersistFile)link).Load(Path.GetFullPath(path), StgmRead));
            var target = new StringBuilder(1024);
            Check(link.GetPath(target, target.Capacity, IntPtr.Zero, 0));
            var workingDirectory = new StringBuilder(1024);
            Check(link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity));
            var icon = new StringBuilder(1024);
            Check(link.GetIconLocation(icon, icon.Capacity, out _));
            string? appId = null;
            var store = (IPropertyStore)link;
            var key = AppUserModelIdKey;
            // Split into two statements: an inline `out var` inside Check(...) skips the out-struct copy-back.
            var hrGet = store.GetValue(ref key, out var value);
            Check(hrGet);
            if (value.Vt == VtLpwstr && value.Data != IntPtr.Zero)
            {
                appId = Marshal.PtrToStringUni(value.Data);
                Marshal.FreeCoTaskMem(value.Data);
            }
            return new ShellLinkInfo(target.ToString(), workingDirectory.ToString(), icon.ToString(), appId);
        }
        catch
        {
            return null; // Unreadable or foreign links are simply non-matching.
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Sets this process's explicit AppUserModelID to the stable product ID.</summary>
    internal static void SetProcessAppId()
    {
        var hr = SetCurrentProcessExplicitAppUserModelID(AppId);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static object CreateShellLink()
    {
        var clsid = ClsidShellLink;
        var iid = IidIShellLinkW;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var link);
        Check(hr);
        return link!;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance([In] ref Guid clsid, IntPtr outer, uint context, [In] ref Guid iid, [Out, MarshalAs(UnmanagedType.IUnknown)] out object? link);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FmtId;
        public uint Pid;
        public PropertyKey(Guid fmtId, uint pid) { FmtId = fmtId; Pid = pid; }
    }

    // x64 PROPVARIANT is 24 bytes: 8-byte header (vt + 3 reserved words) + 16-byte union, alignment 8.
    // Data stays at offset 8; counted-array union members extend up to offset 23, so the buffer must be 24.
    // Only VT_LPWSTR is used here.
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Data;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr idList);
        [PreserveSig] int SetIDList(IntPtr idList);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCmd);
        [PreserveSig] int SetShowCmd(int showCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue([In] ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue([In] ref PropertyKey key, [In] ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
