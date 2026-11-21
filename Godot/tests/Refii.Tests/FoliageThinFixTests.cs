using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Level;
using static GdUnit4.Assertions;
using Verdict = Refii.Level.Engine_LevelProps.Verdict;

namespace Refii.Tests;

/// <summary>`foliage_thin_fix`: plant thinning keeps mirrored large models and record copies whole.</summary>
[TestSuite]
public sealed class FoliageThinFixTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    private static readonly Vector3 Mirrored = new(-1f, 1f, 1f);

    /// <summary>A mirrored hedge (radius 2.75) is thinned by the share without the fix and kept whole with it.</summary>
    [TestCase]
    public void AMirroredHedgeIsKeptWhole()
    {
        AssertThat(Engine_LevelProps.Thinning(2.75f, Mirrored, false, 0.9, 0.25f, fix: false).ToString()).IsEqual(Verdict.Thinned.ToString());
        AssertThat(Engine_LevelProps.Thinning(2.75f, Mirrored, false, 0.9, 0.25f, fix: true).ToString()).IsEqual(Verdict.KeptLargeByFix.ToString());
        // The scale's largest axis, not only x.
        AssertThat(Engine_LevelProps.Thinning(1f, new Vector3(1f, 3f, 1f), false, 0.9, 0.25f, fix: true).ToString()).IsEqual(Verdict.KeptLargeByFix.ToString());
        // Unmirrored and large: kept as before, either way.
        AssertThat(Engine_LevelProps.Thinning(2.75f, Vector3.One, false, 0.9, 0.25f, fix: false).ToString()).IsEqual(Verdict.KeptLarge.ToString());
        AssertThat(Engine_LevelProps.Thinning(2.75f, Vector3.One, false, 0.9, 0.25f, fix: true).ToString()).IsEqual(Verdict.KeptLarge.ToString());
    }

    /// <summary>A small copy that is a `.save` record is never thinned with the fix; without it, by the share.</summary>
    [TestCase]
    public void ARecordCopyIsNeverThinned()
    {
        AssertThat(Engine_LevelProps.Thinning(0.36f, Mirrored, true, 0.9, 0.25f, fix: true).ToString()).IsEqual(Verdict.KeptRecordByFix.ToString());
        AssertThat(Engine_LevelProps.Thinning(0.36f, Mirrored, true, 0.9, 0.25f, fix: false).ToString()).IsEqual(Verdict.Thinned.ToString());
    }

    /// <summary>Small plants with no record are thinned by the share exactly as before.</summary>
    [TestCase]
    public void SmallPlantsAreThinnedAsBefore()
    {
        foreach (bool fix in new[] { false, true })
        {
            AssertThat(Engine_LevelProps.Thinning(0.36f, Mirrored, false, 0.9, 0.25f, fix).ToString()).IsEqual(Verdict.Thinned.ToString());
            AssertThat(Engine_LevelProps.Thinning(0.36f, Mirrored, false, 0.1, 0.25f, fix).ToString()).IsEqual(Verdict.Kept.ToString());
            AssertThat(Engine_LevelProps.Thinning(0.36f, Vector3.One, false, 0.5, 0.75f, fix).ToString()).IsEqual(Verdict.Kept.ToString());
        }

        double share = Engine_LevelProps.ThinHashShare(new Vector3(12.3f, 0f, -4.5f));
        AssertThat(share >= 0.0 && share < 1.0).IsTrue();
        AssertThat(Engine_LevelProps.ThinHashShare(new Vector3(12.3f, 9f, -4.5f))).IsEqual(share);
    }

    /// <summary>The `[foliage-thin]` line counts each verdict and names the models.</summary>
    [TestCase]
    public void TheTallyNamesTheModels()
    {
        var tally = new Engine_LevelProps.ThinTally();
        tally.Add("art/foliage/BW_Nettle1.mdl", Verdict.Thinned);
        tally.Add("art/foliage/BW_Nettle1.mdl", Verdict.Thinned);
        tally.Add("art/foliage/BW_Nettle1.mdl", Verdict.Kept);
        tally.Add("art/foliage/BW_Hedgerow1.mdl", Verdict.KeptRecordByFix);
        tally.Add("art/foliage/WM_Tree_Dead_V1.mdl", Verdict.KeptLargeByFix);
        tally.Add("art/foliage/BW_TreeLarge.mdl", Verdict.KeptLarge);
        string line = tally.Describe(fix: true);
        AssertThat(line).StartsWith("kept 4, thinned 2; the fix (on) kept 1 large mirrored and 1 record copies");
        AssertThat(line).Contains("BW_Hedgerow1 x1");
        AssertThat(line).Contains("WM_Tree_Dead_V1 x1");
        AssertThat(line).EndsWith("thinned most: BW_Nettle1 x2");
    }

    /// <summary>A phone Tuning row, on by default, in no preset, named for a player; the build reports at its end.</summary>
    [TestCase]
    public void TheSettingAndHookAreInPlace()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == Engine_LevelProps.FoliageThinFixKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("true");
        AssertThat(AndroidSettingNames.Keys.Contains(Engine_LevelProps.FoliageThinFixKey)).IsTrue();
        AssertThat(GraphicsPresets.Members.Contains(Engine_LevelProps.FoliageThinFixKey)).IsFalse();
        string props = FileText("Godot/src/Engine/level/props/Engine_LevelProps.cs");
        AssertThat(props).Contains("if (!KeepsFoliageCopy(block, index, foliageDensity)) continue;");
        AssertThat(props).Contains("        ReportFoliageThinning(level);\n        if (editorHierarchy is not null)");
    }
}
