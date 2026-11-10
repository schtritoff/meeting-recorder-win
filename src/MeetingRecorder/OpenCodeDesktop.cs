using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MeetingRecorder;

internal enum OpenCodeDesktopStatus { Ready, Installed, ManualUpdate, Failed, Cancelled, Launched, LaunchFailed }

internal sealed record OpenCodeDesktopLocation(string ExePath, Version? Version);

/// <summary>
/// Expected outcomes are returned, not thrown. Code is a bounded diagnostic, never a raw error or path. PartialInstall
/// means the installer was stopped mid-run and Desktop may be partly installed; ExePath being set means it is now discoverable.
/// </summary>
internal sealed record OpenCodeDesktopResult(OpenCodeDesktopStatus Status, string? ExePath = null, Version? Version = null, string? Code = null, bool PartialInstall = false);

/// <summary>Raised by the bounded process runner after it killed the installer (on cancel or on timeout).</summary>
internal sealed class InstallerInterruptedException(bool cancelled) : Exception("The installer was stopped before it finished.")
{
    internal bool Cancelled { get; } = cancelled;
}

/// <summary>
/// Finds OpenCode Desktop, checks it meets the minimum bridge version, installs the newest stable V2 release when it is
/// missing, and starts it without arguments. Sessions are owned by the plugin's Desktop client, so the CLI is never run.
/// Every OS effect goes through <see cref="OpenCodeDesktopEffects"/>; tests substitute fakes and never reach the network.
/// </summary>
internal static class OpenCodeDesktop
{
    internal static readonly Version MinimumVersion = new(2, 2, 5);
    internal static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(10);
    internal const string AssetName = "opencode-desktop-win-x64.exe";
    internal const string ReleasesUrl = "https://api.github.com/repos/anomalyco/opencode/releases?per_page=100";
    internal static readonly string[] AllowedHosts = ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    private const int MaxRedirects = 5;
    private const string DesktopExe = "OpenCode.exe";
    private const string HelperName = "opencode-cli.cmd";
    private const string CliSuffix = "-x64";
    private const string UserAgent = "MeetingRecorder";
    private static readonly Regex AbsoluteDesktopPath = new(@"[A-Za-z]:\\[^""\r\n]*?OpenCode\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Finds the Desktop exe: known install, registered protocol, then PATH-helper text. Candidates must be named OpenCode.exe and exist.</summary>
    internal static OpenCodeDesktopLocation? Locate(OpenCodeDesktopEffects fx)
    {
        var local = fx.Variable("LOCALAPPDATA");
        var appData = fx.Variable("APPDATA");
        var candidates = new List<string>();
        if (local is not null) candidates.Add(Path.Combine(local, "Programs", "ai.opencode.desktop", DesktopExe));
        if (QuotedExecutable(fx.ProtocolCommand()) is { } registered) candidates.Add(registered);
        candidates.AddRange(HelperHints(fx, local, appData));
        var exe = candidates.FirstOrDefault(path => Path.GetFileName(path).Equals(DesktopExe, StringComparison.OrdinalIgnoreCase) && fx.FileExists(path));
        return exe is null ? null : new OpenCodeDesktopLocation(exe, DesktopVersion(exe, appData, fx));
    }

    /// <summary>Ready at or above the minimum; older or unknown versions are a manual update and are never upgraded silently.</summary>
    internal static async Task<OpenCodeDesktopResult> EnsureInstalledAsync(OpenCodeDesktopEffects fx, CancellationToken ct)
    {
        var found = Locate(fx);
        if (found is not null) return Assess(found, OpenCodeDesktopStatus.Ready);

        var installer = Path.Combine(fx.TempDirectory(), "opencode-desktop-" + Guid.NewGuid().ToString("N") + ".exe");
        bool? interruptedByCancel = null;
        try
        {
            var asset = await SelectInstallerAsync(fx, ct);
            if (asset is null) return new(OpenCodeDesktopStatus.Failed, Code: "no-compatible-release");
            Directory.CreateDirectory(fx.TempDirectory());
            await DownloadAsync(fx, asset, installer, ct);
            if (await fx.RunProcess(InstallerStartInfo(installer), ct) is not 0)
                return new(OpenCodeDesktopStatus.Failed, Code: "installer-failed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(OpenCodeDesktopStatus.Cancelled);
        }
        catch (InstallerInterruptedException ex)
        {
            interruptedByCancel = ex.Cancelled;
        }
        catch (DownloadRejectedException)
        {
            return new(OpenCodeDesktopStatus.Failed, Code: "download-host-rejected");
        }
        catch (DownloadStalledException)
        {
            return new(OpenCodeDesktopStatus.Failed, Code: "download-stalled");
        }
        catch (InvalidDataException)
        {
            return new(OpenCodeDesktopStatus.Failed, Code: "installer-too-large");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or JsonException)
        {
            return new(OpenCodeDesktopStatus.Failed, Code: "download-failed");
        }
        finally
        {
            OpenCodePluginSetup.DeleteQuietly(installer);
        }

        var installed = Locate(fx);
        if (interruptedByCancel is { } cancelled)
            return new(cancelled ? OpenCodeDesktopStatus.Cancelled : OpenCodeDesktopStatus.Failed,
                installed?.ExePath, installed?.Version, "installer-interrupted", PartialInstall: true);
        return installed is null
            ? new(OpenCodeDesktopStatus.Failed, Code: "not-found-after-install")
            : Assess(installed, OpenCodeDesktopStatus.Installed);
    }

    /// <summary>Starts Desktop with no arguments; Electron's single-instance lock focuses an existing window.</summary>
    internal static OpenCodeDesktopResult Launch(OpenCodeDesktopLocation location, OpenCodeDesktopEffects fx) =>
        fx.StartProcess(DesktopStartInfo(location.ExePath))
            ? new(OpenCodeDesktopStatus.Launched, location.ExePath, location.Version)
            : new(OpenCodeDesktopStatus.LaunchFailed, location.ExePath, location.Version, "launch-failed");

    /// <summary>Silent per-user NSIS install. The path goes to FileName unmodified; the only argument is /S.</summary>
    internal static ProcessStartInfo InstallerStartInfo(string installer)
    {
        var info = new ProcessStartInfo(installer) { UseShellExecute = false };
        info.ArgumentList.Add("/S");
        return info;
    }

    internal static ProcessStartInfo DesktopStartInfo(string exe) => new(exe) { UseShellExecute = false };

    private static OpenCodeDesktopResult Assess(OpenCodeDesktopLocation location, OpenCodeDesktopStatus ready)
    {
        if (location.Version is null) return new(OpenCodeDesktopStatus.ManualUpdate, location.ExePath, null, "unknown-version");
        return location.Version >= MinimumVersion
            ? new(ready, location.ExePath, location.Version)
            : new(OpenCodeDesktopStatus.ManualUpdate, location.ExePath, location.Version, "below-minimum");
    }

    // File version info first (product, then file); the CLI folder is only a fallback when the exe carries no usable version.
    private static Version? DesktopVersion(string exe, string? appData, OpenCodeDesktopEffects fx)
    {
        var (product, file) = fx.ReadVersionInfo(exe);
        return ParseStable(product) ?? ParseStable(file) ?? HighestCliVersion(appData, fx);
    }

    private static Version? HighestCliVersion(string? appData, OpenCodeDesktopEffects fx)
    {
        if (appData is null) return null;
        return fx.ListSubdirectories(Path.Combine(appData, "ai.opencode.desktop", "cli"))
            .Where(name => name.EndsWith(CliSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(name => ParseStable(name[..^CliSuffix.Length]))
            .OfType<Version>()
            .Max();
    }

    // The helper is read as text only; it is never executed, and only absolute OpenCode.exe paths become candidates.
    private static IEnumerable<string> HelperHints(OpenCodeDesktopEffects fx, string? local, string? appData)
    {
        if (local is null) return [];
        var text = fx.ReadText(Path.Combine(local, "Microsoft", "WindowsApps", HelperName));
        if (text is null) return [];
        var expanded = text.Replace("%LOCALAPPDATA%", local, StringComparison.OrdinalIgnoreCase);
        if (appData is not null) expanded = expanded.Replace("%APPDATA%", appData, StringComparison.OrdinalIgnoreCase);
        return AbsoluteDesktopPath.Matches(expanded).Select(match => match.Value);
    }

    private static string? QuotedExecutable(string? command)
    {
        if (command is null || !command.StartsWith('"')) return null;
        var end = command.IndexOf('"', 1);
        return end > 1 ? command[1..end] : null;
    }

    // Stable semantic version: X.Y.Z only (no pre-release suffix, no fourth component beyond the zero revision).
    private static Version? ParseStable(string? text)
    {
        if (!Version.TryParse(text?.Trim(), out var version) || version.Build < 0 || version.Revision > 0) return null;
        return new Version(version.Major, version.Minor, version.Build);
    }

    private static async Task<InstallerAsset?> SelectInstallerAsync(OpenCodeDesktopEffects fx, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(fx.ListingTimeout);
        using var response = await SendValidatedAsync(fx, new Uri(ReleasesUrl), "application/vnd.github+json", bound.Token);
        response.EnsureSuccessStatusCode();
        var releases = await response.Content.ReadFromJsonAsync<GitHubRelease[]>(bound.Token) ?? [];
        InstallerAsset? best = null;
        foreach (var release in releases)
        {
            if (release.draft || release.prerelease) continue;
            var tag = release.tag_name;
            var version = ParseStable(tag is not null && tag.StartsWith('v') ? tag[1..] : tag);
            if (version is null || version.Major != 2 || version < MinimumVersion) continue;
            var asset = release.assets?.FirstOrDefault(candidate => candidate.name == AssetName);
            if (asset is null || asset.size > fx.MaxInstallerBytes) continue;
            if (!Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) continue;
            if (best is null || version > best.Version) best = new InstallerAsset(version, url);
        }
        return best;
    }

    private static async Task DownloadAsync(OpenCodeDesktopEffects fx, InstallerAsset asset, string target, CancellationToken ct)
    {
        using var response = await SendWithStallBoundAsync(fx, asset.Url, "application/octet-stream", ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > fx.MaxInstallerBytes) throw new InvalidDataException("Installer is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await ReadChunkAsync(fx, input, buffer, ct)) != 0)
        {
            total += count;
            if (total > fx.MaxInstallerBytes) throw new InvalidDataException("Installer is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    // The overall response time is unbounded (HttpClient.Timeout is infinite); only a stall, i.e. no bytes for StallTimeout, fails.
    private static async Task<HttpResponseMessage> SendWithStallBoundAsync(OpenCodeDesktopEffects fx, Uri url, string accept, CancellationToken ct)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(fx.StallTimeout);
        try
        {
            return await SendValidatedAsync(fx, url, accept, headers.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DownloadStalledException();
        }
    }

    private static async Task<int> ReadChunkAsync(OpenCodeDesktopEffects fx, Stream input, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(fx.StallTimeout);
        try
        {
            return await input.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DownloadStalledException();
        }
    }

    // Redirects are followed by hand so every hop is checked: HTTPS only, and only the allowlisted GitHub hosts.
    private static async Task<HttpResponseMessage> SendValidatedAsync(OpenCodeDesktopEffects fx, Uri url, string accept, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            RequireAllowed(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd(accept);
            var response = await fx.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(response.StatusCode)) return response;
            var next = response.Headers.Location;
            response.Dispose();
            if (next is null || hop == MaxRedirects) throw new HttpRequestException("Too many redirects or no redirect target.");
            current = next.IsAbsoluteUri ? next : new Uri(current, next);
        }
    }

    private static void RequireAllowed(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
            throw new DownloadRejectedException();
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private sealed class DownloadRejectedException() : Exception("A download host or redirect is not allowed.");

    private sealed class DownloadStalledException() : Exception("The download stalled.");

    private sealed record InstallerAsset(Version Version, Uri Url);

    private sealed record GitHubRelease(string? tag_name, bool draft, bool prerelease, GitHubAsset[]? assets);

    private sealed record GitHubAsset(string? name, string? browser_download_url, long? size);
}

/// <summary>
/// Narrow injectable OS/network effects; production defaults are the real Desktop locations, registry, file version
/// info, HTTPS client and process starts. Tests substitute fakes so no real download, install or launch happens.
/// </summary>
internal sealed class OpenCodeDesktopEffects
{
    // Automatic redirects are off (validated by hand); the overall timeout is infinite and each step is bounded separately.
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    internal Func<string, string?> Variable = name => Environment.GetEnvironmentVariable(name);
    internal Func<string?> ProtocolCommand = ReadProtocolCommand;
    internal Func<string, bool> FileExists = File.Exists;
    internal Func<string, string?> ReadText = ReadTextDefault;
    internal Func<string, (string? Product, string? File)> ReadVersionInfo = ReadVersionInfoDefault;
    internal Func<string, IReadOnlyList<string>> ListSubdirectories = ListSubdirectoriesDefault;
    internal HttpClient Http = SharedHttp;
    internal TimeSpan ListingTimeout = TimeSpan.FromSeconds(30);
    internal TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    internal Func<string> TempDirectory = () => Path.Combine(AppPaths.Root, "opencode-desktop");
    internal long MaxInstallerBytes = 500L * 1024 * 1024;
    internal Func<ProcessStartInfo, CancellationToken, Task<int?>> RunProcess = (info, ct) => RunToExitAsync(info, ct, OpenCodeDesktop.InstallerTimeout);
    internal Func<ProcessStartInfo, bool> StartProcess = StartDetached;

    private static string? ReadProtocolCommand()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\opencode\shell\open\command", null, null) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    // The helper is a few hundred bytes; anything larger is not a helper this code should read.
    private static string? ReadTextDefault(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= 64 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (string? Product, string? File) ReadVersionInfoDefault(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.ProductVersion, info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static IReadOnlyList<string> ListSubdirectoriesDefault(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory).Select(path => Path.GetFileName(path)).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Runs the child until it exits. On cancel or timeout the child tree is killed and <see cref="InstallerInterruptedException"/>
    /// reports whether it was a cancel. Returns null when the process could not start.
    /// </summary>
    internal static async Task<int?> RunToExitAsync(ProcessStartInfo info, CancellationToken ct, TimeSpan timeout)
    {
        using var process = TryStart(info);
        if (process is null) return null;
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(bound.Token);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw new InstallerInterruptedException(cancelled: ct.IsCancellationRequested);
        }
        return process.ExitCode;
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already exited or not killable */ }
    }

    private static bool StartDetached(ProcessStartInfo info)
    {
        using var process = TryStart(info);
        return process is not null;
    }

    private static Process? TryStart(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
