using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_discard_draw_priority` / `android_terrain_draw_priority` (AndroidOpaqueOrder).</summary>
[TestSuite]
public sealed class AndroidOpaqueOrderTests
{
    /// <summary>Unset is 0 (Godot's own order); values are read as written and clamped to the limit.</summary>
    [TestCase]
    public void ReadsAndClampsTheOffsets()
    {
        AssertThat(AndroidOpaqueOrder.Wanted(new Dictionary<string, string>())).IsEqual((0, 0));
        AssertThat(AndroidOpaqueOrder.Wanted(new Dictionary<string, string>
        {
            [AndroidOpaqueOrder.DiscardKey] = "-1",
            [AndroidOpaqueOrder.TerrainKey] = "2",
        })).IsEqual((-1, 2));
        AssertThat(AndroidOpaqueOrder.Wanted(new Dictionary<string, string>
        {
            [AndroidOpaqueOrder.DiscardKey] = "127",
            [AndroidOpaqueOrder.TerrainKey] = "-128",
        })).IsEqual((AndroidOpaqueOrder.Limit, -AndroidOpaqueOrder.Limit));
    }

    /// <summary>The shaders that compile a discard move; the culled (FABLE_SOLID) and blended translucent ones never do.</summary>
    [TestCase]
    public void OnlyDiscardingOpaqueShadersMove()
    {
        AssertThat(AndroidOpaqueOrder.ModelPriority(Engine_ShaderFamily.StandardPath, 1)).IsEqual(1);
        AssertThat(AndroidOpaqueOrder.ModelPriority(Engine_ShaderFamily.StandardTreePath, -1)).IsEqual(-1);
        AssertThat(AndroidOpaqueOrder.ModelPriority(Engine_ShaderFamily.StandardWindPath, 3)).IsEqual(3);
        AssertThat(AndroidOpaqueOrder.ModelPriority(Engine_ShaderFamily.StandardCulledPath, 1)).IsNull();
        AssertThat(AndroidOpaqueOrder.ModelPriority(Engine_ShaderFamily.StandardTranslucentPath, 1)).IsNull();
        AssertThat(AndroidOpaqueOrder.ModelPriority(Engine_ShaderFamily.StandardPath, 0)).IsNull();
    }

    /// <summary>Every lite build maps back to its full shader's class, so a lite material moves as its full one would.</summary>
    [TestCase]
    public void LiteBuildsKeepTheirFullShadersClass()
    {
        foreach (string lite in Engine_ShaderVariant.AllVariantPaths.Where(path => !Engine_ShaderVariant.IsTerrainSplat(path)))
        {
            string full = Engine_ShaderVariant.FullPathOf(lite);
            AssertThat(full).IsNotEqual(lite);
            AssertThat(AndroidOpaqueOrder.Discards(full)).IsEqual(full != Engine_ShaderFamily.StandardCulledPath &&
                full != Engine_ShaderFamily.StandardTranslucentPath);
        }
    }

    /// <summary>Launcher rows, 0 by default, with phone names.</summary>
    [TestCase]
    public void RegisteredAtZeroWithPhoneNames()
    {
        foreach (string key in new[] { AndroidOpaqueOrder.DiscardKey, AndroidOpaqueOrder.TerrainKey })
        {
            RuntimeCVarDefinition row = RuntimeCVarSet.All.First(d => d.Name == key);
            AssertThat(row.DefaultValue).IsEqual("0");
            bool offered = GraphicsPresets.Offered;
            try
            {
                GraphicsPresets.Offered = true;
                AssertThat(AndroidSettingNames.Name(key)).IsNotNull();
            }
            finally { GraphicsPresets.Offered = offered; }
        }
    }

    /// <summary>The front-to-back switch: off by default, named for a phone, and nothing on an engine without the patch.</summary>
    [TestCase, RequireGodotRuntime]
    public void FrontToBackIsOffByDefaultAndLeavesAnUnpatchedEngineAlone()
    {
        RuntimeCVarDefinition row = RuntimeCVarSet.All.First(d => d.Name == AndroidOpaqueOrder.FrontToBackKey);
        AssertThat(row.DefaultValue).IsEqual("false");
        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            AssertThat(AndroidSettingNames.Name(AndroidOpaqueOrder.FrontToBackKey)).IsNotNull();
        }
        finally { GraphicsPresets.Offered = offered; }

        // The desktop's official engine has no such setting: asking for the order must not create it.
        bool known = Godot.ProjectSettings.HasSetting(AndroidOpaqueOrder.FrontToBackSetting);
        AndroidOpaqueOrder.ApplyFrontToBack(true);
        AssertThat(Godot.ProjectSettings.HasSetting(AndroidOpaqueOrder.FrontToBackSetting)).IsEqual(known);
        AndroidOpaqueOrder.ApplyFrontToBack(false);
    }
}
