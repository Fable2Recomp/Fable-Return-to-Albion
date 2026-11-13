using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The phone A/B normal-map paths in standard_material_body.gdshaderinc, given through
/// `lite_extra_defines`: what each leaves of the tangent frame once preprocessed, since Godot keeps
/// the tangent attribute, its varyings and its own normal-map maths for any built-in named.
/// </summary>
[TestSuite]
public sealed class LiteNormalMapPathTests
{
    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static string? Include(string resPath, [CallerFilePath] string source = "")
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]));
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Replace("\r\n", "\n") : null;
    }

    /// <summary>The model lite builds (no terrain), each with <paramref name="extra"/> as lite_extra_defines, preprocessed.</summary>
    private static (string Path, string Active)[] Builds(params string[] extra) => Engine_ShaderVariant.AllVariantPaths
        .Where(p => !Engine_ShaderVariant.IsTerrainSplat(p))
        .Select(p =>
        {
            string code = Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(p)), p), extra);
            return (p, Engine_ShaderCeilingSource.Preprocess(code, p, r => Include(r)));
        })
        .ToArray();

    private static bool Names(string code, string builtin) =>
        Regex.IsMatch(code, @"\b" + builtin + @"\b", RegexOptions.CultureInvariant);

    private static readonly Regex Fetch = new(@"texture\(\s*normal_tex\b", RegexOptions.CultureInvariant);

    private static readonly Regex DirectWrite = new(@"\bNORMAL\s*=\s*mapped_view\s*;", RegexOptions.CultureInvariant);

    /// <summary>Without a define every lite build is as before: the map handed to Godot through NORMAL_MAP.</summary>
    [TestCase]
    public void WithoutADefineTheBuildsHandTheMapToGodot()
    {
        (string Path, string Active)[] builds = Builds();
        AssertThat(builds.Length).IsEqual(26);
        foreach ((string path, string active) in builds)
        {
            AssertThat(Names(active, "NORMAL_MAP")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Names(active, "TANGENT") && Names(active, "BINORMAL")).OverrideFailureMessage(path).IsTrue();
            AssertThat(DirectWrite.IsMatch(active)).OverrideFailureMessage(path).IsFalse();
        }
    }

    /// <summary>DIRECT: the body's mapped normal goes to NORMAL; NORMAL_MAP is never named, the frame still is.</summary>
    [TestCase]
    public void DirectWritesTheMappedNormalItself()
    {
        foreach ((string path, string active) in Builds("FABLE_LITE_NM_DIRECT"))
        {
            AssertThat(Names(active, "NORMAL_MAP")).OverrideFailureMessage(path).IsFalse();
            AssertThat(Names(active, "TANGENT") && Names(active, "BINORMAL")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Fetch.IsMatch(active)).OverrideFailureMessage(path).IsTrue();
            AssertThat(DirectWrite.IsMatch(active)).OverrideFailureMessage(path).IsTrue();
        }
    }

    /// <summary>DERIV: the map is fetched and applied, and no build names TANGENT, BINORMAL or NORMAL_MAP.</summary>
    [TestCase]
    public void DerivNamesNoTangentFrame()
    {
        foreach (string[] extra in new[] { new[] { "FABLE_LITE_NM_DERIV" }, new[] { "FABLE_LITE_NM_DERIV", "FABLE_LITE_NM_DIRECT" } })
        {
            foreach ((string path, string active) in Builds(extra))
            {
                AssertThat(Names(active, "TANGENT") || Names(active, "BINORMAL") || Names(active, "NORMAL_MAP"))
                    .OverrideFailureMessage(path).IsFalse();
                AssertThat(Fetch.IsMatch(active)).OverrideFailureMessage(path).IsTrue();
                AssertThat(DirectWrite.IsMatch(active)).OverrideFailureMessage(path).IsTrue();
                AssertThat(active.Contains("dFdx(uv)", StringComparison.Ordinal)).OverrideFailureMessage(path).IsTrue();
            }
        }
    }

    /// <summary>FRAME_ONLY (diagnostic): the frame still named and read, no fetch and no NORMAL_MAP.</summary>
    [TestCase]
    public void FrameOnlyKeepsTheFrameAndDropsTheMap()
    {
        foreach ((string path, string active) in Builds("FABLE_LITE_NM_FRAME_ONLY"))
        {
            AssertThat(Names(active, "TANGENT") && Names(active, "BINORMAL")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Names(active, "NORMAL_MAP")).OverrideFailureMessage(path).IsFalse();
            AssertThat(Fetch.IsMatch(active)).OverrideFailureMessage(path).IsFalse();
        }
    }

    /// <summary>A build already without a normal map stays without the frame whichever path is asked.</summary>
    [TestCase]
    public void TheNoNormalMapFoldStillWins()
    {
        foreach (string define in new[] { "FABLE_LITE_NM_DIRECT", "FABLE_LITE_NM_DERIV", "FABLE_LITE_NM_FRAME_ONLY" })
        {
            foreach ((string path, string active) in Builds(define, "FABLE_SPEC_NO_NORMAL_MAP"))
            {
                AssertThat(Names(active, "TANGENT") || Names(active, "BINORMAL") || Names(active, "NORMAL_MAP"))
                    .OverrideFailureMessage(path + " " + define).IsFalse();
                AssertThat(Fetch.IsMatch(active)).OverrideFailureMessage(path + " " + define).IsFalse();
            }
        }
    }

    /// <summary>
    /// DERIV's frame is the mesh's: T the u gradient normalised, B = cross(N, T) turned to the v
    /// gradient (the file's w), and B takes the side test's sign as BINORMAL does in the shipped path.
    /// </summary>
    [TestCase]
    public void DerivBuildsTheMeshesFrameAndTakesTheSideTest()
    {
        foreach ((string path, string active) in Builds("FABLE_LITE_NM_DERIV"))
        {
            AssertThat(Regex.IsMatch(active, @"\bfloat\s+side\s*=\s*1\.0\s*;")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Regex.IsMatch(active, @"\bvec3\s+bn\s*=\s*cross\(\s*NORMAL\s*,\s*t\s*\)")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Regex.IsMatch(active, @"\?\s*-side\s*:\s*side")).OverrideFailureMessage(path).IsTrue();
        }
    }

    /// <summary>The lite terrain builds that bind normal maps (not flat, not the page), preprocessed with <paramref name="extra"/>.</summary>
    private static (string Path, string Active)[] TerrainBuilds(params string[] extra) => Engine_ShaderVariant.AllVariantPaths
        .Where(p => Engine_ShaderVariant.IsTerrainSplat(p) && !p.Contains("_flat", StringComparison.Ordinal) && !p.Contains("_page", StringComparison.Ordinal))
        .Select(p =>
        {
            string code = Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(p)), p), extra);
            return (p, Engine_ShaderCeilingSource.Preprocess(code, p, r => Include(r)));
        })
        .ToArray();

    /// <summary>
    /// FABLE_LITE_TERRAIN_NM_ANALYTIC: the ground's analytic frame rebuilt in the fragment and the
    /// mapped normal written to NORMAL, so no build names TANGENT, BINORMAL or NORMAL_MAP; without
    /// it the ground hands the map to Godot as before.
    /// </summary>
    [TestCase]
    public void TheGroundsAnalyticFrameNamesNoTangentFrame()
    {
        (string Path, string Active)[] plain = TerrainBuilds();
        AssertThat(plain.Length).IsEqual(4);
        foreach ((string path, string active) in plain)
        {
            AssertThat(Names(active, "NORMAL_MAP") && Names(active, "BINORMAL")).OverrideFailureMessage(path).IsTrue();
        }

        foreach (string[] extra in new[] { new[] { "FABLE_LITE_TERRAIN_NM_ANALYTIC" } })
        {
            foreach ((string path, string active) in TerrainBuilds(extra))
            {
                AssertThat(Names(active, "TANGENT") || Names(active, "BINORMAL") || Names(active, "NORMAL_MAP"))
                    .OverrideFailureMessage(path + " " + string.Join(",", extra)).IsFalse();
                AssertThat(Regex.IsMatch(active, @"\bNORMAL\s*=\s*normalize\(\s*\(nm_t\b")).OverrideFailureMessage(path).IsTrue();
                AssertThat(Regex.IsMatch(active, @"\?\s*-side\s*:\s*side")).OverrideFailureMessage(path).IsTrue();
            }
        }
    }

    /// <summary>`lite_normal_maps`: registered, named for a player, default full (no define), each value its define.</summary>
    [TestCase]
    public void TheNormalMapsSettingComposesItsDefine()
    {
        Refii.Launcher.Core.RuntimeCVarDefinition? row = Refii.Launcher.Core.RuntimeCVarsAndroid.Tuning
            .SingleOrDefault(d => d.Name == Engine_ShaderVariant.NormalMapsKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("full");
        AssertThat(Refii.Launcher.Core.AndroidSettingNames.Keys.Contains(Engine_ShaderVariant.NormalMapsKey)).IsTrue();

        AssertThat(Engine_ShaderVariant.ParseNormalMaps("")).IsEqual(Engine_ShaderVariant.NormalMapPath.Full);
        AssertThat(Engine_ShaderVariant.ParseNormalMaps("nonsense")).IsEqual(Engine_ShaderVariant.NormalMapPath.Full);
        AssertThat(Engine_ShaderVariant.ParseNormalMaps("\"Fast\"")).IsEqual(Engine_ShaderVariant.NormalMapPath.Fast);
        AssertThat(Engine_ShaderVariant.NormalMapDefines(Engine_ShaderVariant.NormalMapPath.Full)).IsEmpty();
        AssertThat(Engine_ShaderVariant.NormalMapDefines(Engine_ShaderVariant.ParseNormalMaps("direct"))).IsEqual(new[] { "FABLE_LITE_NM_DIRECT" });
        AssertThat(Engine_ShaderVariant.NormalMapDefines(Engine_ShaderVariant.ParseNormalMaps("fast"))).IsEqual(new[] { "FABLE_LITE_NM_DERIV" });
        AssertThat(Engine_ShaderVariant.NormalMapDefines(Engine_ShaderVariant.ParseNormalMaps("off"))).IsEqual(new[] { "FABLE_SPEC_NO_NORMAL_MAP" });

        // Fast and off each leave no line naming the tangent frame in any model build.
        foreach (string value in new[] { "fast", "off" })
        {
            foreach ((string path, string active) in Builds(Engine_ShaderVariant.NormalMapDefines(Engine_ShaderVariant.ParseNormalMaps(value))))
            {
                AssertThat(Names(active, "TANGENT") || Names(active, "BINORMAL") || Names(active, "NORMAL_MAP"))
                    .OverrideFailureMessage(path + " " + value).IsFalse();
            }
        }

        // Off the phone the setting is never read: full.
        AssertThat(Engine_ShaderVariant.NormalMapsOn).IsEqual(Engine_ShaderVariant.NormalMapPath.Full);
        AssertThat(Engine_ShaderVariant.ProcessWide("FABLE_SPEC_NO_NORMAL_MAP")).IsFalse();
    }

    /// <summary>
    /// THE GAME'S TANGENT CONVENTION, measured on the shipped models when REFII_GLOBALS_DIR names
    /// the game's data/Globals folder (skipped otherwise): Godot's binormal, cross(N, T) * w, runs
    /// along +v and T along +u, mirrored UV islands included. 2026-11-11 over 1500 models of
    /// globals_models.bnk: 11,113,556 vertex-triangle pairs along +v against 1,556, T along +u
    /// 11,113,838 against 1,274; w &lt; 0 exactly where the island is mirrored.
    /// </summary>
    [TestCase]
    public void TheGamesBinormalRunsAlongPlusV()
    {
        string? dir = System.Environment.GetEnvironmentVariable("REFII_GLOBALS_DIR");
        if (string.IsNullOrEmpty(dir) || !System.IO.File.Exists(Path.Combine(dir, "globals_models.bnk")))
        {
            return;
        }

        AssertThat(Refii.Bnk.BnkArchive.TryOpen(Path.Combine(dir, "globals_model_headers.bnk"), out Refii.Bnk.BnkArchive? headers, out _)).IsTrue();
        AssertThat(Refii.Bnk.BnkArchive.TryOpen(Path.Combine(dir, "globals_models.bnk"), out Refii.Bnk.BnkArchive? bodies, out _)).IsTrue();
        var headerOf = headers!.Entries.GroupBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        long along = 0, against = 0, uAlong = 0, uAgainst = 0;
        int models = 0;
        foreach (Refii.Bnk.BnkEntry entry in bodies!.Entries)
        {
            if (models >= 300) break;
            if (!entry.RelativePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || !headerOf.TryGetValue(entry.RelativePath, out Refii.Bnk.BnkEntry? head)) continue;
            byte[] data = headers.ReadEntryBytes(head).Concat(bodies.ReadEntryBytes(entry)).ToArray();
            if (!Engine_MdlData.TryParseInfo(data, out MdlInfo info, out _) || info.IsStub ||
                !Engine_MdlGeometry.TryBuild(data, info, out System.Collections.Generic.List<MdlGeometry> surfaces)) continue;
            models++;
            foreach (MdlGeometry g in surfaces.Where(s => s.Tangents.Length != 0 && s.Normals.Length != 0))
            {
                for (int i = 0; i + 2 < g.Indices.Length; i += 3)
                {
                    int a = (int)g.Indices[i], b = (int)g.Indices[i + 1], c = (int)g.Indices[i + 2];
                    Godot.Vector3 P(int k) => new(g.Positions[k * 3], g.Positions[(k * 3) + 1], g.Positions[(k * 3) + 2]);
                    Godot.Vector2 U(int k) => new(g.Uvs[k * 2], g.Uvs[(k * 2) + 1]);
                    Godot.Vector3 e1 = P(b) - P(a), e2 = P(c) - P(a);
                    Godot.Vector2 d1 = U(b) - U(a), d2 = U(c) - U(a);
                    float det = (d1.X * d2.Y) - (d2.X * d1.Y);
                    if (MathF.Abs(det) < 1e-8f) continue;
                    Godot.Vector3 dPdu = ((e1 * d2.Y) - (e2 * d1.Y)) / det, dPdv = ((e2 * d1.X) - (e1 * d2.X)) / det;
                    if (dPdu.LengthSquared() < 1e-12f || dPdv.LengthSquared() < 1e-12f) continue;
                    foreach (int k in new[] { a, b, c })
                    {
                        var n = new Godot.Vector3(g.Normals[k * 3], g.Normals[(k * 3) + 1], g.Normals[(k * 3) + 2]);
                        var t = new Godot.Vector3(g.Tangents[k * 4], g.Tangents[(k * 4) + 1], g.Tangents[(k * 4) + 2]);
                        float bd = (n.Cross(t) * g.Tangents[(k * 4) + 3]).Dot(dPdv.Normalized());
                        float td = t.Dot(dPdu.Normalized());
                        if (MathF.Abs(bd) < 0.3f || MathF.Abs(td) < 0.3f) continue;
                        if (bd > 0f) along++; else against++;
                        if (td > 0f) uAlong++; else uAgainst++;
                    }
                }
            }
        }

        headers.Dispose();
        bodies.Dispose();
        AssertThat(along).IsGreater(10000);
        AssertThat(against * 100 < along).OverrideFailureMessage($"+v {along}, -v {against}").IsTrue();
        AssertThat(uAgainst * 100 < uAlong).OverrideFailureMessage($"+u {uAlong}, -u {uAgainst}").IsTrue();
    }
}
