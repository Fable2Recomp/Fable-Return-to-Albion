namespace Refii.Tests.Ui;

using System;
using System.Collections.Generic;
using GdUnit4;
using Refii.Core;
using Refii.Titles.Fable2;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// The front end after the UI cut: its load cards and dialog are taken as their entries stated
/// them (`sub_826E1408`, `sub_826E40E0`), a headless presentation made again at the same address
/// starts its told state clean (a new serial), and the title's dialog is drawn once, by the host.
/// No Godot type is built.
/// </summary>
[TestSuite]
public sealed class FrontEndAuditFixTests
{
	private static uint H(string name) => Engine_GameHash.Sdbm(name);

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

	private static Engine_UiModel.GuiNativeCall Call(string name, params string[] arguments)
	{
		var list = new List<Engine_UiModel.GuiNativeArgument>();
		foreach (string argument in arguments)
		{
			list.Add(new Engine_UiModel.GuiNativeArgument(Engine_UiModel.GuiNativeArgumentKind.String, argument));
		}

		return new Engine_UiModel.GuiNativeCall(name, list);
	}

	/// <summary>StampSaveCard's statements: each save on the card the guest stamped, a later opening's only.</summary>
	[TestCase]
	public void SaveCardsAreTakenAsStated()
	{
		var stated = new Engine_FrontEndStated();
		AssertThat(stated.CardsSince(0, 2) is null).IsTrue();

		stated.SaveCard(Call("@FrontEnd.SaveCard", "0", "3", "1A2B3C4D"));
		AssertThat(stated.CardsSince(0, 2) is null).IsTrue();
		stated.SaveCard(Call("@FrontEnd.SaveCard", "1", "4", "0000BEEF"));
		List<Engine_FrontEndStated.Card>? cards = stated.CardsSince(0, 2);
		AssertThat(cards is { Count: 2 }).IsTrue();
		AssertThat(cards![0].Slot).IsEqual(3);
		AssertThat(cards[0].Texture).IsEqual(0x1A2B3C4Du);
		AssertThat(cards[1].Slot).IsEqual(4);
		AssertThat(cards[1].Texture).IsEqual(0xBEEFu);

		ulong opened = stated.Sequence;
		AssertThat(stated.CardsSince(opened, 2) is null).IsTrue();
		stated.SaveCard(Call("@FrontEnd.SaveCard", "0", "1", "00000001"));
		AssertThat(stated.CardsSince(opened, 1)?[0].Slot == 1).IsTrue();
	}

	/// <summary>OpenDialog's statement: owner, texts, allow-cancel, cancel tag and glyph as called.</summary>
	[TestCase]
	public void TheDialogIsTakenAsStated()
	{
		var stated = new Engine_FrontEndStated();
		stated.OpenDialog(Call("@FrontEnd.Dialog", "4D0A1000", "Accept", "No saves were found.", "1",
			"GUI_BUTTONS_CANCEL", "00C0FFEE"));
		AssertThat(stated.LastDialog is { } dialog && dialog.Owner == 0x4D0A1000u && dialog.Ok == "Accept" &&
			dialog.Body == "No saves were found." && dialog.AllowCancel && dialog.CancelTag == "GUI_BUTTONS_CANCEL" &&
			dialog.Glyph == 0xC0FFEEu).IsTrue();

		ulong first = stated.LastDialog!.Value.Sequence;
		stated.OpenDialog(Call("@FrontEnd.Dialog", "4D0A1000", "GUI_OK", "Body", "0", "", "00000002"));
		AssertThat(stated.LastDialog is { AllowCancel: false, CancelTag: "" } again && again.Sequence > first).IsTrue();
	}
}
