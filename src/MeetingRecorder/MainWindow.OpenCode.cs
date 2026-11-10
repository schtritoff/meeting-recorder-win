using System.Globalization;
using System.IO;
using System.Windows;
using MeetingRecorder.Recording;
using MeetingRecorder.Processing;

namespace MeetingRecorder;

// "Continue in OpenCode" on the Done panel. One handoff runs at a time. Leaving the Done result (New, Start, Continue previous,
// processing, a newer result or closing) cancels it, and a late completion is ignored by identity, so nothing changes under a
// later recording. The handoff call is an injected field, so tests never reach OpenCode, its config or Desktop.
public partial class MainWindow
{
    private Func<string, string, CancellationToken, Func<bool>, IProgress<OpenCodeHandoffPhase>, Task<OpenCodeHandoffResult>> openCodeRun =
        (root, title, token, consent, progress) => OpenCodeHandoff.OpenAsync(root, title, token, consent, progress);
    // Injectable seam: tests answer the consent without a dialog; null means the window's localized Yes/No dialog.
    private Func<bool>? openCodeConsent = null;
    private CancellationTokenSource? openCodeCancellation;

    private void ContinueInOpenCodeClick(object sender, RoutedEventArgs e) => _ = ContinueInOpenCodeAsync();

    private async Task ContinueInOpenCodeAsync()
    {
        var result = output;
        if (!Settings.Load().EnableOpenCodeHandoff || result is null || openCodeCancellation is not null || closed || closeRequested) return;
        var root = result.SeriesFolder; // The series root: the session folder of a flat series, the parent of a dated continuation.
        var title = SeriesTitle(root);
        var cancellation = new CancellationTokenSource();
        openCodeCancellation = cancellation;
        ContinueInOpenCodeButton.IsEnabled = false;
        ShowOpenCodeText(OpenCodePhaseText(OpenCodeHandoffPhase.Preparing, Ui.Culture));
        var progress = new DispatchProgress<OpenCodeHandoffPhase>(Dispatcher, phase =>
        {
            if (IsCurrentOpenCode(cancellation, result)) ShowOpenCodeText(OpenCodePhaseText(phase, Ui.Culture));
        });
        try
        {
            var outcome = await openCodeRun(root, title, cancellation.Token, openCodeConsent ?? ConfirmOpenCodeSetup, progress);
            if (IsCurrentOpenCode(cancellation, result)) ShowOpenCodeText(OpenCodeOutcomeText(outcome, root, title, Ui.Culture));
        }
        catch (Exception ex)
        {
            LogBestEffort("Could not continue in OpenCode.", ex); // Logged for diagnosis; the panel shows only the fixed text.
            if (IsCurrentOpenCode(cancellation, result)) ShowOpenCodeText(OpenCodeOutcomeText(new(OpenCodeHandoffOutcome.Failed), root, title, Ui.Culture));
        }
        finally
        {
            if (ReferenceEquals(openCodeCancellation, cancellation))
            {
                openCodeCancellation = null;
                UpdateOpenCodeAvailability();
            }
            cancellation.Dispose();
        }
    }

    // Cancels an in-flight handoff and clears its status. It never waits for the handoff, so closing is not blocked by it.
    private void CancelOpenCode()
    {
        var cancellation = openCodeCancellation;
        openCodeCancellation = null;
        UpdateOpenCodeAvailability();
        ContinueInOpenCodeStatus.Text = "";
        ContinueInOpenCodeStatus.Visibility = Visibility.Collapsed;
        cancellation?.Cancel();
    }

    private void UpdateOpenCodeAvailability()
    {
        var enabled = Settings.Load().EnableOpenCodeHandoff;
        ContinueInOpenCodeButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ContinueInOpenCodeButton.IsEnabled = enabled;
    }

    private bool IsCurrentOpenCode(CancellationTokenSource cancellation, ProcessingResult result) =>
        !closed && !closeRequested && ReferenceEquals(openCodeCancellation, cancellation) && ReferenceEquals(output, result);

