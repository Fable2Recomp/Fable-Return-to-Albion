using System.Linq;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`mesh_auto_lod_far`: two coarser generated levels after the three a phone always generates.</summary>
[TestSuite]
public sealed class MeshAutoLodFarTests
{
    [TestCase]
    public void TheFarLevelsExtendTheUsualOnesCoarser()
    {
        float[] usual = Engine_MdlAutoLod.ErrorsFor(false);
        float[] far = Engine_MdlAutoLod.ErrorsFor(true);
        AssertThat(usual).IsEqual(new[] { 0.002f, 0.008f, 0.03f });
        AssertThat(far.Take(usual.Length).ToArray()).IsEqual(usual);
        AssertThat(far.Length).IsEqual(5);
        for (int i = 1; i < far.Length; i++)
        {
            AssertThat(far[i] > far[i - 1]).IsTrue();
        }
    }

    [TestCase]
    public void TheKeyIsRegisteredOffWithAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == Engine_MdlAutoLod.FarKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(Engine_MdlAutoLod.FarKey)).IsTrue();
    }
}
