using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests.Launcher;

/// <summary>
/// Verifies <see cref="DesktopGraphicsPresets"/> round-tripping, Custom transition, Look &amp; texture
/// upscaling independence, first-install <c>Balanced</c> default, and mobile isolation.
/// </summary>
[TestSuite]
public sealed class DesktopGraphicsPresetsTests
{
	[AfterTest]
	public void RestorePlatform() => GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid;

	[TestCase]
	public void AllFourNamedPresets_ApplyAndRoundTripCleanly()
	{
		GraphicsPresets.Offered = false;
		var values = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (string preset in DesktopGraphicsPresets.Order)
		{
			AssertThat(DesktopGraphicsPresets.Apply(preset, values)).IsTrue();
			AssertThat(DesktopGraphicsPresets.Current(values)).IsEqual(preset);
			AssertThat(values[DesktopGraphicsPresets.Key]).IsEqual(preset);
		}
	}

	[TestCase]
	public void ModifyingAnyMemberSetting_TransitionsToCustomAndRestoringReturnsPreset()
	{
		GraphicsPresets.Offered = false;
		var model = new RuntimeSettingsModel();
		AssertThat(model.ApplyDesktopPreset(DesktopGraphicsPresets.Balanced)).IsTrue();
		AssertThat(model.CurrentDesktopPreset()).IsEqual(DesktopGraphicsPresets.Balanced);
		AssertThat(model.CurrentLightingPreset()).IsEqual("balanced");

		// Flip SDFGI on -> switches to Custom
		model.Values["sdfgi_enabled"] = "true";
		AssertThat(model.RefreshDesktopPreset("sdfgi_enabled")).IsTrue();
		AssertThat(model.CurrentDesktopPreset()).IsEqual(DesktopGraphicsPresets.Custom);
		AssertThat(model.Values[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.Custom);

		// Restore SDFGI off -> returns to Balanced
		model.Values["sdfgi_enabled"] = "false";
		AssertThat(model.RefreshDesktopPreset("sdfgi_enabled")).IsTrue();
		AssertThat(model.CurrentDesktopPreset()).IsEqual(DesktopGraphicsPresets.Balanced);
	}

	[TestCase]
	public void ApplyingDesktopPresets_PreservesLookAndTextureUpscalingChoice()
	{
		GraphicsPresets.Offered = false;
		var model = new RuntimeSettingsModel();
		model.Values["lighting_model"] = "modern";
		model.Values["materials_modernized"] = "true";
		model.Values["modern_tone_curve"] = "all";
		model.Values["texture_upscaling_enabled"] = "false";
		model.Values["hdr_output"] = "true";

		foreach (string preset in DesktopGraphicsPresets.Order)
		{
			model.ApplyDesktopPreset(preset);
			AssertThat(model.CurrentLook()).IsEqual("modern_filmic");
			AssertThat(model.Values["texture_upscaling_enabled"]).IsEqual("false");
			AssertThat(model.Values["hdr_output"]).IsEqual("true");
		}
	}

	[TestCase]
	public void FirstInstallDefault_UsesBalancedOnAllDesktopSystems()
	{
		GraphicsPresets.Offered = false;
		AssertThat(DesktopGraphicsPresets.DefaultPreset).IsEqual(DesktopGraphicsPresets.Balanced);
		AssertThat(DesktopGraphicsPresets.SelectInitialDesktopPreset("Intel(R) Arc(TM) 140V GPU", isIntegratedGpu: true))
			.IsEqual(DesktopGraphicsPresets.Balanced);
		AssertThat(DesktopGraphicsPresets.SelectInitialDesktopPreset("NVIDIA GeForce RTX 4080", isIntegratedGpu: false))
			.IsEqual(DesktopGraphicsPresets.Balanced);

		string dir = Path.Combine(TestTemp.Root, "refii-desktop-preset-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			string configPath = Path.Combine(dir, "refii.toml");
			bool ok = RuntimeConfig.EnsureDefaults(configPath, out string error);
			AssertThat(error).IsEmpty();
			AssertThat(ok).IsTrue();

			SortedDictionary<string, string> read = RuntimeConfig.ReadValues(configPath);
			AssertThat(read[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.Balanced);
			AssertThat(DesktopGraphicsPresets.Current(read)).IsEqual(DesktopGraphicsPresets.Balanced);
			AssertThat(read["render_scaler"]).IsEqual("fsr");
			AssertThat(read["fsr_quality"]).IsEqual("ultra_quality");
			AssertThat(read["sdfgi_enabled"]).IsEqual("false");
			AssertThat(read["placed_shadow_limit"]).IsEqual("2");
			AssertThat(read.ContainsKey("omni_shadow_mode")).IsFalse();
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[TestCase]
	public void PresetsScaleCubeShadowLimits_AndUpgradePriorRevisionsCleanly()
	{
		GraphicsPresets.Offered = false;

		AssertThat(DesktopGraphicsPresets.Revision).IsEqual(3);
		AssertThat(DesktopGraphicsPresets.Values(DesktopGraphicsPresets.Low)!["placed_light_shadows"]).IsEqual("true");
		AssertThat(DesktopGraphicsPresets.Values(DesktopGraphicsPresets.Low)!["placed_shadow_limit"]).IsEqual("1");
		AssertThat(DesktopGraphicsPresets.Values(DesktopGraphicsPresets.Balanced)!["placed_shadow_limit"]).IsEqual("2");
		AssertThat(DesktopGraphicsPresets.Values(DesktopGraphicsPresets.High)!["placed_shadow_limit"]).IsEqual("4");
		AssertThat(DesktopGraphicsPresets.Values(DesktopGraphicsPresets.Ultra)!["placed_shadow_limit"]).IsEqual("8");

		foreach (string preset in DesktopGraphicsPresets.Order)
		{
			AssertThat(DesktopGraphicsPresets.Values(preset)!.ContainsKey("omni_shadow_mode")).IsFalse();
		}

		// Revision-1 Balanced install (carrying legacy omni_shadow_mode, missing placed_shadow_limit)
		// upgrades cleanly to Revision 3 Balanced with placed_shadow_limit = 2.
		var rev1Balanced = new Dictionary<string, string>(DesktopGraphicsPresets.Values(DesktopGraphicsPresets.Balanced)!, StringComparer.Ordinal)
		{
			[DesktopGraphicsPresets.Key] = DesktopGraphicsPresets.Balanced,
			[DesktopGraphicsPresets.RevisionKey] = "1",
			["omni_shadow_mode"] = "dual_paraboloid",
		};
		rev1Balanced.Remove("placed_shadow_limit");

		AssertThat(DesktopGraphicsPresets.Upgrade(rev1Balanced)).IsTrue();
		AssertThat(rev1Balanced[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.Balanced);
		AssertThat(rev1Balanced[DesktopGraphicsPresets.RevisionKey]).IsEqual("3");
		AssertThat(rev1Balanced["placed_shadow_limit"]).IsEqual("2");
		AssertThat(rev1Balanced.ContainsKey("omni_shadow_mode")).IsFalse();

		// Revision-2 Low install (previously placed_light_shadows = false) upgrades cleanly to
		// Revision 3 Low with cube shadows enabled (placed_shadow_limit = 1).
		var rev2Low = new Dictionary<string, string>(DesktopGraphicsPresets.Values(DesktopGraphicsPresets.Low)!, StringComparer.Ordinal)
		{
			[DesktopGraphicsPresets.Key] = DesktopGraphicsPresets.Low,
			[DesktopGraphicsPresets.RevisionKey] = "2",
			["placed_light_shadows"] = "false",
		};
		AssertThat(DesktopGraphicsPresets.Upgrade(rev2Low)).IsTrue();
		AssertThat(rev2Low[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.Low);
		AssertThat(rev2Low[DesktopGraphicsPresets.RevisionKey]).IsEqual("3");
		AssertThat(rev2Low["placed_light_shadows"]).IsEqual("true");
		AssertThat(rev2Low["placed_shadow_limit"]).IsEqual("1");

		// Custom configurations keep custom settings intact and strip legacy omni_shadow_mode on upgrade.
		var rev1Custom = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[DesktopGraphicsPresets.Key] = DesktopGraphicsPresets.Custom,
			[DesktopGraphicsPresets.RevisionKey] = "1",
			["omni_shadow_mode"] = "dual_paraboloid",
			["placed_shadow_limit"] = "6",
		};
		AssertThat(DesktopGraphicsPresets.Upgrade(rev1Custom)).IsTrue();
		AssertThat(rev1Custom[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.Custom);
		AssertThat(rev1Custom[DesktopGraphicsPresets.RevisionKey]).IsEqual("3");
		AssertThat(rev1Custom["placed_shadow_limit"]).IsEqual("6");
		AssertThat(rev1Custom.ContainsKey("omni_shadow_mode")).IsFalse();
	}

	[TestCase]
	public void MobilePlatform_NeitherShowsNorWritesDesktopPreset()
	{
		GraphicsPresets.Offered = true;
		AssertThat(DesktopGraphicsPresets.Offered).IsFalse();

		RuntimeCVarDefinition presetDef = RuntimeCVarSet.All.First(d => d.Name == DesktopGraphicsPresets.Key);
		RuntimeCVarDefinition revDef = RuntimeCVarSet.All.First(d => d.Name == DesktopGraphicsPresets.RevisionKey);
		AssertThat(RuntimeConfig.ShouldShowCVar(presetDef)).IsFalse();
		AssertThat(RuntimeConfig.ShouldShowCVar(revDef)).IsFalse();
		AssertThat(RuntimeConfig.ShouldWriteCVar(presetDef)).IsFalse();
		AssertThat(RuntimeConfig.ShouldWriteCVar(revDef)).IsFalse();
	}
}