    private void ShowOpenCodeText(string text)
    {
        ContinueInOpenCodeStatus.Text = text;
        ContinueInOpenCodeStatus.Visibility = Visibility.Visible;
    }

    private bool ConfirmOpenCodeSetup() => ConfirmClose(OpenCodeConsentText(OpenCodePluginSetup.DefaultPaths(), Ui.Culture), "No");

    // Runs before the handoff's own guard and the handler is fire-and-forget, so any read problem falls back to the folder
    // name (the same fallback MeetingSeries.Read uses); the handoff reports real failures.
    private static string SeriesTitle(string root)
    {
        try { return MeetingSeries.Read(root).Title; }
        catch (Exception) { return Path.GetFileName(root); }
    }

    internal static string OpenCodeConsentText(OpenCodePluginSetupPaths paths, CultureInfo culture) =>
        Ui.Format("OpenCodeConsent", culture, paths.ConfigDirectory, Path.Combine(paths.PluginsRoot, OpenCodePluginSetup.PluginId));

    private static string OpenCodePhaseText(OpenCodeHandoffPhase phase, CultureInfo culture) => Ui.Get(phase switch
    {
        OpenCodeHandoffPhase.Preparing => "OpenCodePreparing",
        OpenCodeHandoffPhase.Installing => "OpenCodeInstalling",
        OpenCodeHandoffPhase.Setup => "OpenCodeSetup",
        OpenCodeHandoffPhase.Starting => "OpenCodeStarting",
        OpenCodeHandoffPhase.WaitingForOpenCode => "OpenCodeWaiting",
        OpenCodeHandoffPhase.Opening => "OpenCodeOpening",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    }, culture);

    internal static string OpenCodeOutcomeText(OpenCodeHandoffResult result, string seriesRoot, string title, CultureInfo culture)
    {
        switch (result.Outcome)
        {
            case OpenCodeHandoffOutcome.Opened: return Ui.Get("OpenCodeOpened", culture);
            case OpenCodeHandoffOutcome.OpenedAfterCancel: return Ui.Get("OpenCodeOpenedAfterCancel", culture);
            case OpenCodeHandoffOutcome.Cancelled: return Ui.Get("OpenCodeCancelled", culture);
            case OpenCodeHandoffOutcome.Busy: return Ui.Get("OpenCodeBusy", culture);
            case OpenCodeHandoffOutcome.Suppressed: return Ui.Get("OpenCodeSuppressed", culture);
            case OpenCodeHandoffOutcome.ManualUpdate: return Ui.Format("OpenCodeManualUpdate", culture, OpenCodeDesktop.MinimumVersion);
            case OpenCodeHandoffOutcome.Declined:
                return Ui.Get("OpenCodeDeclined", culture) + "\n" + OpenCodeManualText(result, seriesRoot, title, culture);
            default:
                // ManualFallback and Failed. The code is bounded and shown only as a diagnostic.
                var code = result.Code is null ? "" : Ui.Format("OpenCodeCode", culture, result.Code);
                return Ui.Get("OpenCodeFailed", culture) + code + "\n" + OpenCodeManualText(result, seriesRoot, title, culture);
        }
    }

    private static string OpenCodeManualText(OpenCodeHandoffResult result, string seriesRoot, string title, CultureInfo culture)
    {
        var lines = new List<string> { Ui.Format("OpenCodeManual", culture, result.SeriesRoot ?? seriesRoot, title) };
        if (result.SessionId is { } sessionId) lines.Add(Ui.Format("OpenCodeSessionHint", culture, sessionId));
        if (result.RetainedBackupPath is { } backup) lines.Add(Ui.Format("OpenCodeBackupKept", culture, backup));
        if (result.PartialInstall) lines.Add(Ui.Get("OpenCodePartialInstall", culture));
        return string.Join("\n", lines);
    }
}
