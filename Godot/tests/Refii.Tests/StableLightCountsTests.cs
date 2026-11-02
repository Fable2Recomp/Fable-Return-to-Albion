using System;
using System.Collections.Generic;
using System.Text.Json;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`stable_light_counts`: the key, the presets and the debug lever.</summary>
[TestSuite]
public sealed class StableLightCountsTests
{
	/// <summary>Reads leave state alone and invalid values are rejected.</summary>
	[TestCase]
	public void TheDebugLeverValidates()
	{
		using var empty = JsonDocument.Parse("{}");
		(bool? enabled, bool? sentinel) = Engine_StableLightCounts.ReadRequest(empty.RootElement);
		AssertThat(enabled is null && sentinel is null).IsTrue();

		using var on = JsonDocument.Parse("{\"stableLightCounts\":true,\"stableLightSentinel\":false}");
		(enabled, sentinel) = Engine_StableLightCounts.ReadRequest(on.RootElement);
		AssertThat(enabled).IsEqual(true);
		AssertThat(sentinel).IsEqual(false);

		using var invalid = JsonDocument.Parse("{\"stableLightCounts\":1}");
		bool rejected = false;
		try { Engine_StableLightCounts.ReadRequest(invalid.RootElement); } catch (ArgumentException) { rejected = true; }
		AssertThat(rejected).IsTrue();
	}

	/// <summary>Off by default; on in both presets.</summary>
	[TestCase]
	public void OffUnlessConfigured()
	{
		bool before = Engine_StableLightCounts.Enabled;
		try
		{
			Engine_StableLightCounts.Configure(new Dictionary<string, string>());
			AssertThat(Engine_StableLightCounts.Enabled).IsFalse();
			Engine_StableLightCounts.Configure(new Dictionary<string, string> { [Engine_StableLightCounts.Key] = "true" });
			AssertThat(Engine_StableLightCounts.Enabled).IsTrue();
		}
		finally
		{
			Engine_StableLightCounts.Enabled = before;
		}

		AssertThat(GraphicsPresets.BalancedValues[Engine_StableLightCounts.Key]).IsEqual("true");
		AssertThat(GraphicsPresets.UltraPerformanceValues[Engine_StableLightCounts.Key]).IsEqual("true");
	}
}
