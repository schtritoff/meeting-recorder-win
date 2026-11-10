using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeetingRecorder;

internal enum OpenCodeHandoffOutcome { Opened, OpenedAfterCancel, Cancelled, Busy, Suppressed, ManualFallback, ManualUpdate, Declined, Failed }

internal enum OpenCodeHandoffPhase { Preparing, Installing, Setup, Starting, WaitingForOpenCode, Opening }

/// <summary>
/// Opened only follows a matching "opened" response. Code is a bounded diagnostic. SessionId is kept whenever Desktop
/// reported one, so manual recovery can open it. SeriesRoot and Title serve manual selection. RetainedBackupPath is set
/// only when a config rollback could not finish safely.
/// </summary>
internal sealed record OpenCodeHandoffResult(
    OpenCodeHandoffOutcome Outcome, string? Code = null, string? SessionId = null, string? Title = null,
    string? SeriesRoot = null, string? RetainedBackupPath = null, bool PartialInstall = false);

/// <summary>The fields of ready.json the readiness check needs; null when the file is missing, partial or malformed.</summary>
internal sealed record OpenCodeHeartbeat(int Version, string? AppVersion, DateTime UpdatedAt, int WindowCount);

internal enum OpenCodeReadiness { Waiting, Ready, UnsupportedHost }

/// <summary>
/// Opens the series' one persistent OpenCode Desktop conversation through the recorder's GUI bridge, following the file
/// protocol shared with OpenCodePlugin/gui-experimental.cjs. One handoff runs per process. Setup and
/// launch happen outside the waits; readiness and the request each get their own deadline. Nothing here prompts, calls a
/// model or reads meeting content. Every OS and clock effect goes through <see cref="OpenCodeHandoffEffects"/>.
/// </summary>
internal static class OpenCodeHandoff
{
    internal static readonly TimeSpan ReadinessWait = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan HeartbeatMaxAge = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan HeartbeatMaxFuture = TimeSpan.FromSeconds(30);

