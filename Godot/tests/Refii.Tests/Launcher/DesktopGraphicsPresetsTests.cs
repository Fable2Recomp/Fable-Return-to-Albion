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
			AssertThat(read["omni_shadow_mode"]).IsEqual("dual_paraboloid");
		}
		finally
		{
			Directory.Delete(dir, true);
		}
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
