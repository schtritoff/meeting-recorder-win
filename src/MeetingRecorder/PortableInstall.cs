using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace MeetingRecorder;

/// <summary>
/// Portable self-install helpers: pure offer policy, atomic local copy of the running executable and the
/// bounded handoff wait for the source process. No installer, updater, installed-hash ledger or guard
/// bypass; startup orchestration (consent, launch, prompts) is wired up separately in App.
/// </summary>
internal static class PortableInstall
{
    /// <summary>Bounded wait for the source process to exit before the local copy takes the normal guard.</summary>
    internal static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Narrow process seam for tests: returns the UTC start ticks of a live process, null when it has already
    /// exited; throwing means the PID is invalid or inaccessible. Production uses the real OS query.
    /// </summary>
    internal static Func<int, long?>? StartTimeProbeOverride;

    /// <summary>
    /// Pure policy only — no filesystem I/O. A set home override (even empty) suppresses the whole feature; the
    /// opt-out flag, an already-local/equivalent process path, invalid paths and a non-local (UNC/device) target
    /// mean no offer. An ordinary UNC source is allowed (the primary network launch) and never compared by I/O.
    /// </summary>
    internal static bool ShouldOffer(string? homeOverride, string? processPath, string installedPath, bool dontAsk)
    {
        if (homeOverride is not null) return false;
        if (dontAsk) return false;
        var source = Normalize(processPath, allowUnc: true);
        var target = Normalize(installedPath, allowUnc: false);
        if (source is null || target is null) return false;
        return !string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? path, bool allowUnc)
    {
        var full = Rooted(path, allowUnc);
        return full is null ? null : Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// Atomically copies the source executable onto the fixed local target: a unique same-directory
    /// temporary is written, flushed to disk and closed, then moved with overwrite. Throws on any failure and
    /// never modifies the source; reparse-point destinations and source/target aliases of the same file are
    /// rejected, and only the owned temporary is deleted on failure. A locked target fails the final move
    /// and is left unchanged.
    /// </summary>
    internal static void CopyToLocal(string source, string target)
    {
        var sourceFull = Rooted(source, allowUnc: true) ?? throw new IOException($"Invalid source path '{source}'.");
        var targetFull = Rooted(target, allowUnc: false) ?? throw new IOException($"Invalid target path '{target}'.");
        if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source and target are the same path.");
        if (!File.Exists(sourceFull)) throw new IOException($"Source not found '{sourceFull}'.");
        if (File.ResolveLinkTarget(sourceFull, true) is not null)
            throw new IOException($"Source is a filesystem link '{sourceFull}'.");
        if (Directory.Exists(targetFull)) throw new IOException($"Target is a directory '{targetFull}'.");
        if (File.Exists(targetFull) && File.ResolveLinkTarget(targetFull, true) is not null)
            throw new IOException($"Target is a filesystem link '{targetFull}'.");
        var directory = Path.GetDirectoryName(targetFull)!;
        if (Directory.Exists(directory))
        {
            if (Directory.ResolveLinkTarget(directory, true) is not null)
                throw new IOException($"Target directory is a reparse point '{directory}'.");
        }
        else Directory.CreateDirectory(directory);
        if (SameFile(sourceFull, targetFull))
            throw new IOException("Source and target are aliases of the same file.");

        var temporary = Path.Combine(directory, Path.GetFileName(targetFull) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(sourceFull, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            } // closed before the atomic move
            File.Move(temporary, targetFull, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* best effort: delete only the owned temporary */ }
            throw;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the exact source process (pid + UTC start ticks) to exit.
    /// True when it has already exited (normal) or exits in time; false on invalid/inaccessible identity, a
    /// live reused PID or timeout. Never touches the single-instance guard — the caller acquires it normally.
    /// </summary>
    internal static async Task<bool> WaitForSourceExitAsync(int pid, long startTimeUtcTicks, TimeSpan timeout)
    {
        if (pid <= 0 || startTimeUtcTicks <= 0) return false;
        var probe = StartTimeProbeOverride ?? ProbeLiveStartTime;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            long? start;
            try { start = probe(pid); }
            catch { return false; } // invalid or inaccessible process information
            if (start is null) return true; // already exited: proceed to the ordinary guard
            if (start.Value != startTimeUtcTicks) return false; // live PID reuse
            if (watch.Elapsed >= timeout) return false;
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    private static long? ProbeLiveStartTime(int pid)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; } // not running: already exited
        using (process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                if (process.HasExited) return null; // exited in the race window
                throw; // inaccessible live process
            }
        }
    }

    private static string? Rooted(string? path, bool allowUnc)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // Device namespaces (\\?\ and \\.\) are never used; ordinary UNC is allowed for a source only.
                if (full.Length >= 3 && full[2] is '?' or '.') return null;
                if (!allowUnc) return null;
            }
            return full;
        }
        catch { return null; }
    }

    private static bool SameFile(string first, string second)
    {
        var a = FileId(first);
        var b = File.Exists(second) ? FileId(second) : null;
        return a is not null && b is not null && a == b;
    }

    // Volume serial + file index identify the underlying file, catching hard-link aliases of the source.
    private static (uint Volume, ulong Index)? FileId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(stream.SafeFileHandle, out var info)
                ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                : null;
        }
        catch { return null; } // locked/inaccessible: identity simply cannot be proven equal here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
