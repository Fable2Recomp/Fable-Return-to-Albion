using System;
using System.Collections.Generic;
using System.Text.Json;
using GdUnit4;
using Refii.Core;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The allocation removals answer exactly what the code they replaced answered, and the GC frame
/// ring folds and serialises.
/// </summary>
[TestSuite]
public sealed class GcAllocationTests
{
	/// <summary>The character-wise suffix test agrees with lower-casing a slash-normalised copy.</summary>
	[TestCase]
	public void ArchiveSuffixMatchesTheNormalisedCopy()
	{
		string[] paths =
		{
			@"Data\Levels\Bowerstone_Market_Models.bnk", "data/levels/globals_models.bnk",
			@"ART\MODEL_HEADERS\x_models.bnk", "short", string.Empty, @"Data\GLOBALS_MODELS.BNK",
			@"mods\Ünïcode\Some_Models.bnk",
		};
		string[] suffixes = { "_models.bnk", "globals_models.bnk", "levels/globals_models.bnk", "x", string.Empty, "short!" };
		foreach (string path in paths)
		{
			string normalised = path.Replace('\\', '/').ToLowerInvariant();
			foreach (string suffix in suffixes)
			{
				AssertThat(Refii.Level.Engine_PropModelIndex.EndsWithNormalised(path, suffix))
					.IsEqual(normalised.EndsWith(suffix, StringComparison.Ordinal));
			}
		}
	}

	/// <summary>The stack-encoded FNV-1 equals the hash of the encoded byte array, short and long.</summary>
	[TestCase]
	public void Fnv1OfAStringIsTheHashOfItsUtf8Bytes()
	{
		string[] values = { string.Empty, "GUI_BACK", "PlayerHealthMax", "Ünïcode ✓ 𝄞", new string('x', 85), new string('é', 86), new string('y', 400) };
		foreach (string value in values)
		{
			AssertThat(Engine_GameHash.Fnv1(value)).IsEqual(Engine_GameHash.Fnv1(System.Text.Encoding.UTF8.GetBytes(value).AsSpan()));
		}
	}

	/// <summary>The list refresh keeps, adds and drops devices as the enumerable one did, and rejects the same input.</summary>
	[TestCase]
	public void JoypadRefreshFromAListMatchesTheEnumerable()
	{
		var byList = new GameJoypadButtonCapture();
		var byEnumerable = new GameJoypadButtonCapture();
		int[][] steps = { new[] { 0, 1 }, new[] { 0, 1 }, new[] { 1, 3 }, Array.Empty<int>(), new[] { 2 } };
		foreach (int[] step in steps)
		{
			byList.RefreshDevices(new List<int>(step));
			byEnumerable.RefreshDevices((IEnumerable<int>)step);
			AssertThat(byList.DeviceCount).IsEqual(byEnumerable.DeviceCount);
			AssertThat(byList.ContextGeneration).IsEqual(byEnumerable.ContextGeneration);
		}

		AssertThrown(() => byList.RefreshDevices(new List<int> { 4, 4 })).IsInstanceOf<ArgumentException>();
		AssertThrown(() => byList.RefreshDevices(new List<int> { -1 })).IsInstanceOf<ArgumentException>();
	}

	/// <summary>The frame ring returns a window's rows and folds them into a serialisable summary.</summary>
	[TestCase]
	public void GcFramesFoldAWindow()
	{
		long start = Engine_GcFrames.LatestFrame;
		long frame = Math.Max(start, 0) + 1_000_000;
		for (int i = 0; i < 6; ++i)
		{
			_ = new byte[4096];
			Engine_GcFrames.FrameStart(frame + i, 50.0);
		}

		List<Engine_GcFrames.Row> rows = Engine_GcFrames.RowsSince(frame);
		AssertThat(rows.Count).IsGreaterEqual(4);
		Dictionary<string, object?> summary = Engine_GcFrames.Summarise(rows);
		AssertThat((int)summary["frames"]!).IsEqual(rows.Count);
		AssertThat((double)summary["mainKBPerFrame"]!).IsGreaterEqual(4.0);
		JsonSerializer.Serialize(summary);
		JsonSerializer.Serialize(Engine_GcFrames.Workers());
		JsonSerializer.Serialize(Engine_GcFrames.Configuration());
		AssertThat(Engine_GcFrames.Summarise(new List<Engine_GcFrames.Row>()).Count).IsGreater(0);
	}
}
