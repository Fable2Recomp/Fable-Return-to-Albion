namespace Refii.Tests.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Core;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

/// <summary>
/// ONE CONFIG FILE PER TITLE: Fable 2 keeps <c>refii.toml</c>, Fable 3 has <c>refiii.toml</c>, both
/// at the app root; the active title's is the one every reader resolves, and a title's file holds
/// only the settings that title reads.
/// </summary>
[TestSuite]
public class TitleConfigTests
{
	[TestCase]
	public void EachTitleHasItsOwnConfigFile()
	{
		AssertThat(Engine_Title.Fable2.ConfigFileName).IsEqual("refii.toml");
		AssertThat(Engine_Title.Fable3.ConfigFileName).IsEqual("refiii.toml");

		var names = Engine_Title.All.Select(title => title.ConfigFileName).ToList();
		AssertThat(names.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqual(names.Count);
	}

	[TestCase]
	public void EveryTitlesConfigIsAtTheAppRoot()
	{
		foreach (Engine_Title title in Engine_Title.All)
		{
			AssertThat(Path.GetDirectoryName(title.ConfigPath)).IsEqual(Engine_AppRoot.Path);
			AssertThat(Path.GetFileName(title.ConfigPath)).IsEqual(title.ConfigFileName);
		}
	}

	[TestCase]
	public void TheAppRootsConfigIsTheActiveTitles()
	{
		Engine_Title before = Engine_Title.Active;
		try
		{
			foreach (Engine_Title title in Engine_Title.All)
			{
				Engine_Title.Active = title;
				AssertThat(Engine_AppRoot.ConfigPath).IsEqual(title.ConfigPath);
				AssertThat(Installer.GetRuntimeConfigPath()).IsEqual(title.ConfigPath);
			}
		}
		finally
		{
			Engine_Title.Active = before;
		}
	}

	[TestCase]
	public void ATitlesFileHoldsOnlyItsOwnSettings()
	{
		Engine_Title before = Engine_Title.Active;
		string file = Path.Combine(Refii.Tests.TestTemp.Root, "refii_title_config_" + Guid.NewGuid().ToString("N") + ".toml");
		try
		{
			Engine_Title.Active = Engine_Title.Fable3;

			// A Fable 2-only key in the values must not reach Fable 3's file; a key with no
			// definition at all is still preserved.
			var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
			{
				["vfs_trace_vfs"] = "true",
				["pubgame_vector_shapes"] = "false",
				["log_level"] = "debug",
				["some_unknown_key"] = "7",
			};
			AssertThat(RuntimeConfig.WriteValues(file, values, out string error)).OverrideFailureMessage("write failed: " + error).IsTrue();

			SortedDictionary<string, string> written = RuntimeConfig.ReadValues(file);
			AssertThat(written.ContainsKey("vfs_trace_vfs")).IsFalse();
			AssertThat(written.ContainsKey("pubgame_vector_shapes")).IsFalse();
			AssertThat(written.ContainsKey("developer_menu_enabled")).IsFalse();
			AssertThat(written.GetValueOrDefault("log_level")).IsEqual("debug");
			AssertThat(written.GetValueOrDefault("some_unknown_key")).IsEqual("7");

			foreach (string key in written.Keys)
			{
				RuntimeCVarDefinition? definition = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == key);
				if (definition is not null)
				{
					AssertThat(definition.AppliesTo(Engine_Title.Fable3))
						.OverrideFailureMessage(key + " is not Fable 3's but was written to its file").IsTrue();
				}
			}

			// Fable 2's file is written exactly as before: every Fable 2 setting it shows.
			Engine_Title.Active = Engine_Title.Fable2;
			AssertThat(RuntimeConfig.WriteValues(file, values, out error)).OverrideFailureMessage("write failed: " + error).IsTrue();
			written = RuntimeConfig.ReadValues(file);
			AssertThat(written.GetValueOrDefault("vfs_trace_vfs")).IsEqual("true");
			AssertThat(written.GetValueOrDefault("pubgame_vector_shapes")).IsEqual("false");
		}
		finally
		{
			Engine_Title.Active = before;
			File.Delete(file);
		}
	}
}
