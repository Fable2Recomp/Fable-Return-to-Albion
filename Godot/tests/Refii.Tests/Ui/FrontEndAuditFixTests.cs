namespace Refii.Tests.Ui;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Refii.Bnk;
using Refii.Core;
using Refii.Titles.Fable2;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// The front end after the UI cut: the guest's dialog reads the texts its writer TOLD
/// (`sub_826E40E0`), a headless presentation made again at the same address starts its told
/// state clean (a new serial), and the title's dialog is drawn once, by the host. No Godot type
/// is built.
/// </summary>
[TestSuite]
public sealed class FrontEndAuditFixTests
{
	private static uint H(string name) => Engine_GameHash.Sdbm(name);

	private static UiNode Node(List<UiNode> all, string name, UiNode? parent)
	{
		var node = new UiNode
		{
			Ordinal = all.Count,
			Name = name,
			Parent = parent,
			Element = new BgfElement { NameHash = H(name), ParentOrdinal = parent?.Ordinal ?? -1 },
		};
		parent?.Children.Add(node);
		all.Add(node);
		return node;
	}

	/// <summary>
	/// Component_0(0) → { Group(1) → OK(2) ; Layer(3) → { dialog(4) → Group(5) → { yousure(6),
	/// OK(7), cancel(8) } } }: a `Group.OK` outside the dialog that a name search would also find.
	/// </summary>
	private static Engine_UiScreen Screen()
	{
		var all = new List<UiNode>();
		UiNode root = Node(all, "Component_0", null);
		Node(all, "OK", Node(all, "Group", root));
		UiNode group = Node(all, "Group", Node(all, "dialog", Node(all, "Layer", root)));
		Node(all, "yousure", group);
		Node(all, "OK", group);
		Node(all, "cancel", group);
		return new Engine_UiScreen { Name = "frontendmainmenu", Nodes = all.ToArray(), Roots = new[] { root } };
	}

	/// <summary>`GuiElement_Resolve_p` with no start element: `Layer` under the root, then each segment a direct child.</summary>
	[TestCase]
	public void DialogPathsResolveUnderLayer()
	{
		Engine_UiScreen screen = Screen();
		AssertThat(Engine_UiFrontEndScreens.ElementAt(screen, "dialog.Group.yousure")?.Ordinal).IsEqual(6);
		AssertThat(Engine_UiFrontEndScreens.ElementAt(screen, "dialog.Group.OK")?.Ordinal).IsEqual(7);
		AssertThat(Engine_UiFrontEndScreens.ElementAt(screen, "dialog.Group.cancel")?.Ordinal).IsEqual(8);
		AssertThat(Engine_UiFrontEndScreens.ElementAt(screen, "Group.OK")).IsNull();
		AssertThat(Engine_UiFrontEndScreens.ElementAt(screen, "dialog.Group.icon")).IsNull();
	}

	/// <summary>The dialog shows what the writer told after the raise began; an earlier raise's text is not it.</summary>
	[TestCase]
	public void DialogTextIsTheToldTextSinceTheRaise()
	{
		const uint presentation = 0x4D0A1000u;
		Engine_UiScreen screen = Screen();
		uint root = H("Component_0");

		Engine_UiSlideStatements.ApplyText(presentation, root, 6u, "Old question");
		ulong since = Engine_UiSlideStatements.Sequence;
		AssertThat(Engine_UiFrontEndScreens.DialogText(screen, presentation, "yousure", since)).IsEqual(string.Empty);

		Engine_UiSlideStatements.ApplyText(presentation, root, 7u, "Accept");
		Engine_UiSlideStatements.ApplyText(presentation, root, 6u, "No saves were found.");
		Engine_UiSlideStatements.ApplyText(presentation, root, 8u, string.Empty);
		AssertThat(Engine_UiFrontEndScreens.DialogText(screen, presentation, "yousure", since)).IsEqual("No saves were found.");
		AssertThat(Engine_UiFrontEndScreens.DialogText(screen, presentation, "OK", since)).IsEqual("Accept");
		AssertThat(Engine_UiFrontEndScreens.DialogText(screen, presentation, "cancel", since)).IsEqual(string.Empty);

		// Another presentation's statements are not this one's.
		AssertThat(Engine_UiFrontEndScreens.DialogText(screen, presentation + 0x100u, "OK", 0)).IsEqual(string.Empty);
	}

	private const uint Rebuilt = 0x4D0B2000u;

	/// <summary>A one-element headless file, `Root` → `Text`, under whichever serial is asked.</summary>
	private static Engine_UiHeadlessElements AnySerial() => new(
		(presentation, serial) => presentation == Rebuilt ? "Data\\Art\\GUI\\Gameface\\FrontEndMainMenu.bgf" : null,
		(uint presentation, uint serial, uint id, out uint parent, out uint hash) =>
		{
			parent = 0;
			hash = id == 1 ? H("Text") : 0u;
			return presentation == Rebuilt && id == 1;
		},
		_ => new Engine_BgfDocument
		{
			Elements = new[]
			{
				new BgfElement { NameHash = H("Root"), ParentOrdinal = -1, Flags = 4 },
				new BgfElement { NameHash = H("Text"), ParentOrdinal = 0 },
			},
		},
		_ => { });

