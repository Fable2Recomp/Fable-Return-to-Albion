using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Refii.Launcher.Core;

public static class PatchSource
{
    private static readonly Regex BoolPattern = new(
        @"REXCVAR_DEFINE_BOOL\(([^,]+),\s*[^,]+,\s*[^,]+,\s*""([^""]+)""\)",
        RegexOptions.Compiled);

    private static readonly Regex IntPattern = new(
        @"REXCVAR_DEFINE_INT32\(([^,]+),\s*([^,]+),\s*[^,]+,\s*""([^""]+)""\)",
        RegexOptions.Compiled);

    private static readonly Regex IntPatternAlt = new(
        @"REXCVAR_DEFINE_INT32\(([^,]+),\s*([^,]+),\s*""([^""]+)""[^)]*\)",
        RegexOptions.Compiled);

    public static string GetPatchesLocation() =>
        Ini.GetString(
            "PatchesLocation",
            "https://raw.githubusercontent.com/Fable2Recomp/refii/master/src/hooks.cpp",
            "Github");

    public static bool OnlineMetadataEnabled() =>
        Ini.GetBool("EnableOnlineMetadata", false, "Github");

    public static List<Patch> ParseResponse(string responseData)
    {
        var patches = new List<Patch>();

        foreach (Match match in BoolPattern.Matches(responseData))
        {
            patches.Add(new Patch
            {
                Name = match.Groups[1].Value,
                Description = match.Groups[2].Value,
                Type = "bool",
            });
        }

        foreach (Match match in IntPattern.Matches(responseData))
        {
            patches.Add(new Patch
            {
                Name = match.Groups[1].Value,
                Description = match.Groups[3].Value,
                Type = "int",
                IntValue = ParseDefault(match.Groups[2].Value),
            });
        }

        foreach (Match match in IntPatternAlt.Matches(responseData))
        {
            string name = match.Groups[1].Value;

            bool alreadyFound = false;
            foreach (Patch existing in patches)
            {
                if (existing.Name == name && existing.Type == "int")
                {
                    alreadyFound = true;
                    break;
                }
            }

            if (alreadyFound)
            {
                continue;
            }

            patches.Add(new Patch
            {
                Name = name,
                Description = match.Groups[3].Value,
                Type = "int",
                IntValue = ParseDefault(match.Groups[2].Value),
            });
        }

        return patches;
    }

    private static int ParseDefault(string raw) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
}
