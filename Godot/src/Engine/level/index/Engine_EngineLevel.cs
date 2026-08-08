using System;
using System.Collections.Generic;

namespace Refii.Level;

public static class Engine_EngineLevel
{
    private const int MinimumRunLength = 5;

    public static List<string> ReadEhfReferences(byte[] engineLevel)
    {
        var references = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (engineLevel is null || engineLevel.Length == 0)
        {
            return references;
        }

        int runStart = -1;
        for (int i = 0; i <= engineLevel.Length; i++)
        {
            bool printable = i < engineLevel.Length &&
                             engineLevel[i] >= 32 && engineLevel[i] < 127;

            if (printable)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                continue;
            }

            if (runStart >= 0)
            {
                int length = i - runStart;
                if (length >= MinimumRunLength)
                {
                    string text = System.Text.Encoding.ASCII.GetString(
                        engineLevel, runStart, length);
                    if (text.EndsWith(".ehf", StringComparison.OrdinalIgnoreCase) &&
                        seen.Add(text))
                    {
                        references.Add(text);
                    }
                }

                runStart = -1;
            }
        }

        return references;
    }

    public static string LeafOf(string reference)
    {
        string normalised = reference.Replace((char)92, '/');
        int slash = normalised.LastIndexOf('/');
        return slash < 0 ? normalised : normalised[(slash + 1)..];
    }
}
