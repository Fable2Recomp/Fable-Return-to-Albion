using System;
using System.Collections.Generic;

namespace Refii.Level;

public sealed class StreamingModelCandidate
{
    public string HintPath { get; set; } = string.Empty;

    public string ResolvedPath { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string PathKey { get; set; } = string.Empty;

    public bool FromGmd { get; set; }

    public bool HasEntry => ResolvedPath.Length != 0;
}

public static class Engine_StreamingCandidates
{
    private static Func<IEnumerable<string>>? _hints;

    private static List<StreamingModelCandidate>? _candidates;

    public static void SetHintSource(Func<IEnumerable<string>>? source)
    {
        _hints = source;
        _candidates = null;
    }

    public static void Invalidate() => _candidates = null;

    public static IReadOnlyList<StreamingModelCandidate> Candidates
    {
        get
        {
            Build();
            return _candidates ?? new List<StreamingModelCandidate>();
        }
    }

    private static void Build()
    {
        if (_candidates is not null || _hints is null)
        {
            return;
        }

        var list = new List<StreamingModelCandidate>();
        var seen = new Dictionary<string, StreamingModelCandidate>(
            StringComparer.OrdinalIgnoreCase);

        foreach (string file in _hints())
        {
            string lower = file.Replace('\\', '/').ToLowerInvariant();
            string hint;
            bool fromGmd;

            if (lower.EndsWith(".mdl.gmd", StringComparison.Ordinal))
            {
                hint = file[..^4];
                fromGmd = true;
            }
            else if (lower.EndsWith(".hkx", StringComparison.Ordinal))
            {
                hint = file[..^4] + ".mdl";
                fromGmd = false;
            }
            else
            {
                continue;
            }

            string norm = hint.Replace('\\', '/').ToLowerInvariant();
            if (seen.TryGetValue(norm, out StreamingModelCandidate? existing))
            {
                if (fromGmd)
                {
                    existing.FromGmd = true;
                }

                continue;
            }

            string display = NameOf(hint);
            var candidate = new StreamingModelCandidate
            {
                HintPath = hint,
                Key = Engine_LevelProps.CompactText(display),
                FromGmd = fromGmd,
                ResolvedPath = Engine_PropModelIndex.ResolveByPath(hint) ?? string.Empty,
            };

            candidate.PathKey = Engine_LevelProps.CompactText(
                candidate.HintPath + " " + candidate.ResolvedPath);

            seen[norm] = candidate;
            list.Add(candidate);
        }

        _candidates = list;
        Godot.GD.Print($"[props] streaming candidates: {list.Count}");
    }

    private static string NameOf(string path)
    {
        string normalised = path.Replace('\\', '/');
        int slash = normalised.LastIndexOf('/');
        string leaf = slash < 0 ? normalised : normalised[(slash + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot <= 0 ? leaf : leaf[..dot];
    }
}
