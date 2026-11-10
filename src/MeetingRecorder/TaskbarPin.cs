using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Windows.UI.Shell;
using WinRT;

namespace MeetingRecorder;

/// <summary>
/// Supported taskbar pin offer/request for the unpackaged desktop app through the documented
/// Windows.UI.Shell.TaskbarManager projection. Eligibility follows Microsoft's rules: the
/// ITaskbarManagerDesktopAppSupportStatics marker is present, no LAF token is required (this
/// product holds none), policy allows pinning, and the app is not already pinned. Every
/// unavailability, denial or exception means false; the Desktop shortcut stays the independent
/// fallback. Automated tests inject a fake IPinPlatform and never touch the real taskbar.
/// </summary>
internal static class TaskbarPin
{
    // Official desktop-support marker IID (Microsoft TaskbarManager samples, ITaskbarManagerDesktopAppSupportStatics).
    private static readonly Guid DesktopSupportMarkerIid = new("cdfefd63-e879-4134-b9a7-8283f05f9480");
    // LAF seed value for com.microsoft.windows.taskbar.pin (learn.microsoft.com pin-to-taskbar);
    // a nonzero value means Microsoft requires an issued LAF token, which we do not have.
    private const string LafSeedValue = "4096B239A7295B635C090E647E867B5707DA6AB6CB78340B01FE4E0C8F4953D4";
    private const string LafKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\LimitedAccessFeatures\com.microsoft.windows.taskbar.pin";

    private static readonly SemaphoreSlim requestGate = new(1, 1);

    /// <summary>Narrow OS boundary: the real SDK projection by default, a fake in tests.</summary>
    internal interface IPinPlatform
    {
        bool IsDesktopSupported { get; }
        bool IsLafLocked { get; }
        bool IsPinningAllowed { get; }
        Task<bool> IsCurrentAppPinnedAsync();
        Task<bool> RequestPinCurrentAppAsync();
    }

    /// <summary>True when a user-approved pin request may be offered now; false on any unavailability, denial or error.</summary>
    internal static async Task<bool> CanOfferAsync(IPinPlatform? platform = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) return false; // TaskbarManager requires build 16299+
        platform ??= new SdkPinPlatform();
        try
        {
            if (!platform.IsDesktopSupported || platform.IsLafLocked || !platform.IsPinningAllowed)
                return false;
            return !await platform.IsCurrentAppPinnedAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Requests the pin once; true only when Windows confirms the app is pinned. Concurrent requests are excluded.</summary>
    internal static async Task<bool> RequestAsync(IPinPlatform? platform = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) return false;
        platform ??= new SdkPinPlatform();
        if (!await requestGate.WaitAsync(0).ConfigureAwait(false)) return false; // one request in flight
        try
        {
            if (!platform.IsDesktopSupported || platform.IsLafLocked || !platform.IsPinningAllowed)
                return false;
            return await platform.RequestPinCurrentAppAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
        finally
        {
            requestGate.Release();
        }
    }

    [SupportedOSPlatform("windows10.0.16299.0")]
    private sealed class SdkPinPlatform : IPinPlatform
    {
        public bool IsDesktopSupported =>
            TaskbarManager.As<IWinRTObject>().NativeObject.TryAs<IInspectable.Vftbl>(DesktopSupportMarkerIid, out _) >= 0;

        public bool IsLafLocked
        {
            get
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(LafKeyPath);
                    var value = key?.GetValue(LafSeedValue);
                    return value is not null && Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                }
                catch
                {
                    return false; // Absent/unreadable value means no token is required.
                }
            }
        }

        public bool IsPinningAllowed => TaskbarManager.GetDefault().IsPinningAllowed;

        public async Task<bool> IsCurrentAppPinnedAsync() =>
            await TaskbarManager.GetDefault().IsCurrentAppPinnedAsync().AsTask().ConfigureAwait(false);

        public async Task<bool> RequestPinCurrentAppAsync() =>
            await TaskbarManager.GetDefault().RequestPinCurrentAppAsync().AsTask().ConfigureAwait(false);
    }
}
