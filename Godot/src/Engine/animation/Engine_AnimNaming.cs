using System;
using System.Text;

namespace Refii.Animations;

public static class Engine_AnimNaming
{
    public static string Plain(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var text = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            text.Append(c is ' ' or '-' or '.' ? '_' : char.ToLowerInvariant(c));
        }

        return text.ToString().Trim('_');
    }

    public static string WithoutShadow(string name) =>
        name.StartsWith("shadow_", StringComparison.Ordinal) && name.Length > 7
            ? name[7..]
            : name;

    public static string Normalise(string name) => WithoutShadow(Plain(name));

    public static string CleanClipName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "clip";
        }

        var text = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            text.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        string cleaned = text.ToString().Trim('_');
        return cleaned.Length == 0 ? "clip" : cleaned;
    }
}
