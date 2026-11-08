namespace Refii.Tests.Ui;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Bnk;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 3's outlined text (<see cref="Engine_UiTextContract.Outlines"/>): which runs draw
/// PSHADER_GUI_OUTLINED_TEXT_3D — style 1, from the element's <c>.fac</c> record, never a
/// <c>&lt;b&gt;</c> run (style 2) — and that Fable 2 outlines nothing. No Godot runtime type is built.
/// </summary>
[TestSuite]
public class TextOutlineTests
{
	[TestCase]
	public void OnlyFable3OutlinesAndOnlyStyleOne()
	{
		Engine_UiTextContract three = Engine_UiTextContract.Fable3;
		AssertThat(three.Outlines).IsTrue();
		AssertThat(three.Outlined(1)).IsTrue();
		AssertThat(three.Outlined(0)).IsFalse();
		AssertThat(three.Outlined(2)).IsFalse();
		AssertThat(three.Outlined(3)).IsFalse();

		Engine_UiTextContract two = Engine_UiTextContract.Fable2;
		AssertThat(two.Outlines).IsFalse();
		AssertThat(two.Outlined(1)).IsFalse();
	}

	/// <summary>
	/// A world icon's title and opinion: Fable 3 draws them outlined (CWorldIcon3D vf16 packs both with
	/// the outline byte 1, <c>li r10, 1</c> 0x82B50FD8 / 0x82B511A0); Fable 2's stay at the label default,
	/// so its world-icon labels carry no style and draw as before.
	/// </summary>
	[TestCase]
	public void WorldIconLinesOutlineInFable3Only()
	{
		Engine_UiTextContract three = Engine_UiTextContract.Fable3;
		AssertThat(three.WorldIconTextStyle).IsEqual(1);
		AssertThat(three.Outlined(three.WorldIconTextStyle)).IsTrue();

		Engine_UiTextContract two = Engine_UiTextContract.Fable2;
		AssertThat(two.WorldIconTextStyle).IsEqual(new UiText().Style);
		AssertThat(two.Outlined(two.WorldIconTextStyle)).IsFalse();
	}

	/// <summary>The element keeps a style below 4, else 0 (0x82B45FD8..0x82B45FE8); the default is 3.</summary>
	[TestCase]
	public void TheElementClampsItsStyle()
	{
		AssertThat(Engine_UiTextContract.ElementStyle(1)).IsEqual(1);
		AssertThat(Engine_UiTextContract.ElementStyle(3)).IsEqual(3);
		AssertThat(Engine_UiTextContract.ElementStyle(4)).IsEqual(0);
		AssertThat(Engine_UiTextContract.ElementStyle(-1)).IsEqual(0);
		AssertThat(new UiText().Style).IsEqual(3);
	}

	/// <summary>`&lt;b&gt;` draws at style 2 (`li r8, 2` 0x821F85A0), every other run at its element's.</summary>
	[TestCase]
	public void ABoldRunIsNeverOutlined()
	{
		var runs = Engine_UiTextMarkup.Vocabulary.Fable3.Split("<b>Gold</b> coins <s1>here</s1>");
		AssertThat(runs.Count).IsEqual(3);
		AssertThat(runs[0].Text).IsEqual("Gold");
		AssertThat(runs[0].Code).IsEqual(1);
		AssertThat(runs[1].Code).IsEqual(0);
		AssertThat(runs[2].Code).IsEqual(2);

		Engine_UiTextContract three = Engine_UiTextContract.Fable3;
		AssertThat(three.Outlined(Engine_UiTextContract.RunStyle(1, runs[0].Code))).IsFalse();
		AssertThat(three.Outlined(Engine_UiTextContract.RunStyle(1, runs[1].Code))).IsTrue();
		AssertThat(three.Outlined(Engine_UiTextContract.RunStyle(1, runs[2].Code))).IsTrue();
		AssertThat(three.Outlined(Engine_UiTextContract.RunStyle(3, runs[1].Code))).IsFalse();
	}

	/// <summary>
	/// The shipped HUD's outlined elements: the world-icon titles and opinions, the subtitles and
	/// the TopBox button titles carry style 1 in hud.fac (35 records); the IM bar counters in
	/// interactionmode.fac.
	/// </summary>
	[TestCase]
	public void TheShippedHudOutlinesItsWorldTextAndPrompts()
	{
		if (Bank() is not { } archive)
		{
			Console.WriteLine("[fable3-outline] no Fable 3 bank beside the tree; the shipped styles are not checked here");
			return;
		}

		using (archive)
		{
			Engine_UiScreen hud = Screen(archive, "hud");
			string[] outlined = hud.Nodes.Where(n => n.Text is { Style: 1 }).Select(PathOf).ToArray();
			AssertThat(outlined.Length).IsEqual(35);
			AssertThat(outlined.Contains("Layer.Subtitles.Group.Text")).IsTrue();
			AssertThat(outlined.Contains("Layer.TopBoxRefined.TopBox.Buttons.Buttons.Button1.RightTitle")).IsTrue();
			AssertThat(outlined.Contains("Layer.WorldIcons.WorldIcon1.Contents.Title")).IsTrue();

			Engine_UiScreen im = Screen(archive, "interactionmode");
			AssertThat(im.Nodes.Where(n => n.Text is { Style: 1 }).Select(PathOf)
				.Contains("Layer.Bar.Elements.Text.Counter1.Counter.Text")).IsTrue();
		}
	}

	private static string PathOf(UiNode node)
	{
		var parts = new System.Collections.Generic.List<string>();
		for (UiNode? at = node; at is not null && at.Ordinal > 0; at = at.Parent)
		{
			parts.Insert(0, at.Name);
		}

		return string.Join(".", parts);
	}

	private static Engine_UiScreen Screen(BnkArchive archive, string name)
	{
		byte[]? Read(string file) =>
			archive.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.RelativePath.Replace('\\', '/')),
				file, StringComparison.OrdinalIgnoreCase)) is { } entry
				? archive.ReadEntryBytes(entry)
				: null;

		return Engine_UiScreen.Parse(name, Read(name + ".bgf")!, Read(name + ".fac"));
	}

	private static BnkArchive? Bank()
	{
		string data = Path.Combine("assets", "Fable3", "game", "data", "guiscripts.bnk");
		for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			string? found = File.Exists(Path.Combine(at, data)) ? Path.Combine(at, data) : null;
			string packages = Path.Combine(at, "out", "package");
			if (found is null && Directory.Exists(packages))
			{
				found = Directory.EnumerateDirectories(packages).Select(p => Path.Combine(p, data)).FirstOrDefault(File.Exists);
			}

			if (found is not null)
			{
				return BnkArchive.TryOpen(found, out BnkArchive? archive, out _) ? archive : null;
			}
		}

		return null;
	}
}
