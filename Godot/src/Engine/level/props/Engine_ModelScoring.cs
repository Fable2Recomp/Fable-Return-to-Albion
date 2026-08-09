using System;
using System.Collections.Generic;

namespace Refii.Level;

public static class Engine_ModelScoring
{
    public const int AcceptThreshold = 6500;

    public static string? ResolveByEntityScore(string entityName)
    {
        string entityKey = Engine_PropModelIndex.EntityKey(entityName);
        if (entityKey.Length == 0)
        {
            return null;
        }

        StreamingModelCandidate? best = null;
        int bestScore = int.MinValue;

        foreach (StreamingModelCandidate candidate in
                 Engine_StreamingCandidates.Candidates)
        {
            int score = Score(entityKey, candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (bestScore >= AcceptThreshold && best is not null)
        {
            string? resolved = best.ResolvedPath.Length != 0
                ? best.ResolvedPath
                : Engine_PropModelIndex.ResolveByPath(best.HintPath);

            if (resolved is not null)
            {
                return resolved;
            }
        }

        return ResolveByGlobalScore(entityKey);
    }

    private static string? ResolveByGlobalScore(string entityKey)
    {
        string? best = null;
        int bestScore = int.MinValue;

        foreach (KeyValuePair<string, List<PropModelAsset>> bucket in
                 Engine_PropModelIndex.CompactKeys())
        {
            foreach (PropModelAsset asset in bucket.Value)
            {
                var candidate = new StreamingModelCandidate
                {
                    HintPath = asset.VirtualPath,
                    ResolvedPath = asset.VirtualPath,
                    Key = Engine_GmdLayout.CompactKey(NameOf(asset.VirtualPath)),
                    PathKey = Engine_GmdLayout.CompactKey(
                        asset.VirtualPath.Replace('\\', '/')),
                };

                int score = Score(entityKey, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = asset.VirtualPath;
                }
            }
        }

        return bestScore >= AcceptThreshold ? best : null;
    }

    public static int Score(string entityKey, StreamingModelCandidate candidate)
    {
        string path = candidate.HintPath;
        string key = candidate.Key;
        string pathKey = candidate.PathKey;

        if (entityKey.Length == 0 || key.Length == 0)
        {
            return int.MinValue;
        }

        bool Has(string needle) => entityKey.Contains(needle, StringComparison.Ordinal);
        bool CandHas(string needle) => key.Contains(needle, StringComparison.Ordinal);
        bool PathHas(string needle) => pathKey.Contains(needle, StringComparison.Ordinal);

        if (IsOrNumbered(entityKey, "generalstore") &&
            (CandHas("signgeneralstore") || PathHas("signgeneralstore")))
        {
            return int.MinValue;
        }

        bool bareGeneralStore = IsOrNumbered(entityKey, "generalstore");
        bool tavernShell = IsOrNumbered(entityKey, "bsmarkettavern") ||
                           IsOrNumbered(entityKey, "markettavern");
        bool openStallShell = IsOrVariant(entityKey, "bsopenstall") ||
                              IsOrVariant(entityKey, "bsmarketopenstall");
        bool marketStallShell = IsOrVariant(entityKey, "bsmarketstall") ||
                                IsOrVariant(entityKey, "marketstall") ||
                                IsOrNumbered(entityKey, "bstarotstall") ||
                                IsOrNumbered(entityKey, "tarotstall");

        if ((bareGeneralStore || tavernShell || openStallShell || marketStallShell) &&
            (PathHas("bstownhouse") || PathHas("townhouse")))
        {
            return int.MinValue;
        }

        if (bareGeneralStore || tavernShell)
        {
            return int.MinValue;
        }

        int score = int.MinValue;

        int SameVariant(int baseScore)
        {
            int adjusted = baseScore;
            foreach (string v in new[] { "v1", "v2", "v3", "v4", "v5", "v6" })
            {
                if (Has(v) && PathHas(v))
                {
                    adjusted += 500;
                }
            }

            return adjusted;
        }

        if (openStallShell)
        {
            if (PathHas("esashopmarketstall") || PathHas("signstall"))
            {
                return int.MinValue;
            }

            if (PathHas("bsopenstall") || PathHas("bsmarketopenstall") || PathHas("openstall"))
            {
                score = Math.Max(score, SameVariant(18500));
            }
        }

        if (marketStallShell)
        {
            if (PathHas("esashopmarketstall") || PathHas("signstall"))
            {
                return int.MinValue;
            }

            if (Has("tarotstall") && PathHas("tarotstall"))
            {
                score = Math.Max(score, SameVariant(19000));
            }
            else if ((PathHas("bsmarketstall") || PathHas("marketstall")) &&
                     !PathHas("esashopmarketstall"))
            {
                score = Math.Max(score, SameVariant(18500));
            }
        }

        if (entityKey == key)
        {
            score = 12000;
        }
        else if (key.Contains(entityKey, StringComparison.Ordinal))
        {
            score = 9000 + entityKey.Length;
        }
        else if (entityKey.Contains(key, StringComparison.Ordinal))
        {
            score = 7000 + key.Length;
        }

        foreach (Engine_ModelAliases.Alias alias in Engine_ModelAliases.Table)
        {
            if (Has(alias.Entity) && (CandHas(alias.Model) || PathHas(alias.Model)))
            {
                score = Math.Max(score, alias.Score);
            }
        }

        if (score == int.MinValue)
        {
            return score;
        }

        if (candidate.HasEntry)
        {
            score += 500;
        }

        if (candidate.FromGmd)
        {
            score += 150;
        }

        if (Has("facademid") && PathHas("facademid"))
        {
            score += 500;
        }

        if (Has("facade") && PathHas("facade"))
        {
            score += 150;
        }

        return score - Math.Min(path.Length, 200);
    }

    private static string NameOf(string path)
    {
        string normalised = path.Replace('\\', '/');
        int slash = normalised.LastIndexOf('/');
        string leaf = slash < 0 ? normalised : normalised[(slash + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot <= 0 ? leaf : leaf[..dot];
    }

    private static bool IsOrNumbered(string key, string basis)
    {
        if (key == basis)
        {
            return true;
        }

        if (key.Length <= basis.Length || !key.StartsWith(basis, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = basis.Length; i < key.Length; i++)
        {
            if (!char.IsAsciiDigit(key[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOrVariant(string key, string basis)
    {
        if (IsOrNumbered(key, basis))
        {
            return true;
        }

        if (key.Length <= basis.Length + 1 ||
            !key.StartsWith(basis, StringComparison.Ordinal) ||
            key[basis.Length] != 'v')
        {
            return false;
        }

        for (int i = basis.Length + 1; i < key.Length; i++)
        {
            if (!char.IsAsciiDigit(key[i]))
            {
                return false;
            }
        }

        return true;
    }
}
