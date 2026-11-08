using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Refii.AndroidRuntime;

/// <summary>
/// Export: one zip of the run's logs, settings and device, the player's saves, or both, in the
/// phone's shared Download folder, where a player can find it, attach it to a report or keep it
/// as a backup without adb. An export with saves can be imported again (<see cref="AndroidSaveTransfer"/>).
/// </summary>
/// <remarks>
/// Written by .NET, not Godot's file layer, as photo mode writes its PNGs: on Android that
/// layer cannot write shared storage, and a file an app creates in Download directly is its
/// own and listed by the Files app. <c>godot.log</c> is open for writing while it is copied,
/// so every log is opened with <see cref="FileShare.ReadWrite"/>. Godot copies the previous
/// run's <c>godot.log</c> to <c>godot&lt;timestamp&gt;.log</c> when the next run starts, so a run
/// that crashed is the newest rotated log.
/// </remarks>
public static class AndroidLogBundle
{
    /// <summary>What an export holds.</summary>
    public enum Contents { Logs, Saves, LogsAndSaves }

    /// <summary>
    /// The saves' root in an export: the app's one save folder, so an export's save entries are
    /// <c>files/runtime/assets/saves/&lt;profile&gt;/...</c>, the active profile with every title in it.
    /// </summary>
    public const string SaveFolder = "runtime/assets/" + Refii.Save.Engine_SaveLayout.RootFolderName;

    /// <summary>The save folder an export named before the save profiles: one content root, still imported.</summary>
    public const string LegacySaveFolder = "runtime/assets/save";

    /// <summary>The app's one save folder on the phone, <c>runtime/assets/saves</c>.</summary>
    /// <param name="userDirectory">The app's data directory.</param>
    public static string SavesRoot(string userDirectory) =>
        Refii.Save.Engine_SaveLayout.Root(Path.Combine(userDirectory, "runtime", "assets"));

    /// <summary>The active save profile on the phone, the folder the native session points the game at.</summary>
    /// <param name="userDirectory">The app's data directory.</param>
    public static string SaveDirectory(string userDirectory) =>
        Refii.Save.Engine_SaveProfiles.ActiveDirectory(SavesRoot(userDirectory));

    /// <summary>The profile's own folder that never leaves the phone: the RexNet identity key and friend lists.</summary>
    public const string PrivateSaveFolder = Refii.Save.Engine_SaveLayout.IdentityFolderName;

    /// <summary>The profile's id file, which names this phone's profile and never leaves it either.</summary>
    public const string PrivateSaveFile = Refii.Save.Engine_SaveLayout.ProfileIdFileName;

    /// <summary>The manifest an export with saves carries: what it holds. An import checks the save entries themselves.</summary>
    public const string ManifestName = "export.json";

    /// <summary>One file in the zip.</summary>
    /// <param name="Source">The file's path on the phone.</param>
    /// <param name="ZipPath">Its name in the zip, relative to the app's data directory as <c>files/...</c>.</param>
    public sealed record Entry(string Source, string ZipPath);

    /// <summary>How many rotated <c>godot&lt;timestamp&gt;.log</c> files go in, newest first.</summary>
    /// <remarks>Four, not two: a launcher-only start rotates the log too, so the last two can both be
    /// launcher runs and miss the game run that crashed. Logs compress about 50:1.</remarks>
    public const int RotatedLogsKept = 4;

    /// <summary>A longer file keeps only its end: what led up to a crash.</summary>
    public const long MaxBytesPerFile = 64L * 1024 * 1024;

    /// <summary>The zip's name for a logs export at <paramref name="now"/>.</summary>
    public static string ArchiveName(DateTime now) => ArchiveName(Contents.Logs, now);

    /// <summary>The zip's name for an export of <paramref name="contents"/> at <paramref name="now"/>.</summary>
    /// <param name="contents">What it holds.</param>
    /// <param name="now">The local time it is named by.</param>
    /// <param name="label">Replaces the contents' name, as the backup an import makes first is named.</param>
    public static string ArchiveName(Contents contents, DateTime now, string? label = null) =>
        "Fable2-" + (label ?? contents switch
        {
            Contents.Saves => "saves",
            Contents.LogsAndSaves => "logs-and-saves",
            _ => "logs",
        }) + "-" + now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>Whether <paramref name="contents"/> holds the logs.</summary>
    public static bool HasLogs(Contents contents) => contents != Contents.Saves;

