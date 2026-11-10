using System.IO;
using System.Text;
using System.Text.Json;

namespace MeetingRecorder;

internal enum OpenCodePluginSetupStatus { Configured, AlreadyConfigured, Declined, ManualSetup, Failed }

internal sealed record OpenCodePluginSetupPaths(string ConfigDirectory, string PluginsRoot, string QueueDirectory, string BackupDirectory);

internal sealed record OpenCodePluginSetupResult(OpenCodePluginSetupStatus Status, string? Code = null, OpenCodePluginSetupTransaction? Transaction = null);

internal sealed record OpenCodePluginRollbackResult(bool Verified, string? RetainedBackupPath);

/// <summary>Complete is false when the scan could not finish (access, IO or link problem); nothing was deleted in that case.</summary>
internal sealed record OpenCodePluginSweepResult(int Removed, IReadOnlyList<string> Retained, bool Complete);

/// <summary>
/// Installs the recorder-owned OpenCode Desktop GUI plugin: extracts the embedded bridge with its queue path
/// substituted, and adds one percent-encoded local file entry to the global OpenCode config after consent.
/// Config edits are byte splices at JSON token offsets, so comments, BOM, line endings and unrelated values survive.
/// The original bytes stay in a private backup until the caller commits or rolls back. Nothing here touches Desktop,
/// sessions or the UI.
/// </summary>
internal static class OpenCodePluginSetup
{
    internal const string PluginId = "meetingrecorder-handoff";
    private const string SourceFileName = "gui-experimental.cjs";
    private const string ResourceName = "opencode/gui-experimental.cjs";
    private const string QueueToken = "\"__MEETINGRECORDER_QUEUE_DIRECTORY__\"";
    private const string SchemaUrl = "https://opencode.ai/config.json";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // Bounded diagnostic codes; raw errors, paths and config contents are never returned.
    internal const string BootstrapInvalid = "bootstrap-invalid";
    internal const string ConfigUnsupported = "config-unsupported";
    internal const string BothConfigFiles = "both-config-files";
    internal const string ConfigChanged = "config-changed";
    internal const string IoError = "io-error";
    internal const string LinkInvalid = "link-invalid";

    /// <summary>Test seam: runs just before the final unchanged-since-read check of a config save or rollback.</summary>
    internal static Action<string>? BeforeWriteHook;

    /// <summary>Test seam for link detection: a link's target (dangling links included), or null for a non-link.</summary>
    internal static Func<string, string?>? LinkTargetOverride;

    /// <summary>
    /// Production paths. Any MEETING_RECORDER_HOME override (even empty, as in PortableInstall.ShouldOffer) moves the
    /// config directory into a sandbox, so isolated runs never reach the real ~/.config/opencode.
    /// </summary>
    internal static OpenCodePluginSetupPaths DefaultPaths()
    {
        var home = Environment.GetEnvironmentVariable("MEETING_RECORDER_HOME");
        var configDirectory = home is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode")
            : Path.Combine(
                Path.IsPathFullyQualified(AppPaths.Root) ? AppPaths.Root : Path.GetTempPath(),
                "opencode-config-sandbox");
        return new(
            configDirectory,
            Path.Combine(AppPaths.Root, "opencode"),
            Path.Combine(AppPaths.Root, "opencode-handoff"),
            Path.Combine(AppPaths.Root, "opencode-backup"));
    }

    /// <summary>
    /// Extracts the plugin, then edits the global config only when no entry exists, consent is given and the config is
    /// a shape this code can splice safely. Consent is asked before any config or plugin write, and a refusal writes nothing.
    /// </summary>
    internal static OpenCodePluginSetupResult Install(OpenCodePluginSetupPaths paths, Func<bool> consent)
    {
        var folder = Path.Combine(paths.PluginsRoot, PluginId);
        var entry = PluginFolderUri(folder);
        try
        {
            var existing = ExistingConfigs(paths.ConfigDirectory);
            var configPath = existing.Length == 1 ? existing[0] : Path.Combine(paths.ConfigDirectory, "opencode.jsonc");
            var linked = existing.Any(path => LinkTarget(path) is not null);
            var original = existing.Length > 0 && !linked ? File.ReadAllBytes(configPath) : null;
            var plan = linked
                ? new ConfigPlan(LinkInvalid, false, null)
                : original is null
                    ? new ConfigPlan(null, false, Utf8.GetBytes(CreationText(entry)))
                    : existing.Length == 2
                        ? PlanBoth(File.ReadAllBytes(Path.Combine(paths.ConfigDirectory, "opencode.json")), original, entry, folder)
                        : PlanEdit(original, entry, folder);

            if (plan.Code is not null || plan.Present)
            {
                ExtractPlugin(paths.PluginsRoot, paths.QueueDirectory);
                return plan.Code is null ? new(OpenCodePluginSetupStatus.AlreadyConfigured) : new(OpenCodePluginSetupStatus.ManualSetup, plan.Code);
            }
            if (!consent()) return new(OpenCodePluginSetupStatus.Declined);

            ExtractPlugin(paths.PluginsRoot, paths.QueueDirectory);
            return Apply(paths, configPath, original, plan.Bytes!, folder);
        }
        catch (ConfigChangedException) { return new(OpenCodePluginSetupStatus.Failed, ConfigChanged); }
        catch (InvalidDataException) { return new(OpenCodePluginSetupStatus.Failed, BootstrapInvalid); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(OpenCodePluginSetupStatus.Failed, IoError); }
    }

