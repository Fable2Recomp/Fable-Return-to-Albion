using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Text;

namespace Refii.Gdb;

public static class Engine_BnkTextBankLoader
{
    private static string _gameDirectory = string.Empty;

    private static Func<string, byte[]?>? _resolver;

    private static TextBank? _bank;

    private static bool _tried;

    public static void SetGameDirectory(string directory)
    {
        _gameDirectory = directory ?? string.Empty;
        Invalidate();
    }

    /// <summary>Binds a resolver for mounted text banks (including mod overlays).</summary>
    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        _resolver = resolver;
        Invalidate();
    }

    /// <summary>Forces reloading the text bank on next access.</summary>
    public static void Invalidate()
    {
        _bank = null;
        _tried = false;
    }

    public static TextBank? Shared
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                _bank = LoadBank();
            }

            return _bank;
        }
    }

    public static bool TryLookup(uint tag, out string text)
    {
        text = string.Empty;
        TextBank? bank = Shared;
        return bank is not null && bank.TryLookup(tag, out text);
    }

    private static TextBank? LoadBank()
    {
        // 1. Try reading from the mounted filesystem via the resolver:
        if (_resolver is not null)
        {
            string[] probePaths = {
                "language/en-uk/text/book.babel",
                "data/language/en-uk/text/book.babel",
                "language/text/book.babel",
                "data/language/text/book.babel",
            };

            foreach (string path in probePaths)
            {
                byte[]? blob = _resolver(path);
                if (blob is not null && blob.Length > 0)
                {
                    try
                    {
                        TextBank? bank = TextBank.FromBytes(blob);
                        if (bank is not null)
                        {
                            GD.Print($"[babel] {bank.TagCount} text tag(s) loaded from mounted bank ({path})");
                            return bank;
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PushWarning($"[babel] failed reading mounted bank '{path}': {ex.Message}");
                    }
                }
            }
        }

        // 2. Fall back to reading shipped language files directly from disk:
        if (_gameDirectory.Length == 0)
        {
            return null;
        }

        string languageRoot = Path.Combine(_gameDirectory, "data", "language");
        if (!Directory.Exists(languageRoot))
        {
            return null;
        }

        var candidates = new List<string>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(languageRoot))
            {
                string babel = Path.Combine(dir, "text", "book.babel");
                if (File.Exists(babel))
                {
                    candidates.Add(babel);
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        candidates.Sort((a, b) =>
        {
            bool englishA = a.Contains("en-", StringComparison.OrdinalIgnoreCase);
            bool englishB = b.Contains("en-", StringComparison.OrdinalIgnoreCase);
            if (englishA != englishB)
            {
                return englishA ? -1 : 1;
            }

            return string.CompareOrdinal(a, b);
        });

        foreach (string path in candidates)
        {
            try
            {
                TextBank? bank = TextBank.FromBytes(File.ReadAllBytes(path));
                if (bank is not null)
                {
                    GD.Print(
                        $"[babel] {bank.TagCount} text tag(s) from " +
                        $"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))}");
                    return bank;
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }
}
