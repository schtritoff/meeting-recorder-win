using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;

namespace MeetingRecorder;

/// <summary>
/// Startup integration for the portable self-install: exact handoff argument parsing, the bounded
/// pre-guard wait for the source process, the pre-main install consent (atomic copy + unconditional
/// Desktop shortcut + local launch) and the one-time post-install taskbar pin offer. Every OS/UI
/// effect goes through <see cref="StartupEffects"/> so tests drive the real orchestration with narrow
/// synthetic seams; production uses the real defaults.
/// </summary>
internal static class PortableInstallStartup
{
    internal const string HandoffFlag = "--install-handoff";

    /// <summary>Exactly `--install-handoff &lt;pid&gt; &lt;ticks&gt;` with positive invariant integers; anything else is false.</summary>
    internal static bool TryParseHandoff(string[] args, out int sourcePid, out long sourceStartTimeUtcTicks)
    {
        sourcePid = 0;
        sourceStartTimeUtcTicks = 0;
        if (args.Length != 3 || !string.Equals(args[0], HandoffFlag, StringComparison.Ordinal)) return false;
        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return false;
        if (!long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0) return false;
        sourcePid = pid;
        sourceStartTimeUtcTicks = ticks;
        return true;
    }

    /// <summary>
    /// Handoff phase, always BEFORE the ordinary guard: an ordinary launch has no handoff flag; a valid
    /// handoff waits (bounded) for the exact source process to exit; invalid arguments or a timeout /
    /// mismatched live parent fail visibly and continue to the normal guard, which is never bypassed.
    /// Returns true when this process is a valid post-install child.
    /// </summary>
    internal static bool HandleHandoff(string[] args, StartupEffects fx)
    {
        if (!args.Contains(HandoffFlag)) return false;
        if (!TryParseHandoff(args, out var pid, out var ticks))
        {
            fx.ReportError("HandoffInvalidArgs", null);
            return false;
        }
        var exited = PortableInstall.WaitForSourceExitAsync(pid, ticks, fx.HandoffTimeout()).GetAwaiter().GetResult();
        if (!exited)
        {
            fx.ReportError("HandoffSourceWaitFailed", null);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Pre-main offer after the guard: consent with the opt-out saved immediately on either answer, atomic
    /// copy, unconditional Desktop shortcut (failures reported, installed EXE kept), local launch with the
    /// private handoff arguments. Returns true only when the local copy launched — then the source exits
    /// normally and OnExit releases the guard.
    /// </summary>
    internal static bool RunInstallOffer(StartupEffects fx)
    {
        if (!fx.IsSingleFileBundle()) return false; // never copy a loose debug apphost into app-data
        var home = fx.HomeOverride();
        var source = fx.ProcessPath();
        var installed = fx.InstalledExePath();
        var settings = fx.LoadSettings();
        if (!PortableInstall.ShouldOffer(home, source, installed, settings.DontAskInstall)) return false;
        var answer = fx.ShowInstallPrompt();
        if (answer.DontAsk)
        {
            try { fx.SaveSettings(settings with { DontAskInstall = true }); }
            catch (Exception ex) { fx.ReportError("InstallPreferenceSaveFailed", ex); } // coordinates preserved in the attempted save
        }
        if (!answer.Install || source is null) return false;
        try { PortableInstall.CopyToLocal(source, installed); }
        catch (Exception ex) { fx.ReportError("InstallCopyFailed", ex); return false; } // the source stays usable
        try { fx.CreateShortcut(fx.DesktopDirectory(), "Meeting Recorder", installed, WindowsShortcuts.AppId); }
        catch (Exception ex) { fx.ReportError("DesktopShortcutFailed", ex); } // visible, but the installed EXE stays
        var (pid, ticks) = fx.CurrentProcessIdentity();
        if (!fx.TryStartLocalExe(installed, pid, ticks)) { fx.ReportError("InstallLaunchFailed", null); return false; }
        return true; // only a successful launch lets the source shut down
    }

    /// <summary>
    /// One-time post-install pin offer on the visible local window; the Start link is created only when
    /// pinning is eligible and its failure never removes the independent Desktop fallback.
    /// </summary>
    internal static async Task OfferPinAsync(Window? owner, StartupEffects fx)
    {
        try
        {
            if (fx.HomeOverride() is not null) return;
            if (!fx.IsSingleFileBundle()) return;
            var installed = fx.InstalledExePath();
            if (!SameLocalPath(fx.ProcessPath(), installed)) return; // only the installed copy, never an ordinary launch
            if (!await fx.CanOfferPin().ConfigureAwait(true)) return;
            try { fx.CreateShortcut(fx.ProgramsDirectory(), "Meeting Recorder", installed, WindowsShortcuts.AppId); }
            catch (Exception ex) { fx.ReportError("StartShortcutFailed", ex); }
            if (fx.ShowPinPrompt(owner)) // one-time, window-owned, foreground prompt
                await fx.RequestPin().ConfigureAwait(true); // explicit foreground user action, exactly once
        }
        catch (Exception ex) { LogBestEffort("The post-install pin offer failed.", ex); }
    }

    // Log.Error appends to a file and can itself throw; a logging failure must never strand startup
    // or fault this fire-and-forget task (same best-effort pattern as MainWindow.LogBestEffort).
    internal static void LogBestEffort(string message, Exception ex)
    {
        try { Log.Error(message, ex); } catch (Exception) { /* Best effort only. */ }
    }

    private static bool SameLocalPath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>Consent result: primary button plus the "don't ask again" checkbox.</summary>
internal readonly record struct InstallAnswer(bool Install, bool DontAsk);

internal enum PreWindowResult { ShowMainWindow, ExitDuplicate, ExitInstallLaunched }

/// <summary>
/// Runs the pre-window startup phase on the dispatcher thread, so the single-instance mutex stays
/// thread-affine between <see cref="Run"/> and <see cref="DisposeInstance"/> (App.OnExit).
/// </summary>
internal sealed class StartupOrchestrator
{
    internal SingleInstance? Instance { get; private set; }
    internal bool PostInstall { get; private set; }

    internal PreWindowResult Run(string[] args, StartupEffects fx)
    {
        PostInstall = PortableInstallStartup.HandleHandoff(args, fx); // exact args + bounded wait BEFORE the guard
        Instance = new SingleInstance(AppPaths.Root);
        if (!Instance.IsOwner) return PreWindowResult.ExitDuplicate; // activate the existing instance; never bypass
        if (fx.HomeOverride() is null) // outside isolated suppression
            try { fx.SetAppId(); } catch (Exception ex) { PortableInstallStartup.LogBestEffort("Could not set the AppUserModelID.", ex); } // before any visible window
        return PortableInstallStartup.RunInstallOffer(fx) ? PreWindowResult.ExitInstallLaunched : PreWindowResult.ShowMainWindow;
    }

    internal void DisposeInstance() => Instance?.Dispose();
}

/// <summary>Narrow injectable startup effects; production defaults are the real OS/UI, tests substitute fakes.</summary>
internal sealed class StartupEffects
{
    internal static readonly StartupEffects Production = new();

    internal Func<string?> HomeOverride = () => Environment.GetEnvironmentVariable("MEETING_RECORDER_HOME");
    internal Func<string?> ProcessPath = () => Environment.ProcessPath;
    internal Func<bool> IsSingleFileBundle = IsSingleFileBundleDefault;
    internal Func<string> InstalledExePath = () => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder", "MeetingRecorder.exe");
    internal Func<string> DesktopDirectory = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    internal Func<string> ProgramsDirectory = () => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    internal Func<TimeSpan> HandoffTimeout = () => PortableInstall.HandoffTimeout;
    internal Func<string, string, string, string, string> CreateShortcut = WindowsShortcuts.Create;
    internal Func<string, int, long, bool> TryStartLocalExe = StartLocalExe;
    internal Func<(int Pid, long Ticks)> CurrentProcessIdentity = CurrentIdentity;
    internal Func<Settings> LoadSettings = Settings.Load;
    internal Action<Settings> SaveSettings = settings => settings.Save();
    internal Func<InstallAnswer> ShowInstallPrompt = () => StartupPromptWindow.AskInstall();
    internal Func<Window?, bool> ShowPinPrompt = owner => StartupPromptWindow.AskPin(owner!);
    internal Func<Task<bool>> CanOfferPin = () => TaskbarPin.CanOfferAsync();
    internal Func<Task<bool>> RequestPin = () => TaskbarPin.RequestAsync();
    internal Action SetAppId = WindowsShortcuts.SetProcessAppId;
    internal Action<string, Exception?> ReportError = (key, ex) => App.ShowError(key, ex);

    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty Location is exactly the single-file bundle signal; a loose apphost keeps a non-empty Location and is never copied.")]
    private static bool IsSingleFileBundleDefault()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly is not null && string.IsNullOrEmpty(assembly.Location);
    }

    private static (int, long) CurrentIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return (process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    private static bool StartLocalExe(string path, int sourcePid, long sourceStartTimeUtcTicks)
    {
        try
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false };
            info.ArgumentList.Add(PortableInstallStartup.HandoffFlag);
            info.ArgumentList.Add(sourcePid.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(sourceStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
            return Process.Start(info) is not null;
        }
        catch { return false; }
    }
}
