namespace Refii.Tests.Ui;

using System.Collections.Generic;
using System.Reflection;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The guest's UI Lua is cut (the runner `sub_82426470` records and never runs a chunk), so no
/// lever that worked through the guest's Lua menu may remain on the UI source, and a commit on a
/// screen that reads the highlighted row states the row before the screen's A. Pure model code —
/// no Godot type is built.
/// </summary>
[TestSuite]
public class GuestUiLuaCutTests
{
	/// <summary>Records what a commit sends, in order.</summary>
	private sealed class RecordingSource : Engine_UiSource
	{
		public readonly List<string> Calls = new();

		public override uint ScreenAction(uint code)
		{
			Calls.Add($"ScreenAction:{code}");
			return 1;
		}

		public override void StateHighlightedRow(uint screen, int eventType, int id, string name, string label) =>
			Calls.Add($"Highlighted:{screen}:{eventType}:{id}:{name}:{label}");
	}

	[TestCase]
	public void HighlightCommitStatesTheRowThenPressesA()
	{
		var source = new RecordingSource();
		var row = new Engine_UiExpandableMenu.Item("Brightness", "OPTIONS_BRIGHTNESS")
		{
			EventType = 72,
			IdLow = 3,
			IdHigh = 1,
		};

		Engine_UiScreenMenu.StateAndPress(source, 18u, row);

		AssertThat(source.Calls.Count).IsEqual(2);
		AssertThat(source.Calls[0]).IsEqual($"Highlighted:18:72:{(1 << 16) + 3}:Brightness:OPTIONS_BRIGHTNESS");
		AssertThat(source.Calls[1]).IsEqual($"ScreenAction:{Engine_UiScreenInput.Select}");
	}

	[TestCase]
	public void HighlightCommitPrefersTheStatedLabel()
	{
		var source = new RecordingSource();
		var row = new Engine_UiExpandableMenu.Item("Sofa", "FURN_SOFA") { StatedLabel = "Sofa (2)", IdLow = 0 };

		Engine_UiScreenMenu.StateAndPress(source, 26u, row);

		AssertThat(source.Calls[0]).IsEqual("Highlighted:26:0:0:Sofa:Sofa (2)");
	}

	[TestCase]
	public void NoLuaMenuLeverRemainsOnTheUiSource()
	{
		foreach (string lever in new[] { "MenuSelect", "MenuHighlight", "MenuScroll" })
		{
			AssertThat(typeof(Engine_UiSource).GetMethod(lever, BindingFlags.Public | BindingFlags.Instance)).IsNull();
		}
	}

	[TestCase]
	public void TheScreenListHasNoGuestCursor()
	{
		const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		foreach (string member in new[] { "Track", "Solve", "HostExpanded", "GuestHighlight" })
		{
			AssertThat(typeof(Engine_UiScreenMenu).GetMember(member, any).Length).IsEqual(0);
		}
	}
}