    // The plugin checks title.length, which counts UTF-16 units, so the cut is in units and never splits a character.
    private const int MaxTitleUnits = 512;
    private const string RequestFile = "request.json";
    private static readonly Regex RootPattern = new(@"^[A-Za-z]:\\(?:[^\\/:*?""<>|\x00]+\\)*[^\\/:*?""<>|\x00]+$", RegexOptions.CultureInvariant);
    private static readonly Regex SessionPattern = new(@"^ses_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly string[] Statuses = ["opened", "opened-after-cancel", "cancelled", "failed"];
    private static int busy;

    /// <summary>
    /// Runs one handoff. Consent is asked only when the config must change. A call made while another runs returns
    /// Busy at once. Suppressed (isolated MEETING_RECORDER_HOME) does no setup, launch or request.
    /// </summary>
    internal static async Task<OpenCodeHandoffResult> OpenAsync(string seriesFolder, string title, CancellationToken ct, Func<bool> consent, IProgress<OpenCodeHandoffPhase>? progress = null, OpenCodeHandoffEffects? effects = null)
    {
        var fx = effects ?? OpenCodeHandoffEffects.Production;
        if (!fx.IsEnabled()) return new(OpenCodeHandoffOutcome.Suppressed);
        if (fx.HomeOverride() is not null) return new(OpenCodeHandoffOutcome.Suppressed);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return new(OpenCodeHandoffOutcome.Busy);
        try
        {
            return await new Run(fx, ct, consent, progress).ExecuteAsync(seriesFolder, title);
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    /// <summary>The series root as the plugin expects it (drive-letter absolute, no trailing separator), or null.</summary>
    internal static string? CanonicalRoot(string seriesFolder)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(seriesFolder));
            return RootPattern.IsMatch(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static string TruncateTitle(string? text)
    {
        var builder = new StringBuilder();
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > MaxTitleUnits) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>Readiness from one heartbeat: fresh, with a window and a host at or above the minimum version.</summary>
    internal static OpenCodeReadiness CheckHeartbeat(OpenCodeHeartbeat? beat, DateTime now)
    {
        if (beat is null || beat.Version != 1) return OpenCodeReadiness.Waiting;
        var age = now - beat.UpdatedAt;
        if (age > HeartbeatMaxAge || age < -HeartbeatMaxFuture || beat.WindowCount < 1) return OpenCodeReadiness.Waiting;
        return IsSupportedHost(beat.AppVersion) ? OpenCodeReadiness.Ready : OpenCodeReadiness.UnsupportedHost;
    }

    internal static OpenCodeHeartbeat? ReadHeartbeat(string queue) => ReadJson(Path.Combine(queue, "ready.json"), ParseHeartbeat);

    internal static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool IsSupportedHost(string? appVersion) =>
        Version.TryParse(appVersion, out var version)
        && new Version(version.Major, version.Minor, Math.Max(version.Build, 0)) >= OpenCodeDesktop.MinimumVersion;

    // Whole milliseconds, so the stamp written for createdAt and the one for expiresAt come from the same precision.
    private static DateTime Millis(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static OpenCodeHeartbeat? ParseHeartbeat(JsonElement root)
    {
        var version = IntField(root, "version");
        var windows = IntField(root, "windowCount");
        var updated = TimeField(root, "updatedAt");
        return version is null || windows is null || updated is null
            ? null
            : new OpenCodeHeartbeat(version.Value, StringField(root, "appVersion"), updated.Value, windows.Value);
    }

    private static HandoffResponse? ParseResponse(JsonElement root, string id, string seriesRoot)
    {
        var status = StringField(root, "status");
        var sessionId = StringField(root, "sessionId");
        if (IntField(root, "version") != 1 || StringField(root, "requestId") != id) return null;
        if (!string.Equals(StringField(root, "seriesRoot"), seriesRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (status is null || !Statuses.Contains(status) || TimeField(root, "createdAt") is null) return null;
        // A malformed id is never trusted, but only the opened statuses need one; a failure keeps its code without it.
        if (sessionId is not null && !SessionPattern.IsMatch(sessionId)) sessionId = null;
        if (status is "opened" or "opened-after-cancel" && sessionId is null) return null;
        return new HandoffResponse(status, sessionId, StringField(root, "title"), StringField(root, "code"));
    }

    private static int? IntField(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? StringField(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? TimeField(JsonElement obj, string name) =>
        StringField(obj, name) is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    // Bounded read: a missing, locked, partial, oversized or malformed file is "not there yet", never an exception.
    private static T? ReadJson<T>(string path, Func<JsonElement, T?> parse) where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxJsonBytes) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return parse(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private const int MaxJsonBytes = 64 * 1024;

    private sealed record HandoffResponse(string Status, string? SessionId, string? Title, string? Code);

    private sealed class Run(OpenCodeHandoffEffects fx, CancellationToken ct, Func<bool> consent, IProgress<OpenCodeHandoffPhase>? progress)
    {
        // Guards the state the cancellation callback shares with the async steps. The callback runs on the thread that calls
        // Cancel() (the UI thread when the window closes), so the rollback and the cancel marker never wait for a continuation.
        private readonly object gate = new();
        private OpenCodePluginSetupPaths paths = null!;
        private string root = "";
        private string title = "";
        private OpenCodePluginSetupTransaction? transaction;
        private string? retainedBackup; // set when a rollback could not finish safely; every later RollBack returns it
        private string? outstanding;    // id of the request this run wrote and has not yet cleaned up
        private bool cancelled;         // the callback has run; the async cancel and rollback paths then add nothing

        private string Queue => paths.QueueDirectory;

        internal async Task<OpenCodeHandoffResult> ExecuteAsync(string seriesFolder, string shownTitle)
        {
            var canonical = CanonicalRoot(seriesFolder);
            if (canonical is null) return new(OpenCodeHandoffOutcome.ManualFallback, "unsupported-root");
            if (!Directory.Exists(canonical)) return new(OpenCodeHandoffOutcome.Failed, "series-missing");
            root = canonical;
            title = TruncateTitle(shownTitle);
            paths = fx.Paths();
            using var registration = ct.Register(OnCancelled);
            try
            {
                return await RunAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Done(OpenCodeHandoffOutcome.Cancelled, backup: RollBack());
            }
            catch (Exception)
            {
                // An unexpected fault must still undo a fresh config edit; the exception itself never reaches the UI.
                return Done(OpenCodeHandoffOutcome.Failed, "unexpected", backup: RollBack());
            }
        }

        // Runs synchronously inside Cancel(), so a closing window restores the config and removes its request before any
        // continuation could run. It must not throw: an exception would surface on the cancelling thread.
        private void OnCancelled()
        {
            lock (gate)
            {
                if (cancelled) return;
                cancelled = true;
                try
                {
                    RollBackLocked();
                }
                catch (Exception)
                {
                    // The transaction is still held, so the async path retries the rollback and reports what it cannot undo.
                }
                if (outstanding is { } id)
                {
                    WriteCancelMarker(id);
                    RemoveOwnRequest(id);
                }
            }
        }

        private async Task<OpenCodeHandoffResult> RunAsync()
        {
            progress?.Report(OpenCodeHandoffPhase.Preparing);
            fx.Prepare(root); // context files are optional for the conversation; Prepare reports its own failures

            progress?.Report(OpenCodeHandoffPhase.Installing);
            var desktop = await OpenCodeDesktop.EnsureInstalledAsync(fx.Desktop, ct);
            if (desktop.Status == OpenCodeDesktopStatus.Cancelled) return Done(OpenCodeHandoffOutcome.Cancelled, desktop.Code, partial: desktop.PartialInstall);
            if (desktop.Status == OpenCodeDesktopStatus.ManualUpdate) return Done(OpenCodeHandoffOutcome.ManualUpdate, desktop.Code);
            if (desktop.Status == OpenCodeDesktopStatus.Failed) return Done(OpenCodeHandoffOutcome.Failed, desktop.Code, partial: desktop.PartialInstall);
            var location = new OpenCodeDesktopLocation(desktop.ExePath!, desktop.Version);

            progress?.Report(OpenCodeHandoffPhase.Setup);
            fx.Sweep(paths);
            var setup = fx.Install(paths, consent);
            if (setup.Status == OpenCodePluginSetupStatus.Declined) return Done(OpenCodeHandoffOutcome.Declined);
            if (setup.Status == OpenCodePluginSetupStatus.ManualSetup) return Done(OpenCodeHandoffOutcome.ManualFallback, setup.Code);
            if (setup.Status == OpenCodePluginSetupStatus.Failed) return Done(OpenCodeHandoffOutcome.Failed, setup.Code);
            Adopt(setup.Transaction); // null when the plugin entry already existed

            progress?.Report(OpenCodeHandoffPhase.Starting);
            if (OpenCodeDesktop.Launch(location, fx.Desktop).Status == OpenCodeDesktopStatus.LaunchFailed) return Unverified("launch-failed");

            progress?.Report(OpenCodeHandoffPhase.WaitingForOpenCode);
            switch (await WaitForReadyAsync())
            {
                case OpenCodeReadiness.UnsupportedHost:
                    return Unverified("unsupported-host");
                case OpenCodeReadiness.Waiting:
                    return Unverified(transaction is null ? "possibly-disabled" : "not-ready");
            }
            CommitVerified();

            progress?.Report(OpenCodeHandoffPhase.Opening);
            return await RequestAsync();
        }

        private async Task<OpenCodeReadiness> WaitForReadyAsync()
        {
            var deadline = fx.UtcNow() + ReadinessWait;
            for (; ; )
            {
                ct.ThrowIfCancellationRequested();
                var readiness = CheckHeartbeat(ReadHeartbeat(Queue), fx.UtcNow());
                if (readiness != OpenCodeReadiness.Waiting || fx.UtcNow() >= deadline) return readiness;
                await fx.Delay(PollInterval, ct);
            }
        }

        private async Task<OpenCodeHandoffResult> RequestAsync()
        {
            var id = fx.NewId().ToString("D");
            var created = Millis(fx.UtcNow());
            var deadline = created + RequestWait;
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                requestId = id,
                seriesRoot = root,
                title,
                createdAt = Stamp(created),
                expiresAt = Stamp(deadline),
            });
            if (body.Length > MaxJsonBytes) return Done(OpenCodeHandoffOutcome.Failed, "invalid-request");

            try
            {
                // Under the gate: a cancel that lands first writes nothing, and a later one finds this request and removes it.
                lock (gate)
                {
                    ct.ThrowIfCancellationRequested();
                    OpenCodePluginSetup.RefuseLink(Queue);
                    Directory.CreateDirectory(Queue);
                    OpenCodePluginSetup.ReplaceFile(Path.Combine(Queue, RequestFile), body);
                    outstanding = id;
                }
                fx.Checkpoint?.Invoke("request-written");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Done(OpenCodeHandoffOutcome.Failed, OpenCodePluginSetup.IoError);
            }

            try
            {
                return Mapped(await AwaitResponseAsync(id, deadline, ct), cancelled: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return await CancelAsync(id);
            }
            finally
            {
                Cleanup(id);
            }
        }

        // The synchronous callback has already written the cancel marker and removed the request, so this only waits the
        // grace period for a late answer.
        private async Task<OpenCodeHandoffResult> CancelAsync(string id) =>
            Mapped(await AwaitResponseAsync(id, fx.UtcNow() + CancelGrace, CancellationToken.None), cancelled: true);

        // The cancel marker goes before the request is removed, so the plugin sees the cancel first. A failed marker still
        // lets the request go: the plugin only opens the tab for a request that is still current.
        private void WriteCancelMarker(string id)
        {
            try
            {
                OpenCodePluginSetup.ReplaceFile(CancelPath(id), JsonSerializer.SerializeToUtf8Bytes(new { version = 1, requestId = id, cancelledAt = Stamp(Millis(fx.UtcNow())) }));
                fx.Checkpoint?.Invoke("cancel-written");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the request is removed right after regardless
            }
        }

        private async Task<HandoffResponse?> AwaitResponseAsync(string id, DateTime deadline, CancellationToken token)
        {
            for (; ; )
            {
                token.ThrowIfCancellationRequested();
                var response = ReadJson(Path.Combine(Queue, "response-" + id + ".json"), element => ParseResponse(element, id, root));
                if (response is not null || fx.UtcNow() >= deadline) return response;
                await fx.Delay(PollInterval, token);
            }
        }

        private OpenCodeHandoffResult Mapped(HandoffResponse? response, bool cancelled)
        {
            if (response is null) return cancelled ? Done(OpenCodeHandoffOutcome.Cancelled) : Done(OpenCodeHandoffOutcome.ManualFallback, "timeout");
            if (response.Status is "opened" or "opened-after-cancel")
            {
                var late = cancelled || response.Status == "opened-after-cancel";
                return new(late ? OpenCodeHandoffOutcome.OpenedAfterCancel : OpenCodeHandoffOutcome.Opened, null, response.SessionId, response.Title ?? title, root);
            }
            if (response.Status == "cancelled") return Done(OpenCodeHandoffOutcome.Cancelled, response.Code);
            return Done(OpenCodeHandoffOutcome.ManualFallback, response.Code ?? "failed", response.SessionId);
        }

        // A fresh edit is adopted under the gate. If cancellation already ran, the edit is rolled back at once.
        private void Adopt(OpenCodePluginSetupTransaction? setupTransaction)
        {
            lock (gate)
            {
                transaction = setupTransaction;
                if (ct.IsCancellationRequested) RollBackLocked();
            }
        }

        private void CommitVerified()
        {
            if (!HasTransaction) return;
            fx.Checkpoint?.Invoke("before-commit"); // outside the gate: a checkpoint may cancel
            lock (gate)
            {
                if (transaction is null) return; // a cancellation that landed first already rolled the edit back
                try
                {
                    transaction.Commit();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // readiness is verified; a backup that cannot be deleted is left for the next sweep
                }
                transaction = null;
            }
        }

        private bool HasTransaction
        {
            get
            {
                lock (gate) return transaction is not null;
            }
        }

        private OpenCodeHandoffResult Unverified(string code) => Done(OpenCodeHandoffOutcome.ManualFallback, code, backup: RollBack());

        // Idempotent: the first rollback (from the callback or from the async path) does the work, and later calls return its path.
        private string? RollBack()
        {
            lock (gate)
            {
                RollBackLocked();
                return retainedBackup;
            }
        }

        private void RollBackLocked()
        {
            if (transaction is null) return;
            var result = transaction.Rollback();
            transaction = null;
            retainedBackup = result.RetainedBackupPath;
        }

        // Removes only our own files. The request goes only while it still names our id.
        private void Cleanup(string id)
        {
            lock (gate)
            {
                if (outstanding == id) outstanding = null; // from here on the callback has nothing of ours to write or remove
            }
            OpenCodePluginSetup.DeleteQuietly(ResponsePath(id));
            OpenCodePluginSetup.DeleteQuietly(CancelPath(id));
            RemoveOwnRequest(id);
        }

        private void RemoveOwnRequest(string id)
        {
            var path = Path.Combine(Queue, RequestFile);
            if (ReadJson(path, element => StringField(element, "requestId")) != id) return;
            OpenCodePluginSetup.DeleteQuietly(path);
            fx.Checkpoint?.Invoke("request-removed");
        }

        private string ResponsePath(string id) => Path.Combine(Queue, "response-" + id + ".json");

        private string CancelPath(string id) => Path.Combine(Queue, "cancel-" + id + ".json");

        private OpenCodeHandoffResult Done(OpenCodeHandoffOutcome outcome, string? code = null, string? sessionId = null, string? backup = null, bool partial = false) =>
            new(outcome, code, sessionId, title, root, backup, partial);
    }
}

/// <summary>
/// Narrow injectable effects for <see cref="OpenCodeHandoff"/>. Production defaults are the real clock, Desktop, config
/// and queue; tests replace them with temp paths, a fake clock and a fake plugin, and never reach real config or Desktop.
/// <see cref="Checkpoint"/> names each protocol step so tests can assert ordering.
/// </summary>
internal sealed class OpenCodeHandoffEffects
{
    internal static readonly OpenCodeHandoffEffects Production = new();

    internal Func<bool> IsEnabled = () => Settings.Load().EnableOpenCodeHandoff;
    internal Func<string?> HomeOverride = () => Environment.GetEnvironmentVariable("MEETING_RECORDER_HOME");
    internal Func<DateTime> UtcNow = () => DateTime.UtcNow;
    internal Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;
    internal Func<Guid> NewId = Guid.NewGuid;
    internal Func<OpenCodePluginSetupPaths> Paths = OpenCodePluginSetup.DefaultPaths;
    internal Func<string, OpenCodeContextResult> Prepare = OpenCodeContext.Prepare;
    internal Func<OpenCodePluginSetupPaths, OpenCodePluginSweepResult> Sweep = OpenCodePluginSetup.SweepBackups;
    internal Func<OpenCodePluginSetupPaths, Func<bool>, OpenCodePluginSetupResult> Install = OpenCodePluginSetup.Install;
    internal OpenCodeDesktopEffects Desktop = new();
    internal Action<string>? Checkpoint;
}