    /// <summary>Whether <paramref name="contents"/> holds the saves.</summary>
    public static bool HasSaves(Contents contents) => contents != Contents.Logs;

    /// <summary>Whether <paramref name="name"/> is one of Godot's rotated logs, <c>godot&lt;timestamp&gt;.log</c>.</summary>
    public static bool IsRotatedLog(string name) =>
        name.Length > "godot.log".Length && name.StartsWith("godot", StringComparison.Ordinal)
        && name.EndsWith(".log", StringComparison.Ordinal);

    /// <summary>The newest <paramref name="count"/> rotated logs among <paramref name="names"/>.</summary>
    /// <remarks>Godot names them by an ISO date and time, so the newest sorts last.</remarks>
    public static IReadOnlyList<string> RecentRotatedLogs(IEnumerable<string> names, int count = RotatedLogsKept) =>
        names.Select(Path.GetFileName).OfType<string>().Where(IsRotatedLog).Distinct(StringComparer.Ordinal)
            .OrderByDescending(name => name, StringComparer.Ordinal).Take(Math.Max(0, count)).ToList();

    /// <summary>The files that go in the zip, those of them that exist.</summary>
    /// <param name="userDirectory">The app's data directory (<c>OS.GetUserDataDir</c>).</param>
    /// <param name="exists">Whether a path is a file to include.</param>
    /// <param name="logNames">The names in <c>logs/</c>, to pick the rotated logs from.</param>
    public static List<Entry> SelectEntries(string userDirectory, Func<string, bool> exists, IEnumerable<string> logNames)
    {
        var relative = new List<string> { "logs/godot.log" };
        relative.AddRange(RecentRotatedLogs(logNames).Select(name => "logs/" + name));
        relative.Add("logs/refii_flight.jsonl");
        relative.Add("logs/refii_flight.jsonl.1");
        relative.Add("runtime/refii.toml");
        relative.Add("logs/" + AndroidLastExit.FileName);
        relative.Add("logs/" + AndroidLastExit.PreviousFileName);
        relative.Add("logs/" + AndroidLastExit.TraceFileName);
        relative.Add("logs/" + AndroidLastExit.PreviousTraceFileName);
        // The runtime's own log, where a guest fatal names its address, rotates by size across runs.
        relative.Add("runtime/logs/refii.log");
        relative.Add("runtime/logs/refii.1.log");
        relative.Add("runtime/logs/refii_crash.log");
        // Which compiled module the game runs and what the installer last recorded.
        relative.Add("android-runtime.json");
        relative.Add("phone-installer-state.json");
        relative.Add("android-status.json");
        return relative
            .Select(path => (Entry: new Entry(Path.Combine(userDirectory, path.Replace('/', Path.DirectorySeparatorChar)), "files/" + path), RawPath: path))
            .Where(item => exists(item.Entry.Source) || exists(Path.Combine(userDirectory, item.RawPath)))
            .Select(item => item.Entry)
            .ToList();
    }

