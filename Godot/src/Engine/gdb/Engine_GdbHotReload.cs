using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Refii.Gdb;

/// <summary>
/// The guest's databases after a remount, without a relaunch.
///
/// The guest parses a <c>.gdb</c> once into a blob it keeps and maps every record id to a
/// pointer into it; a title update (<c>X.gdb.tu</c>) is a second file loaded by the same loader
/// whose records replace the first's in that map. So the composited patches the mount serves
/// (<see cref="GdbPatchCompositor"/>) reach the running game by having the guest's own loader
/// read them again: the tick command <c>gdb_reload PATH</c> (src/vfs/src/hooks/gdb_hot_reload.cpp)
/// loads the remounted file and writes its values into every older copy of each record, so an
/// entity holding an old record pointer sees them too; <c>gdb_revert PATH HASH...</c> maps rows
/// a patch no longer states back to the base file's records.
///
/// This class knows WHAT the guest has (<see cref="Observe"/>: the composites the guest loaded
/// at boot, then every applied reload) and plans the commands a new mount needs
/// (<see cref="Plan"/>, pure). Posted from the remount's main-thread finish
/// (<see cref="Apply"/>), the one path the Studio's reload, "Reload mods" and the co-op mod sync
/// share (<c>ModRemount</c>).
///
/// What a reload cannot do, stated in docs/modding/gdb-and-text.md: a row the base never had and a patch
/// states no more stays until a relaunch (the guest's map has no tombstones), and a property
/// ADDED to a record is seen by holders of the record's older copy only when they look it up
/// again.
/// </summary>
public static class GdbHotReload
{
    public const string ReloadCommand = "gdb_reload";

    public const string RevertCommand = "gdb_revert";

    /// <summary>
    /// Hashes per <c>gdb_revert</c> line: the tick splits a line into at most 64 words
    /// (mod_commands.cpp <c>Split</c>), two of them the verb and the path.
    /// </summary>
    public const int HashesPerLine = 60;

    private const string TuSuffix = ".gdb.tu";

    private const string GdbSuffix = ".gdb";

    private static readonly object Lock = new();

    /// <summary>What the guest has loaded, by composite sub path: the bytes it read.</summary>
    private static Dictionary<string, byte[]> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The composites of a mount built while the game runs, until the finish applies them.</summary>
    private static Dictionary<string, byte[]>? _pending;

    /// <summary>
    /// The composites a mount produced. At boot (<paramref name="live"/> false) the guest loads
    /// them itself, so they become what it has; during a run they wait for <see cref="Apply"/>.
    /// Any thread.
    /// </summary>
    public static void Observe(IEnumerable<CompositeGdbResult> composites, bool live)
    {
        var now = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (CompositeGdbResult composite in composites)
        {
            if (composite.Binary is { Length: > 0 } bytes && !string.IsNullOrEmpty(composite.SubPath))
            {
                now[composite.SubPath] = bytes;
            }
        }

        lock (Lock)
        {
            if (live)
            {
                _pending = now;
            }
            else
            {
                _loaded = now;
                _pending = null;
            }
        }
    }

