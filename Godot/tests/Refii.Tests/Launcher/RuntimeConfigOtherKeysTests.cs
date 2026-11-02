namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

/// <summary>
/// KEYS WITH NO DEFINITION are kept under "Other", unless TOML can't hold them beside the rest: a
/// dotted key nested under a key that holds a value makes the whole file unreadable to the game.
/// </summary>
[TestSuite]
public class RuntimeConfigOtherKeysTests
{
	[TestCase]
	public void ADottedKeyUnderAValueIsDroppedAndTheRestKept()
	{
		string file = Path.Combine(Path.GetTempPath(), "refii_other_keys_" + Guid.NewGuid().ToString("N") + ".toml");
		try
		{
			var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
			{
				["log_level"] = "debug",
				["log_level.extra"] = "",
				["some_unknown_key"] = "7",
				["some_unknown_key.nested.deeper"] = "x",
				["unknown_group.leaf"] = "kept",
			};
			AssertThat(RuntimeConfig.WriteValues(file, values, out string error)).OverrideFailureMessage("write failed: " + error).IsTrue();

			string text = File.ReadAllText(file);
			AssertThat(text.Contains("log_level.extra", StringComparison.Ordinal)).IsFalse();
			AssertThat(text.Contains("some_unknown_key.nested", StringComparison.Ordinal)).IsFalse();

			SortedDictionary<string, string> written = RuntimeConfig.ReadValues(file);
			AssertThat(written.GetValueOrDefault("log_level")).IsEqual("debug");
			AssertThat(written.GetValueOrDefault("some_unknown_key")).IsEqual("7");
			AssertThat(written.GetValueOrDefault("unknown_group.leaf")).IsEqual("kept");
		}
		finally
		{
			File.Delete(file);
		}
	}
}
