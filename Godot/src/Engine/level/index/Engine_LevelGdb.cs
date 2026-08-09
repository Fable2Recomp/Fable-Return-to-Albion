using System;

namespace Refii.Level;

public static class Engine_LevelGdb
{
    public static byte[]? TryRead(
        LevelEntry level, Func<string, byte[]?> read, out string path)
    {
        path = string.Empty;

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
