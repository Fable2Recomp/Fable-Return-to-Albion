using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Refii.AndroidRuntime;

/// <summary>Persists prepared/compiled installer progress and safely commits a first or replacement game configuration.</summary>
/// <remarks>Compilation and extraction never replace the active game. Only a verified Play action
/// commits configuration; cleanup retains playable assets, the module, save data and provenance.</remarks>
public static class AndroidInstallState
{
    /// <summary>Restartable app-private installer state, separate from the active runtime configuration.</summary>
    /// <param name="Game">Complete prepared assets, if this launcher imported a disc.</param>
    /// <param name="Compiled">Verified compile result, if compilation completed.</param>
    public sealed record State(AndroidGameInstaller.Prepared? Game, AndroidPhoneCompiler.Result? Compiled);

    /// <summary>Atomically publishes installer progress without activating the game.</summary>
    /// <param name="user">Physical app-private user directory.</param>
    /// <param name="state">Prepared assets and optional complete module.</param>
    public static void Save(string user, State state)
    {
        user = AndroidRuntimeOptions.PhysicalPath(user, "installer user root");
        string path = Path.Combine(user, "phone-installer-state.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, AndroidJsonContext.Default.State));
        File.Move(temporary, path, true);
    }

    /// <summary>Loads usable prepared progress and accepts only exact completed modules inside the owned build root.</summary>
    /// <param name="user">Physical app-private user directory.</param>
    /// <returns>Validated progress, or an empty state for a fresh installation.</returns>
    public static State Load(string user)
    {
        user = AndroidRuntimeOptions.PhysicalPath(user, "installer user root");
        string path = Path.Combine(user, "phone-installer-state.json");
        if (!File.Exists(path)) return new(null, null);
        State state = JsonSerializer.Deserialize(File.ReadAllText(path), AndroidJsonContext.Default.State) ?? throw new InvalidDataException("Invalid installer state");
        if (state.Game != null)
        {
            RequireBelow(Path.Combine(user, "phone-installs"), state.Game.Directory);
            if (state.Game.GameRoot != Path.Combine(state.Game.Directory, "game") ||
                !File.Exists(Path.Combine(state.Game.Directory, "prepared.json")) ||
                !File.Exists(Path.Combine(state.Game.GameRoot, "default.xex")))
                throw new InvalidDataException("Prepared game files are incomplete");
        }
        if (state.Compiled != null)
        {
            RequireBelow(Path.Combine(user, "phone-builds"), state.Compiled.Directory);
            if (state.Compiled.Module != Path.Combine(state.Compiled.Directory, "librefii_generated.so") ||
                !AndroidPhoneLauncher.CanActivate(state.Compiled)) return new(state.Game, null);
        }
        return state;
    }

    /// <summary>Activates exact verified output and creates first-install settings while preserving an older configuration.</summary>
    /// <param name="user">Physical app-private user directory.</param>
    /// <param name="game">Complete extracted disc assets or the previously configured game root.</param>
    /// <param name="result">Exact completed private module.</param>
    /// <returns>The committed runtime options ready for the production Android game scene.</returns>
    public static AndroidRuntimeOptions Activate(string user, string game, AndroidPhoneCompiler.Result result)
    {
        user = AndroidRuntimeOptions.PhysicalPath(user, "installer user root");
        game = AndroidRuntimeOptions.PhysicalPath(game, "prepared game root");
        RequireBelow(Path.Combine(user, "phone-builds"), result.Directory);
        if (result.Module != Path.Combine(result.Directory, "librefii_generated.so") ||
            !AndroidPhoneLauncher.CanActivate(result) || !File.Exists(Path.Combine(game, "default.xex")) ||
            !Directory.Exists(Path.Combine(game, "data"))) throw new InvalidDataException("Game installation is not complete");
        string path = Path.Combine(user, AndroidRuntimeOptions.FileName);
        AndroidRuntimeOptions? previous = File.Exists(path) ? AndroidRuntimeOptions.Parse(File.ReadAllText(path), user) : null;
        string backup = Path.Combine(result.Directory, "previous-android-runtime.json");
        if (previous != null && !File.Exists(backup))
        {
            File.Copy(path, backup, false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(backup, File.GetUnixFileMode(path));
        }
        string temporary = path + ".phone-" + Guid.NewGuid().ToString("N");
        string json = Refii.Core.Engine_Json.Serialize(new {
            schema = 1, game_root = game, module_path = result.Module, save_import_root = previous?.SaveImportRoot,
            skip_movies = previous?.SkipMovies ?? true, movie_dwell_seconds = previous?.MovieDwellSeconds ?? 2 },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        AndroidRuntimeOptions options = AndroidRuntimeOptions.Parse(json, user);
        File.WriteAllText(temporary, json);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, previous == null
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite : File.GetUnixFileMode(path));
        File.Move(temporary, path, true);
        return options;
    }

    /// <summary>Removes owned compiler scratch after success/failure while retaining the verified module and bounded diagnostics.</summary>
    /// <param name="attempt">New compilation directory with an explicit ownership marker.</param>
    /// <param name="successful">Whether a verified module must be retained for activation.</param>
    public static void TrimBuild(string attempt, bool successful)
    {
        RequireOwner(attempt, "phone-build-", ".refii-build-owner");
        var removed = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string directory in new[] { "input", "generated" })
        {
            string path = Path.Combine(attempt, directory);
            if (!Directory.Exists(path)) continue;
            RejectLinks(path);
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                removed[Path.GetRelativePath(attempt, file).Replace('\\', '/')] = DescribeFile(file);
            Directory.Delete(path, true);
        }
        foreach (string file in Directory.EnumerateFiles(attempt))
        {
            string name = Path.GetFileName(file);
            if (name == "refii.pch" || Regex.IsMatch(name, @"\Aunit-[0-9]+\.o\z") ||
                (!successful && name == "librefii_generated.so"))
            {
                RejectLinks(file);
                removed[name] = DescribeFile(file);
                File.Delete(file);
            }
            else if (name.StartsWith("command-", StringComparison.Ordinal) && name.EndsWith(".log", StringComparison.Ordinal) &&
                new FileInfo(file).Length > 64 * 1024)
            {
                RejectLinks(file);
                using var stream = File.OpenRead(file);
                stream.Seek(-64 * 1024, SeekOrigin.End);
                byte[] tail = new byte[64 * 1024];
                stream.ReadExactly(tail);
                stream.Close();
                File.WriteAllBytes(file, tail);
            }
        }
        File.WriteAllText(Path.Combine(attempt, "cleanup.json"), Refii.Core.Engine_Json.Serialize(new {
            successful, module_retained = successful, originals_and_saves_untouched = true, removed }));
    }

    /// <summary>Removes only a newly extracted public compiler cache created by this installer.</summary>
    /// <param name="user">App-private user directory containing the tool cache.</param>
    /// <param name="bundle">Public extracted compiler resources used by the finished attempt.</param>
    public static void TrimBundle(string user, string bundle)
    {
        RequireBelow(user, bundle);
        string marker = Path.Combine(bundle, ".refii-toolchain-owner");
        if (!File.Exists(marker)) return;
        if (File.ReadAllText(marker) != Path.GetFileName(bundle) ||
            !Regex.IsMatch(Path.GetFileName(bundle), @"\Aphone-toolchain-[0-9a-f]{64}(?:-[0-9a-f]{32})?\z"))
            throw new InvalidDataException("Compiler cache is not owned by this installer");
        RejectLinks(bundle);
        Directory.Delete(bundle, true);
    }

    /// <summary>Prunes incomplete owned preparation left by a killed process and bounds old failure diagnostics.</summary>
    /// <param name="user">App-private user root; active saves/configuration are never visited.</param>
    public static void RecoverInterrupted(string user)
    {
        string builds = Path.Combine(user, "phone-builds");
        if (Directory.Exists(builds))
            foreach (string attempt in Directory.EnumerateDirectories(builds, "phone-build-*"))
                if (File.Exists(Path.Combine(attempt, ".refii-build-owner")) &&
                    !File.Exists(Path.Combine(attempt, "cleanup.json")))
                    TrimBuild(attempt, File.Exists(Path.Combine(attempt, "completed.json")));
        foreach (string bundle in Directory.EnumerateDirectories(user, "phone-toolchain-*"))
            if (File.Exists(Path.Combine(bundle, ".refii-toolchain-owner"))) TrimBundle(user, bundle);
        string root = Path.Combine(user, "phone-installs");
        if (!Directory.Exists(root)) return;
        foreach (string attempt in Directory.EnumerateDirectories(root, "install-*"))
        {
            RequireOwner(attempt, "install-", ".refii-install-owner");
            if (File.Exists(Path.Combine(attempt, "prepared.json"))) continue;
            AndroidGameInstaller.DeleteOwnedChild(attempt, "input");
            AndroidGameInstaller.DeleteOwnedChild(attempt, "game");
        }
        // Failed attempts contain only small receipts after owned temporary cleanup.
        // Retain three for useful diagnostics, without deleting complete or unowned installs.
        foreach (string attempt in Directory.EnumerateDirectories(root, "install-*")
            .Where(path => !File.Exists(Path.Combine(path, "prepared.json")))
            .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path)).Skip(3))
        {
            RequireOwner(attempt, "install-", ".refii-install-owner");
            RejectLinks(attempt);
            Directory.Delete(attempt, true);
        }
    }

    /// <summary>
    /// Removes the complete installs and compiled builds the game no longer uses: those named by
    /// neither the active configuration (android-runtime.json) nor the installer state.
    /// </summary>
    /// <remarks>
    /// Nothing else ever removed them, so each "Extract and compile" left the previous extracted
    /// game behind (about 6.5 GB) and each compile its module (about 80 MB): a Galaxy S10e had
    /// collected three games (20 GB) and eight modules (780 MB). Only owned folders go, by the
    /// name and marker <see cref="RecoverInterrupted"/> checks, and only complete ones (incomplete
    /// attempts are its). Installs are pruned only while the active game is one of them, and
    /// builds only while the active module is, so a PC-supplied game or module never leaves the
    /// phone's own unprotected; an unreadable configuration or state removes nothing.
    /// </remarks>
    /// <param name="user">App-private user directory.</param>
    /// <returns>The removed folders' names.</returns>
    public static IReadOnlyList<string> PruneUnused(string user)
    {
        user = AndroidRuntimeOptions.PhysicalPath(user, "installer user root");
        string config = Path.Combine(user, AndroidRuntimeOptions.FileName);
        if (!File.Exists(config)) return Array.Empty<string>();
        string installs = Path.Combine(user, "phone-installs"), builds = Path.Combine(user, "phone-builds");
        var keep = new HashSet<string>(StringComparer.Ordinal);
        string? activeInstall, activeBuild;
        try
        {
            AndroidRuntimeOptions active = AndroidRuntimeOptions.Parse(File.ReadAllText(config), user);
            activeInstall = ChildOf(installs, active.GameRoot);
            activeBuild = ChildOf(builds, active.ModulePath);
            string state = Path.Combine(user, "phone-installer-state.json");
            if (File.Exists(state))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(state));
                foreach ((string section, string root) in new[] { ("Game", installs), ("Compiled", builds) })
                    if (document.RootElement.TryGetProperty(section, out JsonElement entry) && entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("Directory", out JsonElement directory) && directory.ValueKind == JsonValueKind.String &&
                        ChildOf(root, directory.GetString()!) is { } name)
                        keep.Add(name);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Array.Empty<string>();
        }
        var removed = new List<string>();
        void Prune(string root, string? active, string prefix, string marker, string complete)
        {
            if (active is null || !Directory.Exists(root)) return;
            keep.Add(active);
            foreach (string attempt in Directory.EnumerateDirectories(root, prefix + "*"))
            {
                string name = Path.GetFileName(attempt);
                if (keep.Contains(name) || !File.Exists(Path.Combine(attempt, complete)) || !File.Exists(Path.Combine(attempt, marker))) continue;
                try { RequireOwner(attempt, prefix, marker); }
                catch (InvalidDataException) { continue; }
                Directory.Delete(attempt, true);
                removed.Add(name);
            }
        }
        Prune(installs, activeInstall, "install-", ".refii-install-owner", "prepared.json");
        Prune(builds, activeBuild, "phone-build-", ".refii-build-owner", "completed.json");
        return removed;
    }

    /// <summary>The name of <paramref name="root"/>'s child folder that holds <paramref name="path"/>, or null outside it.</summary>
    private static string? ChildOf(string root, string path)
    {
        string parent = Path.GetFullPath(MatchPrivateAlias(root, root)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(MatchPrivateAlias(root, path));
        if (!full.StartsWith(parent, StringComparison.Ordinal)) return null;
        string name = full[parent.Length..].Split(Path.DirectorySeparatorChar)[0];
        return name.Length > 0 ? name : null;
    }

    /// <summary>Records an owned scratch file's exact bytes before cleanup removes the redundant copy.</summary>
    private static object DescribeFile(string file)
    {
        using var stream = File.OpenRead(file);
        return new { bytes = stream.Length, sha256 = Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream)).ToLowerInvariant() };
    }

    /// <summary>Validates a unique attempt name and ownership marker before any destructive cleanup.</summary>
    private static void RequireOwner(string attempt, string prefix, string marker)
    {
        if (!Regex.IsMatch(Path.GetFileName(attempt), "\\A" + Regex.Escape(prefix) + "[0-9a-f]{32}\\z") ||
            File.ReadAllText(Path.Combine(attempt, marker)) != Path.GetFileName(attempt))
            throw new InvalidDataException("Directory is not owned by this installer");
        RejectLinks(attempt);
    }

    /// <summary>Requires an ordinary descendant of the explicit private root rather than an original input location.</summary>
    private static void RequireBelow(string root, string path)
    {
        // Android exposes the same owner through /data/data and /data/user/0.
        // Normalize only this app's files prefix; other users/apps and links
        // inside its private tree retain the existing rejection checks.
        path = MatchPrivateAlias(root, path);
        root = MatchPrivateAlias(root, root);
        root = AndroidRuntimeOptions.PhysicalPath(root, "private parent");
        path = AndroidRuntimeOptions.PhysicalPath(path, "private child");
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Installer state escapes private storage");
        string current = path;
        while (current != root)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installer state does not follow symbolic links");
            current = Path.GetDirectoryName(current)!;
        }
    }

    /// <summary>Matches Android's two owner-zero private path spellings for the explicitly trusted app root.</summary>
    /// <param name="root">The parent derived from the current application's user directory.</param>
    /// <param name="path">A persisted path; different apps, users and ordinary desktop paths remain unchanged.</param>
    /// <returns>The same path with its matching app-private prefix spelled as /data/user/0.</returns>
    internal static string MatchPrivateAlias(string root, string path)
    {
        var owner = Regex.Match(root, @"\A/data/(?:data|user/0)/(?<app>[A-Za-z0-9_.]+)/files(?:/|\z)");
        if (!owner.Success) return path;
        string app = owner.Groups["app"].Value;
        foreach (string prefix in new[] { "/data/data/" + app + "/files", "/data/user/0/" + app + "/files" })
            if (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal))
                return "/data/user/0/" + app + "/files" + path[prefix.Length..];
        return path;
    }

    /// <summary>Rejects linked files and directories before an owned cleanup traverses them.</summary>
    private static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Installer cleanup does not follow symbolic links");
        if (Directory.Exists(path))
            foreach (string entry in Directory.EnumerateFileSystemEntries(path)) RejectLinks(entry);
    }
}