    /// <summary>What the guest has, for a reader of the state (tests, the debug channel).</summary>
    public static IReadOnlyDictionary<string, byte[]> Loaded
    {
        get
        {
            lock (Lock)
            {
                return new Dictionary<string, byte[]>(_loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Plans and posts the commands for the mount that just finished, against what the guest
    /// has, and takes the new composites as what it has from now on. Main thread, after the
    /// mount is served. Returns the lines posted (none when the guest is not up: the next boot
    /// loads what is mounted).
    /// </summary>
    public static List<string> Apply(IReadOnlyList<string> changed)
    {
        Dictionary<string, byte[]> loaded;
        Dictionary<string, byte[]>? pending;
        lock (Lock)
        {
            loaded = _loaded;
            pending = _pending;
            _pending = null;
        }

        var now = pending ?? loaded;
        List<string> lines = Plan(loaded, now, changed);
        if (!Refii.Host.HostBridge.IsGuestUp)
        {
            lines.Clear();
        }

        foreach (string line in lines)
        {
            Refii.Host.HostBridge.PostModCommand(line);
        }

        lock (Lock)
        {
            _loaded = now;
        }

        return lines;
    }

    /// <summary>
    /// The tick commands that take the guest from <paramref name="loaded"/> to
    /// <paramref name="now"/> (composite bytes by sub path), with <paramref name="changed"/> the
    /// mount's changed virtual paths (a base <c>.gdb</c> a mod replaced whole is reloaded first,
    /// then its patch on top). Pure.
    /// </summary>
    public static List<string> Plan(
        IReadOnlyDictionary<string, byte[]> loaded,
        IReadOnlyDictionary<string, byte[]> now,
        IEnumerable<string> changed)
    {
        var lines = new List<string>();
        var reloads = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Whole base files first: the guest's loader maps every record the file states, so the
        // patch must come after it to stay on top.
        var bases = changed
            .Where(p => p.EndsWith(GdbSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string basePath in bases)
        {
            if (seen.Add(Key(basePath)))
            {
                reloads.Add(basePath);
            }
        }

        // The patches: changed or new, and every patch of a base reloaded above.
        foreach (string subPath in now.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bool differs = !loaded.TryGetValue(subPath, out byte[]? before) ||
                !before.AsSpan().SequenceEqual(now[subPath]);
            bool onReloadedBase = bases.Any(b => SameFamily(b, subPath));
            if ((differs || onReloadedBase) && seen.Add(Key(subPath)))
            {
                reloads.Add(subPath);
            }
        }

        // A patch outside the compositor's set that changed on the mount (a mod's own .gdb.tu the
        // composites do not carry): still the guest's loader reads it again.
        foreach (string path in changed
            .Where(p => p.EndsWith(TuSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!now.Keys.Any(k => SamePath(k, path)) && !loaded.Keys.Any(k => SamePath(k, path)) &&
                seen.Add(Key(path)))
            {
                reloads.Add(path);
            }
        }

        foreach (string path in reloads)
        {
            lines.Add(ReloadCommand + " " + path);
        }

        // Rows the previous composites stated that the new ones do not: back to the base.
        foreach (string subPath in loaded.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var gone = new SortedSet<uint>(RecordIds(loaded[subPath]));
            if (now.TryGetValue(subPath, out byte[]? after))
            {
                gone.ExceptWith(RecordIds(after));
            }

            if (gone.Count == 0)
            {
                continue;
            }

            var batch = new StringBuilder();
            int inLine = 0;
            foreach (uint id in gone)
            {
                if (inLine == 0)
                {
                    batch.Clear().Append(RevertCommand).Append(' ').Append(subPath);
                }

                batch.Append(" 0x").Append(id.ToString("X8"));
                if (++inLine == HashesPerLine)
                {
                    lines.Add(batch.ToString());
                    inLine = 0;
                }
            }

            if (inLine != 0)
            {
                lines.Add(batch.ToString());
            }
        }

        return lines;
    }

    /// <summary>Every record id a container states, in its sorted order; none for bytes that are not one.</summary>
    public static IEnumerable<uint> RecordIds(byte[] container)
    {
        if (!GdbView.TryParse(container, out GdbView view, out _))
        {
            yield break;
        }

        for (int i = 0; i < view.RecordCount; i++)
        {
            yield return view.RecordHash(i);
        }
    }

    private static string Key(string path) => path.Replace('\\', '/').ToLowerInvariant();

    /// <summary>Whether one path names the other's file at a separator, either way round.</summary>
    private static bool SamePath(string a, string b)
    {
        string ka = Key(a);
        string kb = Key(b);
        return ka == kb || ka.EndsWith("/" + kb, StringComparison.Ordinal) ||
            kb.EndsWith("/" + ka, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="basePath"/> is the base of the patch <paramref name="patchPath"/>.</summary>
    private static bool SameFamily(string basePath, string patchPath)
    {
        string patch = Key(patchPath);
        if (!patch.EndsWith(TuSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        return SamePath(basePath, patch.Substring(0, patch.Length - 3));
    }
}
