using System;
using System.Collections.Generic;

namespace Refii.Level;

public sealed class ScenarioManifest
{
    public List<string> Gdb { get; } = new();
    public List<string> EngineTheme { get; } = new();
    public List<string> Ghf { get; } = new();
    public List<string> AudioMaps { get; } = new();
    public List<string> Hdb { get; } = new();
    public List<string> Genv { get; } = new();
    public List<string> EngineLevel { get; } = new();
    public List<string> AiConfig { get; } = new();
    public List<string> Save { get; } = new();
    public List<string> Bnk { get; } = new();
    public List<string> HavokScenario { get; } = new();
    public List<string> Unrecognized { get; } = new();

    public int Total =>
        Gdb.Count + EngineTheme.Count + Ghf.Count + AudioMaps.Count + Hdb.Count +
        Genv.Count + EngineLevel.Count + AiConfig.Count + Save.Count + Bnk.Count +
        HavokScenario.Count;

    public string Describe() =>
        $"{Total} entries ({Gdb.Count} gdb, {Ghf.Count} ghf, {AudioMaps.Count} audio, " +
        $"{Hdb.Count} hdb, {Genv.Count} genv, {EngineLevel.Count} engine_level, " +
        $"{EngineTheme.Count} engine_theme, {AiConfig.Count} ai_config, {Save.Count} save, " +
        $"{Bnk.Count} bnk, {HavokScenario.Count} havok)";
}

public static class Engine_LevelManifest
{
    public const string DefaultScenario = "DefaultScenario";

    public static string NormalizeLevelName(string name)
    {
        string s = name.Replace('/', (char)92);
        string doubled = new string((char)92, 2);
        string single = new string((char)92, 1);
        while (s.Contains(doubled, StringComparison.Ordinal))
        {
            s = s.Replace(doubled, single, StringComparison.Ordinal);
        }

        return s;
    }

    public static uint LevelId(string name) =>
        Refii.Core.Engine_GameHash.Fnv1(NormalizeLevelName(name));

    public static ScenarioManifest Parse(string text)
    {
        var manifest = new ScenarioManifest();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            List<string> bucket =
                EndsWith(line, ".ghf") ? manifest.Ghf :
                EndsWith(line, ".hfa") || EndsWith(line, ".ama") ||
                EndsWith(line, ".amm") || EndsWith(line, ".amr") ? manifest.AudioMaps :
                EndsWith(line, ".hdb") ? manifest.Hdb :
                EndsWith(line, ".genv") ? manifest.Genv :
                EndsWith(line, ".engine_level") ? manifest.EngineLevel :
                EndsWith(line, ".engine_theme") ? manifest.EngineTheme :
                EndsWith(line, ".save") ? manifest.Save :
                EndsWith(line, ".gdb") ? manifest.Gdb :
                EndsWith(line, ".ai_config") ? manifest.AiConfig :
                EndsWith(line, ".bnk") ? manifest.Bnk :
                EndsWith(line, ".havok_scenario") ? manifest.HavokScenario :
                manifest.Unrecognized;

            bucket.Add(line);
        }

        return manifest;
    }

    public static bool TryResolve(
        LevelEntry level,
        Func<string, byte[]?> read,
        out ScenarioManifest manifest,
        out string manifestPath,
        out string scenarioUsed)
    {
        manifest = new ScenarioManifest();
        manifestPath = string.Empty;
        scenarioUsed = string.Empty;

        string primary = level.Scenario.Length != 0 ? level.Scenario : DefaultScenario;
        foreach (string scenario in Candidates(primary))
        {
            string lower = scenario.ToLowerInvariant();
            string path =
                $"worlds/{level.World.ToLowerInvariant()}/{level.LevelPath.ToLowerInvariant()}/" +
                $"{lower}/{lower}.list";

            byte[]? bytes = ResolveBytes(path, read);
            if (bytes is null)
            {
                continue;
            }

            manifest = Parse(System.Text.Encoding.ASCII.GetString(bytes));
            manifestPath = path;
            scenarioUsed = scenario;
            return true;
        }

        return false;
    }

    public static bool TryResolvePath(
        string manifestLine, Func<string, byte[]?> read, out string virtualPath)
    {
        virtualPath = VirtualPath(manifestLine);
        if (ResolveBytes(virtualPath, read) is not null)
        {
            return true;
        }

        virtualPath = string.Empty;
        return false;
    }

    public static string VirtualPath(string manifestLine)
    {
        string path = manifestLine.Replace((char)92, '/').ToLowerInvariant();
        if (path.StartsWith("data/", StringComparison.Ordinal))
        {
            path = path["data/".Length..];
        }

        return path;
    }

    public static string? TryRead(
        LevelEntry level, Func<string, byte[]?> read, out string manifestPath)
    {
        manifestPath = string.Empty;
        if (level.Scenario.Length == 0)
        {
            return null;
        }

        if (!TryResolve(level, read, out _, out string path, out _))
        {
            return null;
        }

        byte[]? bytes = ResolveBytes(path, read);
        if (bytes is null)
        {
            return null;
        }

        manifestPath = path;
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    public static bool TryFind(
        LevelEntry level, Func<string, byte[]?> read, string extension, out string found)
    {
        found = string.Empty;

        if (!TryResolve(level, read, out ScenarioManifest manifest, out _, out _))
        {
            return false;
        }

        List<string>? bucket = extension.ToLowerInvariant() switch
        {
            ".gdb" => manifest.Gdb,
            ".ghf" => manifest.Ghf,
            ".hdb" => manifest.Hdb,
            ".genv" => manifest.Genv,
            ".engine_level" => manifest.EngineLevel,
            ".engine_theme" => manifest.EngineTheme,
            ".save" => manifest.Save,
            ".ai_config" => manifest.AiConfig,
            ".bnk" => manifest.Bnk,
            ".havok_scenario" => manifest.HavokScenario,
            _ => null,
        };

        if (bucket is not null && bucket.Count != 0)
        {
            found = VirtualPath(bucket[0]);
            return true;
        }

        foreach (string line in bucket is null ? EveryLine(manifest) : bucket)
        {
            if (line.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                found = VirtualPath(line);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EveryLine(ScenarioManifest manifest)
    {
        foreach (string line in manifest.AudioMaps) yield return line;
        foreach (string line in manifest.Unrecognized) yield return line;
    }

    private static IEnumerable<string> Candidates(string primary)
    {
        yield return primary;
        if (!primary.Equals(DefaultScenario, StringComparison.OrdinalIgnoreCase))
        {
            yield return DefaultScenario;
        }
    }

    private static byte[]? ResolveBytes(string path, Func<string, byte[]?> read) =>
        read(path) ?? read("data/" + path);

    private static bool EndsWith(string line, string extension) =>
        line.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