    /// <summary>Every file in the active profile but its <see cref="PrivateSaveFolder"/> and <see cref="PrivateSaveFile"/>,
    /// as <c>files/runtime/assets/saves/&lt;profile&gt;/...</c>.</summary>
    /// <param name="userDirectory">The app's data directory.</param>
    /// <remarks>Symbolic links are not followed: the save folder is the app's own and holds none.</remarks>
    public static List<Entry> SelectSaveEntries(string userDirectory)
    {
        string root = SaveDirectory(userDirectory);
        string profile = Path.GetFileName(root);
        var entries = new List<Entry>();
        if (!Directory.Exists(root)) return entries;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(folder).OrderBy(name => name, StringComparer.Ordinal))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(path))
                {
                    if (folder == root && relative == PrivateSaveFolder) continue;
                    pending.Push(path);
                }
                else if (folder != root || relative != PrivateSaveFile)
                    entries.Add(new Entry(path, "files/" + SaveFolder + "/" + profile + "/" + relative));
            }
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.ZipPath, b.ZipPath));
        return entries;
    }

    /// <summary>Writes the zip into <paramref name="downloads"/> and returns its path.</summary>
    /// <param name="downloads">The shared Download folder.</param>
    /// <param name="userDirectory">The app's data directory.</param>
    /// <param name="device">The text of <c>device.txt</c>.</param>
    /// <param name="now">The local time the zip is named by.</param>
    /// <param name="maxBytesPerFile">The most of any one log kept, from its end; save files are always whole.</param>
    /// <param name="contents">The logs, the saves or both.</param>
    /// <param name="label">Names the zip in place of its contents (<see cref="ArchiveName(Contents, DateTime, string?)"/>).</param>
    /// <param name="apkBundle">This APK's compiler bundle identity, for <c>install.txt</c>; empty when unknown.</param>
    /// <exception cref="IOException">The zip could not be written; nothing is left behind.</exception>
    public static string Write(string downloads, string userDirectory, string device, DateTime now,
        long maxBytesPerFile = MaxBytesPerFile, Contents contents = Contents.Logs, string? label = null, string apkBundle = "")
    {
        Directory.CreateDirectory(downloads);
        var entries = new List<Entry>();
        if (HasLogs(contents))
        {
            string logs = Path.Combine(userDirectory, "logs");
            IEnumerable<string> names = Directory.Exists(logs)
                ? Directory.EnumerateFiles(logs).Select(Path.GetFileName).OfType<string>() : Array.Empty<string>();
            entries.AddRange(SelectEntries(userDirectory, File.Exists, names));
        }
        List<Entry> saves = HasSaves(contents) ? SelectSaveEntries(userDirectory) : new List<Entry>();
        string path = UniquePath(downloads, ArchiveName(contents, now, label));
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, System.IO.FileAccess.Write);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            AddText(zip, "device.txt", device);
            if (HasLogs(contents)) AddText(zip, "install.txt", DescribeInstall(userDirectory, ApkWrittenUtc(), apkBundle));
            if (HasSaves(contents))
            {
                AddText(zip, ManifestName, Manifest(contents, saves.Count, now));
            }
            var problems = new StringBuilder();
            foreach (Entry entry in entries)
            {
                try { AddFile(zip, entry, maxBytesPerFile); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    problems.Append(entry.ZipPath).Append(": ").AppendLine(error.Message);
                }
            }
            if (problems.Length > 0) AddText(zip, "unreadable.txt", problems.ToString());
            // A save is all or nothing: an unreadable file fails the export rather than leave a save that
            // would import broken (and the backup an import writes first must be whole).
            foreach (Entry entry in saves) AddFile(zip, entry, long.MaxValue);
        }
        catch
        {
            try { File.Delete(path); } catch (Exception) { }
            throw;
        }
        return path;
    }

    /// <summary>The manifest of an export with saves: what it holds and how many save files.</summary>
    public static string Manifest(Contents contents, int saveFiles, DateTime now) =>
        "{\n  \"schema\": 1,\n  \"kind\": \"refii-android-export\",\n" +
        $"  \"contents\": \"{(contents == Contents.Saves ? "saves" : "logs-and-saves")}\",\n" +
        $"  \"save_root\": \"files/{SaveFolder}/\",\n  \"save_files\": {saveFiles},\n" +
        $"  \"created\": \"{now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\"\n}}\n";

    /// <summary>What an export is called in the lines the buttons show.</summary>
    public static string Noun(Contents contents) => contents switch
    {
        Contents.Saves => "Saves",
        Contents.LogsAndSaves => "Logs and saves",
        _ => "Logs",
    };

    /// <summary>Saves the logs zip: <see cref="ExportAsync"/> with the logs alone.</summary>
    public static Task<string> SaveAsync() => ExportAsync(Contents.Logs);

    /// <summary>
    /// Collects <c>device.txt</c> on the main thread, writes the zip on a worker and returns the
    /// line to show: "Logs saved to Download/..." or why not. Never throws.
    /// </summary>
    /// <param name="contents">The logs, the saves or both.</param>
    public static async Task<string> ExportAsync(Contents contents)
    {
        string noun = Noun(contents);
        try
        {
            string downloads = OS.GetSystemDir(OS.SystemDir.Downloads);
            if (string.IsNullOrWhiteSpace(downloads)) return noun + " not saved: this phone has no Download folder.";
            string user = OS.GetUserDataDir();
            DateTime now = LocalNow();
            GD.Print("[export] saving " + ArchiveName(contents, now));
            string device = DescribeDevice();
            string bundle = HasLogs(contents) ? ApkBundle() : "";
            string path = await Task.Run(() => Write(downloads, user, device, now, contents: contents, apkBundle: bundle));
            GD.Print("[export] saved " + path);
            return noun + " saved to Download/" + Path.GetFileName(path);
        }
        catch (Exception error)
        {
            GD.PushWarning("[export] not saved: " + error.Message);
            return noun + " not saved: " + error.Message;
        }
    }

    /// <summary>The phone's local time from Godot, which reads the system zone where .NET on Android may not.</summary>
    internal static DateTime LocalNow()
    {
        try
        {
            Godot.Collections.Dictionary now = Time.GetDatetimeDictFromSystem();
            return new DateTime(now["year"].AsInt32(), now["month"].AsInt32(), now["day"].AsInt32(),
                now["hour"].AsInt32(), now["minute"].AsInt32(), now["second"].AsInt32());
        }
        catch (Exception) { return DateTime.Now; }
    }

    /// <summary>The model, Android, CPU, memory, GPU, build and screens, one fact a line; a fact that fails says so.</summary>
    public static string DescribeDevice()
    {
        var text = new StringBuilder();
        void Line(string label, Func<string> value)
        {
            string result;
            try { result = value(); }
            catch (Exception error) { result = "(unavailable: " + error.Message + ")"; }
            text.Append(label).Append(": ").AppendLine(result);
        }
        static string Megabytes(Variant bytes) => bytes.AsInt64() < 0 ? "unknown" : $"{bytes.AsInt64() / (1024 * 1024)} MB";

        Line("Saved", () => Time.GetDatetimeStringFromSystem(false, true) + " local, " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Line("Model", OS.GetModelName);
        Line("OS", () => $"{OS.GetName()} {OS.GetVersion()} ({OS.GetVersionAlias()}) {OS.GetDistributionName()}");
        Line("Processor", () => $"{FirstNonEmpty(OS.GetProcessorName(), ProcField("/sys/devices/soc0/machine", null), ProcField("/proc/cpuinfo", "Hardware"))}, {OS.GetProcessorCount()} cores");
        Line("Memory", () =>
        {
            Godot.Collections.Dictionary memory = OS.GetMemoryInfo();
            if (memory["physical"].AsInt64() > 0)
                return $"physical {Megabytes(memory["physical"])}, available {Megabytes(memory["available"])}, free {Megabytes(memory["free"])}";
            // Godot reports nothing on Android: the kernel's own figures.
            return $"total {ProcField("/proc/meminfo", "MemTotal")}, available {ProcField("/proc/meminfo", "MemAvailable")}";
        });
        Line("GPU", () => $"{RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterVendor()}, {RenderingServer.GetVideoAdapterType()})");
        Line("Graphics API", () => $"{RenderingServer.GetCurrentRenderingDriverName()} {RenderingServer.GetVideoAdapterApiVersion()}, {RenderingServer.GetCurrentRenderingMethod()}");
        Line("Engine", () => Engine.GetVersionInfo()["string"].AsString());
        Line("App", () =>
        {
            string version = ProjectSettings.GetSetting("application/config/version", "").AsString();
            string assembly = typeof(AndroidLogBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            return $"{ProjectSettings.GetSetting("application/config/name", "").AsString()} {version} (assembly {assembly}, {(OS.HasFeature("debug") ? "debug" : "release")})".Trim();
        });
        Line("Data", OS.GetUserDataDir);
        Line("Window", () => $"{DisplayServer.WindowGetSize()} ({DisplayServer.WindowGetMode()})");
        int screens = 0;
        try { screens = DisplayServer.GetScreenCount(); }
        catch (Exception error) { text.Append("Screens: (unavailable: ").Append(error.Message).AppendLine(")"); }
        for (int screen = 0; screen < screens; screen++)
        {
            int index = screen;
            Line("Screen " + index, () => $"{DisplayServer.ScreenGetSize(index)}, {DisplayServer.ScreenGetDpi(index)} dpi, " +
                $"scale {DisplayServer.ScreenGetScale(index).ToString(CultureInfo.InvariantCulture)}, " +
                $"{DisplayServer.ScreenGetRefreshRate(index).ToString("0.##", CultureInfo.InvariantCulture)} Hz, " +
                $"safe area {DisplayServer.GetDisplaySafeArea()}");
        }
        return text.ToString();
    }

    /// <summary>
    /// <c>install.txt</c>: when the APK was installed, the module the game runs, whether its game
    /// code was compiled from this APK's bundle, and every compile job and compiled module, newest
    /// first, with whether each job finished.
    /// </summary>
    /// <remarks>
    /// A compile the system stops leaves the previous module in place, and the game plays it until a
    /// compile finishes: this says which module a crash ran on and whether it matches the APK, the
    /// launcher's own test (<see cref="AndroidPhoneLauncher.IsStale"/>), which the logs alone cannot.
    /// </remarks>
    /// <param name="userDirectory">The app's data directory.</param>
    /// <param name="apkWrittenUtc">When the running APK was installed; null when unknown.</param>
    /// <param name="apkBundle">This APK's compiler bundle identity; empty when unknown.</param>
    public static string DescribeInstall(string userDirectory, DateTime? apkWrittenUtc, string apkBundle = "")
    {
        var text = new StringBuilder();
        static string Utc(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        text.Append("APK installed: ").AppendLine(apkWrittenUtc is { } apk ? Utc(apk) : "unknown");
        string module = "";
        try
        {
            using var runtime = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(userDirectory, "android-runtime.json")));
            if (runtime.RootElement.TryGetProperty("module_path", out System.Text.Json.JsonElement path)) module = path.GetString() ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        if (module.Length == 0) text.AppendLine("Active module: none recorded");
        else if (!File.Exists(module)) text.Append("Active module: ").Append(module).AppendLine(" (MISSING)");
        else
        {
            text.Append("Active module: ").Append(module).Append(" (written ").Append(Utc(File.GetLastWriteTimeUtc(module))).AppendLine(")");
        }
        text.AppendLine(DescribeCompiledBundle(userDirectory, module, apkBundle));

        string jobs = Path.Combine(userDirectory, "phone-jobs");
        text.AppendLine().AppendLine("Compile jobs, newest first:");
        foreach (string job in NewestFirst(jobs))
        {
            static string Read(string file) => File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            bool finished = File.Exists(Path.Combine(job, "finished"));
            text.Append("  ").Append(Path.GetFileName(job)).Append("  ").Append(Utc(Directory.GetLastWriteTimeUtc(job)))
                .Append("  ").Append(finished ? "finished" : "NOT FINISHED")
                .Append("  last step: ").AppendLine(Read(Path.Combine(job, "progress")));
        }

        string builds = Path.Combine(userDirectory, "phone-builds");
        text.AppendLine().AppendLine("Compiled modules, newest first:");
        foreach (string build in NewestFirst(builds))
        {
            string library = Path.Combine(build, "librefii_generated.so");
            text.Append("  ").Append(Path.GetFileName(build)).Append("  ").Append(Utc(Directory.GetLastWriteTimeUtc(build)))
                .Append(File.Exists(library) ? $"  module {new FileInfo(library).Length / (1024 * 1024)} MB, written {Utc(File.GetLastWriteTimeUtc(library))}" : "  no module")
                .AppendLine(File.Exists(Path.Combine(build, "completed.json")) ? ", completed" : ", not completed");
        }
        return text.ToString().Replace("\r\n", "\n");
    }

    /// <summary>Whether the last compiled module was built from this APK's bundle, read from the installer state.</summary>
    private static string DescribeCompiledBundle(string userDirectory, string activeModule, string apkBundle)
    {
        static string Short(string identity) => identity.Length > 12 ? identity[..12] : identity;
        string compiledModule = "", compiledBundle = "";
        try
        {
            using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(userDirectory, "phone-installer-state.json")));
            if (state.RootElement.TryGetProperty("Compiled", out System.Text.Json.JsonElement compiled)
                && compiled.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (compiled.TryGetProperty("Module", out System.Text.Json.JsonElement path)) compiledModule = path.GetString() ?? "";
                if (compiled.TryGetProperty("Bundle", out System.Text.Json.JsonElement bundle) && bundle.ValueKind == System.Text.Json.JsonValueKind.String)
                    compiledBundle = bundle.GetString() ?? "";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        if (compiledModule.Length == 0) return "Compiled game code: none recorded";
        string line = apkBundle.Length == 0 ? "Compiled game code: bundle " + (compiledBundle.Length > 0 ? Short(compiledBundle) : "unknown") + ", this APK's unknown"
            : compiledBundle == apkBundle ? "Compiled game code: from this APK's bundle (" + Short(apkBundle) + ")"
            : "Compiled game code: NOT from this APK's bundle (" + (compiledBundle.Length > 0 ? Short(compiledBundle) : "unknown")
                + ", this APK's " + Short(apkBundle) + "): the launcher recompiles it, and the game runs the old code until that finishes";
        if (activeModule.Length > 0 && compiledModule != activeModule) line += "\n  (the active module is not the last compiled one: " + compiledModule + ")";
        return line;
    }

    /// <summary>The folders in <paramref name="parent"/>, most recently written first; none when it is missing.</summary>
    private static IEnumerable<string> NewestFirst(string parent) =>
        Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).OrderByDescending(Directory.GetLastWriteTimeUtc).ToList()
            : Enumerable.Empty<string>();

    /// <summary>This APK's compiler bundle identity, or empty when it cannot be read: the export goes on without it.</summary>
    private static string ApkBundle()
    {
        try { return AndroidPhoneLauncher.ApkBundleIdentity(); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            GD.PushWarning("[export] bundle identity: " + error.Message);
            return "";
        }
    }

    /// <summary>When the running APK was installed: its file's time, found through the process's own mappings.</summary>
    private static DateTime? ApkWrittenUtc()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/maps"))
            {
                int at = line.IndexOf('/');
                if (at >= 0 && line.EndsWith("/base.apk", StringComparison.Ordinal)) return File.GetLastWriteTimeUtc(line[at..]);
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The first value that is not blank, or "unknown".</summary>
    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "unknown";

    /// <summary>A "Name: value" line's value from a kernel text file (the whole first line when <paramref name="name"/> is null); empty when unreadable.</summary>
    private static string ProcField(string path, string? name)
    {
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (name is null) return line.Trim();
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim() == name) return line[(colon + 1)..].Trim();
            }
        }
        catch (Exception) { }
        return "";
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/>, numbered when a save in the same second exists.</summary>
    private static string UniquePath(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + "-" + n + Path.GetExtension(name));
        return path;
    }

    /// <summary>Adds <paramref name="text"/> as a UTF-8 entry.</summary>
    private static void AddText(ZipArchive zip, string name, string text)
    {
        using Stream target = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        target.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Adds one file, shared with its writer; a longer file than the limit keeps its end.</summary>
    private static void AddFile(ZipArchive zip, Entry entry, long maxBytes)
    {
        using var source = new FileStream(entry.Source, FileMode.Open, System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        ZipArchiveEntry item = zip.CreateEntry(entry.ZipPath, CompressionLevel.Optimal);
        try
        {
            DateTime written = File.GetLastWriteTime(entry.Source);
            if (written.Year is >= 1980 and <= 2107) item.LastWriteTime = written;
        }
        catch (Exception) { }
        using Stream target = item.Open();
        long length = source.Length;
        if (length > maxBytes)
        {
            target.Write(Encoding.UTF8.GetBytes($"[Save logs: the first {length - maxBytes} of {length} bytes were left out]\n"));
            source.Seek(length - maxBytes, SeekOrigin.Begin);
            byte[] buffer = new byte[81920];
            long left = maxBytes;
            while (left > 0)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0) break;
                target.Write(buffer, 0, read);
                left -= read;
            }
        }
        else
        {
            source.CopyTo(target);
        }
    }
}
