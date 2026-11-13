namespace Refii.Tests.Level;

using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Launcher.Core;
using Refii.Level;
using Refii.Models;

/// <summary>`prop_cell_metres` (Android): the batch cell for everything but undergrowth; and the lodcensus categories it is read with.</summary>
[TestSuite]
public class PropCellMetresTests
{
    [TestCase]
    public void UnsetZeroAndInvalidKeepTheSharedCellAndTheRestIsHeldToEightToThirtyTwo()
    {
        AssertThat(Engine_LevelProps.ClampPropCell(0f)).IsEqual(0f);
        AssertThat(Engine_LevelProps.ClampPropCell(-4f)).IsEqual(0f);
        AssertThat(Engine_LevelProps.ClampPropCell(float.NaN)).IsEqual(0f);
        AssertThat(Engine_LevelProps.ClampPropCell(4f)).IsEqual(8f);
        AssertThat(Engine_LevelProps.ClampPropCell(16f)).IsEqual(16f);
        AssertThat(Engine_LevelProps.ClampPropCell(100f)).IsEqual(Engine_LevelProps.BatchCellMetres);
    }

    [TestCase]
    public void TheKeyIsRegisteredOffWithAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == Engine_LevelProps.PropCellKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("0");
        AssertThat(AndroidSettingNames.Keys.Contains(Engine_LevelProps.PropCellKey)).IsTrue();
    }

    [TestCase]
    public void CensusCategoriesComeFromTheBuildAndWhatDrawsIt()
    {
        const string lite = "res://src/Engine/material/lite/";
        AssertThat(Engine_MdlLodCensus.Category(lite + "standard_material_culled_lite.gdshader", false, false, true, "Engine_LevelProps")).IsEqual("props batch");
        AssertThat(Engine_MdlLodCensus.Category(lite + "standard_material_lite.gdshader", false, false, false, "GameEntities")).IsEqual("props");
        AssertThat(Engine_MdlLodCensus.Category(lite + "standard_material_tree_lite.gdshader", false, false, true, "x")).IsEqual("trees batch");
        AssertThat(Engine_MdlLodCensus.Category(lite + "standard_material_wind_lite.gdshader", false, false, false, "x")).IsEqual("plants");
        AssertThat(Engine_MdlLodCensus.Category(lite + "standard_material_lite.gdshader", false, true, true, "x")).IsEqual("plants batch");
        AssertThat(Engine_MdlLodCensus.Category("res://src/Engine/level/terrain/lite/terrain_splat_lite.gdshader", false, false, false, "x")).IsEqual("terrain");
        // Skinned wins over the shader: a character's surfaces are the standard material too.
        AssertThat(Engine_MdlLodCensus.Category(lite + "standard_material_lite.gdshader", true, false, false, "GameEntities")).IsEqual("characters");
        AssertThat(Engine_MdlLodCensus.Category("res://x/fire.gdshader", false, false, false, "FableFx")).IsEqual("fx");
        AssertThat(Engine_MdlLodCensus.Category("", false, false, false, "x")).IsEqual("other");
        AssertThat(Engine_MdlLodCensus.Category("res://src/Engine/level/water/water.gdshader", false, false, false, "x")).IsEqual("water");
    }
}
