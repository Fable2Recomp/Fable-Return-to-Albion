using System;

namespace Refii.Level;

public static class Engine_LevelGdb
{
    public static byte[]? TryRead(
        LevelEntry level, Func<string, byte[]?> read, out string path)
    {
        path = string.Empty;

        // THE SCENARIO MANIFEST FIRST, because it STATES the answer.
        //
        // `Data\Worlds\<World>\<Level>\<Scenario>\<Scenario>.list` names every file the
        // level is made of, and the level's gdb is one of them — for BWSSlums's default
        // scenario that is `Worlds\Albion\BWSSlums\DefaultScenario\DefaultScenario.gdb`,
        // which lives under the SCENARIO directory and is not a sibling of the heightfield at
        // all. The search below infers a path instead, and it infers it from the heightfield's
        // own folder — whose last component is literally "Heightfields", not a scenario name.
        //
        // Getting this wrong does not fail loudly. It finds SOME level gdb and builds a world
        // out of another story state's placements, which is how objects come out duplicated
        // and the wrong NPCs spawn.
        if (level.Scenario.Length != 0 &&
            Engine_LevelManifest.TryFind(level, read, ".gdb", out string stated))
        {
            byte[]? statedBytes = read(stated);
            if (statedBytes is not null)
            {
                path = stated;
                return statedBytes;
            }
        }

        string reference = level.MainEhfPath.Length != 0
            ? level.MainEhfPath
            : level.HeightfieldPath;

        if (reference.Length == 0)
        {
            return null;
        }

        int slash = reference.LastIndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        string directory = reference[..slash];
        int parent = directory.LastIndexOf('/');
        string scenario = parent < 0 ? directory : directory[(parent + 1)..];

        string search = directory;
        while (search.Length != 0)
        {
            foreach (string candidate in new[]
                     {
                         $"{search}/{scenario}.gdb",
                         $"{search}/{scenario}/{scenario}.gdb",
                     })
            {
                byte[]? bytes = read(candidate);
                if (bytes is not null)
                {
                    path = candidate;
                    return bytes;
                }
            }

            int cut = search.LastIndexOf('/');
            if (cut <= 0)
            {
                break;
            }

            search = search[..cut];
        }

        return null;
    }

    public static string SiblingWithExtension(string gdbPath, string extension)
    {
        if (gdbPath.Length == 0)
        {
            return string.Empty;
        }

        int dot = gdbPath.LastIndexOf('.');
        return dot < 0 ? gdbPath + extension : gdbPath[..dot] + extension;
    }
}