	/// <summary>
	/// Quit to Title makes the front end's presentations again, possibly at the same addresses:
	/// a statement under a new serial starts the presentation clean, so the old slides, texts and
	/// stamps are never read as the new one's.
	/// </summary>
	[TestCase]
	public void ANewSerialStartsThePresentationClean()
	{
		Engine_UiSlideStatements.Headless = AnySerial();
		uint word = Engine_UiHeadlessElements.HeadlessOrdinal | 1u;
		Engine_UiSlideStatements.ApplyText(Rebuilt, 11u, word, "First life");
		Engine_UiSlideStatements.Apply(5u, Rebuilt, 11u, word, 0xABCDu, 0u);
		AssertThat(Engine_UiSlideStatements.TextOf(Rebuilt, H("Root"), 1, 0)).IsEqual("First life");

		// The same presentation keeps what it was told.
		Engine_UiSlideStatements.Apply(1u, Rebuilt, 11u, word, 2u, 0u);
		AssertThat(Engine_UiSlideStatements.TextOf(Rebuilt, H("Root"), 1, 0)).IsEqual("First life");

		Engine_UiSlideStatements.Apply(4u, Rebuilt, 12u, word, 1u, 0u);
		AssertThat(Engine_UiSlideStatements.TextOf(Rebuilt, H("Root"), 1, 0)).IsNull();
		AssertThat(Engine_UiSlideStatements.Of(Rebuilt, H("Root"), 0)).IsNull();
		var textures = new List<(int, uint)>(Engine_UiSlideStatements.TexturesSince(Rebuilt, H("Root"), 0));
		AssertThat(textures.Count).IsEqual(0);
		AssertThat(Engine_UiSlideStatements.ActiveOf(Rebuilt, H("Root"), 1, 0) == true).IsTrue();
	}

	/// <summary>A built presentation's statements carry no serial: they neither start a presentation clean nor are a new one.</summary>
	[TestCase]
	public void BuiltStatementsKeepTheHeldState()
	{
		const uint built = 0x4D0C3000u;
		Engine_UiSlideStatements.ApplyText(built, 0x77u, 3u, "Kept");
		Engine_UiSlideStatements.Apply(4u, built, 0x77u, 3u, 1u, 0u);
		AssertThat(Engine_UiSlideStatements.TextOf(built, 0x77u, 3, 0)).IsEqual("Kept");
	}

	/// <summary>The title's dialog is the host's warning box alone, as the main menu's is.</summary>
	[TestCase]
	public void TheTitleDialogIsOwnedElsewhere()
	{
		Engine_UiTemplateSet authored = Fable2UiTemplates.AuthoredDefaults();
		Engine_UiScreenTemplate title = authored.Screens[1011];
		AssertThat(title.OwnedElsewhere.Contains("dialog")).IsTrue();
		AssertThat(title.ChromeUnits.Contains("dialog")).IsFalse();
		AssertThat(authored.Screens[1010].OwnedElsewhere.Contains("dialog")).IsTrue();
	}

	private static Engine_UiScreen? Shipped(string file)
	{
		string? bank = null;
		for (string? at = AppContext.BaseDirectory; at is not null && bank is null; at = Path.GetDirectoryName(at))
		{
			string direct = Path.Combine(at, "assets", "Fable2", "game", "data", "guiscripts.bnk");
			bank = File.Exists(direct) ? direct : null;
		}

		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			Console.WriteLine($"[front-end] no packaged install beside the tree; {file} is not read here");
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (entry.RelativePath.EndsWith(file + ".bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse(file, archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	/// <summary>The writer's three paths name the shipped files' dialog texts.</summary>
	[TestCase]
	public void TheShippedDialogTextsResolve()
	{
		if (Shipped("frontendmainmenu") is { } menu)
		{
			AssertThat(Engine_UiFrontEndScreens.ElementAt(menu, "dialog.Group.yousure")?.Ordinal).IsEqual(3682);
			AssertThat(Engine_UiFrontEndScreens.ElementAt(menu, "dialog.Group.OK")?.Ordinal).IsEqual(3683);
			AssertThat(Engine_UiFrontEndScreens.ElementAt(menu, "dialog.Group.cancel")?.Ordinal).IsEqual(3690);
		}

		if (Shipped("frontendstartupscreen") is { } title)
		{
			AssertThat(Engine_UiFrontEndScreens.ElementAt(title, "dialog.Group.yousure")?.Ordinal).IsEqual(68);
			AssertThat(Engine_UiFrontEndScreens.ElementAt(title, "dialog.Group.OK")?.Ordinal).IsEqual(69);
			AssertThat(Engine_UiFrontEndScreens.ElementAt(title, "dialog.Group.cancel")?.Ordinal).IsEqual(76);
		}
	}
}