    /// <summary>Writes the substituted plugin source to &lt;pluginsRoot&gt;\meetingrecorder-handoff, only when the bytes differ.</summary>
    internal static void ExtractPlugin(string pluginsRoot, string queueDirectory)
    {
        var bytes = Utf8.GetBytes(SubstituteQueueDirectory(ReadEmbeddedSource(), queueDirectory));
        var folder = Path.Combine(pluginsRoot, PluginId);
        RefuseLink(pluginsRoot);
        RefuseLink(folder);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, SourceFileName);
        RefuseLink(file); // before the compare: a link with matching bytes must still be refused
        if (File.Exists(file) && SameBytes(File.ReadAllBytes(file), bytes)) return;
        ReplaceFile(file, bytes);
    }

    /// <summary>Replaces the single quoted queue token with the JSON string literal of the absolute queue path.</summary>
    internal static string SubstituteQueueDirectory(string source, string queueDirectory)
    {
        if (!Path.IsPathFullyQualified(queueDirectory)) throw new InvalidDataException("Queue path must be absolute.");
        if (source.Split(QueueToken).Length - 1 != 1) throw new InvalidDataException("Queue token must occur exactly once.");
        return source.Replace(QueueToken, JsonSerializer.Serialize(queueDirectory));
    }

    /// <summary>
    /// Percent-encoded local file URI of the plugin folder (the directory, not the file). Each segment is escaped
    /// explicitly because Uri(path) would keep an existing "%20" in a folder name unescaped; the drive colon stays literal.
    /// </summary>
    internal static string PluginFolderUri(string folder)
    {
        var segments = Path.GetFullPath(folder).Replace('\\', '/').Split('/');
        var encoded = string.Join("/", segments.Select((segment, index) => index == 0 ? segment : Uri.EscapeDataString(segment)));
        return new Uri("file:///" + encoded).AbsoluteUri;
    }

    /// <summary>True when a plugin entry is a file URI that decodes to the given folder (case-insensitive).</summary>
    internal static bool IsPluginEntry(string? spec, string folder) =>
        spec is not null
        && Uri.TryCreate(spec, UriKind.Absolute, out var uri)
        && uri.IsFile
        && string.Equals(
            Path.TrimEndingDirectorySeparator(uri.LocalPath),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
            StringComparison.OrdinalIgnoreCase);

    private static string CreationText(string entry) =>
        "{\n  \"$schema\": \"" + SchemaUrl + "\",\n  \"plugins\": [" + JsonSerializer.Serialize(entry) + "]\n}\n";

    private static OpenCodePluginSetupResult Apply(OpenCodePluginSetupPaths paths, string configPath, byte[]? original, byte[] edited, string folder)
    {
        string? backup = null;
        if (original is null)
        {
            RefuseLink(paths.ConfigDirectory);
            Directory.CreateDirectory(paths.ConfigDirectory);
        }
        else backup = WriteBackup(paths.BackupDirectory, original);
        try
        {
            GuardedWrite(configPath, original, () => ReplaceFile(configPath, edited));
        }
        catch
        {
            if (backup is not null) DeleteQuietly(backup); // the config was never replaced, so the backup is not needed
            throw;
        }
        return new(OpenCodePluginSetupStatus.Configured, null,
            new OpenCodePluginSetupTransaction(configPath, backup, edited, original is null, folder));
    }

    /// <summary>Runs the hook, then writes only if the file still holds the bytes that were read (no overwrite of concurrent edits).</summary>
    internal static void GuardedWrite(string path, byte[]? expected, Action write)
    {
        BeforeWriteHook?.Invoke(path);
        if (!SameBytes(ReadOrNull(path), expected)) throw new ConfigChangedException();
        write();
    }

    /// <summary>Removes every entry that resolves to the folder, one token-aware splice at a time; null when unsafe to edit.</summary>
    internal static byte[]? RemoveEntries(byte[] data, string folder)
    {
        var current = data;
        while (true)
        {
            var root = ParseRoot(current, out _);
            if (root is null) return null;
            var plugins = root.Members.Where(member => member.Name == "plugins").ToList();
            if (plugins.Count == 0) return current;
            var array = plugins.Count == 1 ? plugins[0].Child : null;
            if (array is null || current[array.Open] != '[') return null;
            var index = array.Members.FindIndex(member => IsPluginEntry(PluginSpec(current, member), folder));
            if (index < 0) return current;
            current = Apply(current, [RemovalOf(current, array, index)]);
        }
    }

    internal static void ReplaceFile(string path, byte[] bytes)
    {
        RefuseLink(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            DeleteQuietly(temporary);
            throw;
        }
    }

    internal static byte[]? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    internal static bool SameBytes(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);

    internal static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch { /* best effort: delete only the owned file */ }
    }

    private static string WriteBackup(string directory, byte[] bytes)
    {
        RefuseLink(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "opencode-config-" + Guid.NewGuid().ToString("N") + ".bak");
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (!SameBytes(File.ReadAllBytes(path), bytes)) throw new IOException("Backup verification failed.");
            return path;
        }
        catch
        {
            DeleteQuietly(path);
            throw;
        }
    }

    private static string ReadEmbeddedSource()
    {
        using var stream = typeof(OpenCodePluginSetup).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Embedded OpenCode plugin is missing.");
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>Refuses an existing file or folder that is a link or junction; the owned path must be a plain target.</summary>
    internal static void RefuseLink(string path)
    {
        if (LinkTarget(path) is not null) throw new IOException("Refusing a linked file or folder.");
    }

    // Immediate target of a link, without requiring the target to exist, so dangling links are seen too.
    private static string? LinkTarget(string path)
    {
        if (LinkTargetOverride is not null) return LinkTargetOverride(path);
        try
        {
            return Directory.Exists(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName
                : File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // no entry at this path at all; a dangling link still has an entry and resolves
        }
    }

    private static string[] ExistingConfigs(string directory) =>
        new[] { "opencode.json", "opencode.jsonc" }
            .Select(name => Path.Combine(directory, name))
            .Where(path => File.Exists(path) || LinkTarget(path) is not null)
            .ToArray();

    /// <summary>
    /// Removes backups left by an interrupted setup, and only where the config proves the backup is not needed: its bytes
    /// equal the config (no edit happened), or the config holds our entry (setup completed) and the backup is the sole one.
    /// Every other backup is kept and its path returned, never its contents. Run before setup.
    /// </summary>
    internal static OpenCodePluginSweepResult SweepBackups(OpenCodePluginSetupPaths paths)
    {
        try
        {
            if (!Directory.Exists(paths.BackupDirectory)) return new(0, [], true);
            if (LinkTarget(paths.BackupDirectory) is not null) return new(0, [], false); // never sweep through a link
            var backups = Directory.GetFiles(paths.BackupDirectory, "opencode-config-*.bak");
            if (backups.Length == 0) return new(0, [], true);

            var folder = Path.Combine(paths.PluginsRoot, PluginId);
            var configs = ExistingConfigs(paths.ConfigDirectory);
            var target = configs.Length == 1 ? configs[0] : Path.Combine(paths.ConfigDirectory, "opencode.jsonc");
            byte[]? config = configs.Length > 0 && configs.All(path => LinkTarget(path) is null) ? ReadOrNull(target) : null;

            var removed = 0;
            var retained = new List<string>();
            foreach (var backup in backups)
            {
                if (!CanDiscard(backup, config, backups.Length, folder)) { retained.Add(backup); continue; }
                try
                {
                    File.Delete(backup);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { retained.Add(backup); }
            }
            return new(removed, retained, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(0, [], false); // the scan could not finish: delete nothing
        }
    }

    private static bool CanDiscard(string backup, byte[]? config, int backupCount, string folder)
    {
        if (config is null) return false;
        try
        {
            if (SameBytes(File.ReadAllBytes(backup), config)) return true; // no edit happened: the config is the original
            var stripped = RemoveEntries(config, folder);
            return backupCount == 1 && stripped is not null && !SameBytes(stripped, config); // setup completed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed record ConfigPlan(string? Code, bool Present, byte[]? Bytes);

    private readonly record struct Member(string? Name, int Start, int End, Container? Child);

    private sealed class Container
    {
        public int Open;
        public int Close;
        public readonly List<Member> Members = [];
    }

    private readonly record struct Edit(int Start, int End, byte[] Bytes);

    // OpenCode itself writes opencode.jsonc when both exist, so that is the target. opencode.json must not hold a
    // plugins/plugin key (its array merge with ours is unverified) and must parse; it is never edited.
    private static ConfigPlan PlanBoth(byte[] json, byte[] jsonc, string entry, string folder)
    {
        if (PlanEdit(json, entry, folder).Present) return new(null, true, null);
        if (PlanEdit(jsonc, entry, folder).Present) return new(null, true, null);
        var root = ParseRoot(json, out _);
        if (root is null || root.Members.Any(member => member.Name is "plugins" or "plugin")) return new(BothConfigFiles, false, null);
        return PlanEdit(jsonc, entry, folder);
    }

    private static ConfigPlan PlanEdit(byte[] data, string entry, string folder)
    {
        var root = ParseRoot(data, out var offset);
        if (root is null) return new(ConfigUnsupported, false, null);
        var plugins = root.Members.Where(member => member.Name == "plugins").ToList();
        var array = plugins.Count == 1 ? plugins[0].Child : null;
        if (plugins.Count > 1 || (plugins.Count == 1 && (array is null || data[array.Open] != '[')))
            return new(ConfigUnsupported, false, null);
        if (array is not null && array.Members.Any(member => IsPluginEntry(PluginSpec(data, member), folder)))
            return new(null, true, null);
        if (root.Members.Any(member => member.Name == "plugin")) return new(ConfigUnsupported, false, null); // deprecated V1 key: merge rules unverified

        if (array is not null)
        {
            var separator = array.Members.Count > 0 && CommaPosition(data, array.Members[^1].End, array.Close) < 0 ? ", " : "";
            return new(null, false, Apply(data, [new Edit(array.Close, array.Close, Utf8.GetBytes(separator + JsonSerializer.Serialize(entry)))]));
        }

        var newline = data.AsSpan(offset).IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        var edits = new List<Edit>();
        if (root.Members.Count > 0 && CommaPosition(data, root.Members[^1].End, root.Close) < 0)
            edits.Add(new Edit(root.Members[^1].End, root.Members[^1].End, Utf8.GetBytes(",")));
        edits.Add(new Edit(root.Close, root.Close,
            Utf8.GetBytes(newline + "  \"plugins\": [" + JsonSerializer.Serialize(entry) + "]" + newline)));
        return new(null, false, Apply(data, edits));
    }

    private static Container? ParseRoot(byte[] data, out int offset)
    {
        offset = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        try
        {
            var reader = new Utf8JsonReader(data.AsSpan(offset), isFinalBlock: true, new JsonReaderState(ReaderOptions));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            var root = Scan(ref reader, data, offset);
            return reader.Read() ? null : root;
        }
        catch (JsonException) { return null; }
    }

    // Reader is positioned on StartObject/StartArray; returns with the reader on the matching end token.
    private static Container Scan(ref Utf8JsonReader reader, byte[] data, int offset)
    {
        var container = new Container { Open = offset + (int)reader.TokenStartIndex };
        string? name = null;
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                container.Close = offset + (int)reader.TokenStartIndex;
                return container;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                name = reader.GetString();
                continue;
            }
            var start = offset + (int)reader.TokenStartIndex;
            Container? child = null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) child = Scan(ref reader, data, offset);
            container.Members.Add(new Member(name, start, offset + (int)reader.BytesConsumed, child));
            name = null;
        }
        throw new JsonException("Unterminated container.");
    }

    // The plugin spec of an entry: a string, or the "package" of an object form. Null for anything else.
    private static string? PluginSpec(byte[] data, Member member)
    {
        using var document = JsonDocument.Parse(data.AsMemory(member.Start, member.End - member.Start), DocumentOptions);
        var element = document.RootElement;
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("package", out var package)
            && package.ValueKind == JsonValueKind.String
                ? package.GetString()
                : null;
    }

    private static Edit RemovalOf(byte[] data, Container array, int index)
    {
        var member = array.Members[index];
        var boundary = index + 1 < array.Members.Count ? array.Members[index + 1].Start : array.Close;
        var comma = CommaPosition(data, member.End, boundary);
        if (comma >= 0) return new Edit(member.Start, comma + 1, Array.Empty<byte>());
        if (index == 0) return new Edit(member.Start, member.End, Array.Empty<byte>());
        return new Edit(CommaPosition(data, array.Members[index - 1].End, member.Start), member.End, Array.Empty<byte>());
    }

    // Offset of the comma between two members, or -1. Only whitespace and comments can sit between them.
    private static int CommaPosition(byte[] data, int from, int to)
    {
        var i = from;
        while (i < to)
        {
            if (data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
                continue;
            }
            if (data[i] == '/' && i + 1 < to && data[i + 1] == '/')
            {
                while (i < to && data[i] != '\n') i++;
                continue;
            }
            if (data[i] == '/' && i + 1 < to && data[i + 1] == '*')
            {
                var close = data.AsSpan(i + 2, to - i - 2).IndexOf("*/"u8);
                if (close < 0) return -1;
                i += close + 4;
                continue;
            }
            return data[i] == ',' ? i : -1;
        }
        return -1;
    }

    private static byte[] Apply(byte[] data, IEnumerable<Edit> edits)
    {
        using var output = new MemoryStream();
        var cursor = 0;
        foreach (var edit in edits.OrderBy(edit => edit.Start))
        {
            output.Write(data, cursor, edit.Start - cursor);
            output.Write(edit.Bytes);
            cursor = edit.End;
        }
        output.Write(data, cursor, data.Length - cursor);
        return output.ToArray();
    }
}

/// <summary>
/// The edit made by <see cref="OpenCodePluginSetup.Install"/>. Commit removes the backup; Rollback restores the exact
/// original bytes only while the config still holds our edit, otherwise it removes only our entry.
/// </summary>
internal sealed class OpenCodePluginSetupTransaction
{
    private readonly string configPath;
    private readonly string? backupPath;
    private readonly byte[] editedBytes;
    private readonly bool created;
    private readonly string folder;

    internal OpenCodePluginSetupTransaction(string configPath, string? backupPath, byte[] editedBytes, bool created, string folder)
    {
        this.configPath = configPath;
        this.backupPath = backupPath;
        this.editedBytes = editedBytes;
        this.created = created;
        this.folder = folder;
    }

    /// <summary>Deletes the credential-bearing backup after readiness has been verified by the caller.</summary>
    internal void Commit()
    {
        if (backupPath is not null) File.Delete(backupPath);
    }

    internal OpenCodePluginRollbackResult Rollback()
    {
        try
        {
            var current = OpenCodePluginSetup.ReadOrNull(configPath);
            if (current is null)
            {
                if (!created) return Unsafe(); // the user removed a file we only edited; never recreate it from the backup
            }
            else if (OpenCodePluginSetup.SameBytes(current, editedBytes))
            {
                if (created) OpenCodePluginSetup.GuardedWrite(configPath, editedBytes, () => File.Delete(configPath));
                else OpenCodePluginSetup.GuardedWrite(configPath, editedBytes, () => OpenCodePluginSetup.ReplaceFile(configPath, File.ReadAllBytes(backupPath!)));
            }
            else
            {
                var stripped = OpenCodePluginSetup.RemoveEntries(current, folder);
                if (stripped is null) return Unsafe();
                if (!OpenCodePluginSetup.SameBytes(stripped, current))
                    OpenCodePluginSetup.GuardedWrite(configPath, current, () => OpenCodePluginSetup.ReplaceFile(configPath, stripped));
            }
            return Verified() ? Finish() : Unsafe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unsafe();
        }
    }

    private bool Verified()
    {
        var now = OpenCodePluginSetup.ReadOrNull(configPath);
        return now is null
            ? created
            : OpenCodePluginSetup.RemoveEntries(now, folder) is { } rest && OpenCodePluginSetup.SameBytes(rest, now);
    }

    private OpenCodePluginRollbackResult Finish()
    {
        if (backupPath is null) return new(true, null);
        try
        {
            File.Delete(backupPath);
            return new(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(true, backupPath);
        }
    }

    // The backup a failed rollback hands back for manual recovery is renamed out of the sweep pattern, so it survives
    // the next sweep. If the rename itself fails, the .bak path is returned and the sweep rules still apply to it.
    private OpenCodePluginRollbackResult Unsafe()
    {
        if (backupPath is null) return new(false, null);
        var retained = Path.ChangeExtension(backupPath, ".retained");
        try
        {
            File.Move(backupPath, retained);
            return new(false, retained);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, backupPath);
        }
    }
}

internal sealed class ConfigChangedException() : IOException("OpenCode config changed since it was read.");
