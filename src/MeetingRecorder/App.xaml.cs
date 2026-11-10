using System.Windows;
using System.Windows.Interop;

namespace MeetingRecorder;

public partial class App : Application
{
    private StartupOrchestrator? startup;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Pre-main prompts (handoff errors, install consent) must not trigger OnLastWindowClose shutdown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var fx = StartupEffects.Production;
        startup = new StartupOrchestrator();
        switch (startup.Run(e.Args, fx))
        {
            case PreWindowResult.ExitDuplicate:
                startup.Instance!.ActivateExisting();
                MessageBox.Show(Ui.Get("AlreadyRunning"),
                    "Meeting Recorder for Windows", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            case PreWindowResult.ExitInstallLaunched:
                Shutdown(); // Normal source shutdown: OnExit releases the guard for the waiting local copy.
                return;
        }
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.SourceInitialized += (_, _) =>
        {
            var activation = startup.Instance!.ActivationMessage; // registered once here, never per window message
            if (activation == 0) return;
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(
                (nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    if ((uint)message == activation)
                    {
                        window.RestoreMain();
                        handled = true;
                    }
                    return 0;
                });
        };
        window.Show();
        // Restore normal main-window behavior only once the real main window is shown.
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        if (startup.PostInstall)
            _ = PortableInstallStartup.OfferPinAsync(window, fx); // One-time, owned by the visible local window.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        startup?.DisposeInstance();
        base.OnExit(e);
    }

    public App()
    {
        Ui.Initialize();
        DispatcherUnhandledException += (_, e) =>
        {
            ShowError("UnexpectedError", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowError("FatalError", e.ExceptionObject as Exception);
    }

    internal static void ShowError(string key, Exception? exception = null, params object[] args)
    {
        try { Log.Error(Ui.Format(key, System.Globalization.CultureInfo.GetCultureInfo("en"), args), exception); }
        catch (Exception) { /* Show the original error even if the log cannot be written. */ }
        MessageBox.Show(Ui.Format("ErrorDetails", Ui.Format(key, args), exception?.Message ?? "", AppPaths.LogFile),
            "Meeting Recorder for Windows", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
