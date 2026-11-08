namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// THE BOX SCREENS' OWN ART. Each box-manager screen is drawn in its own .bgf's frame — the black
/// box, its glow, the top and bottom lines and the description band (`BlackBox`, `TopLine`,
/// `BottomLine`, `Desription` under a `Small1` / `Medium1` / `Large1` … node) — on the shared HUD
/// view, behind the list <see cref="Fable3StatedScreen"/> draws its rows on. Only the frame is cut
/// (<see cref="Fable3HudDocument"/> groups-only): the texts, rows and prompts are the template's.
/// </summary>
/// <remarks>
/// <para>WHICH FILE. The box's type (vtable slot 7) picks the presentation through sub_8291F4C8 and
/// the file through sub_82B3EC40's table (<see cref="Fable3BoxScreenModel"/>'s class list); the
/// stems are the documents <see cref="Fable3Elements"/> keys their statements by.</para>
/// <para>WHEN IT SHOWS. A frame stands in the scene root's slide 1 and its screen root's slide — the
/// file's own gating (each frame is an entry of both). Both are stated here while the screen is up,
/// from what the screen's own calls stated (<see cref="RootSlide"/>), so the frame follows the page:
/// the divorce box's Large1 on its asking and told pages, Medium1 on the confirmed one; the
/// treasury's Large4 with the balances, Medium1 for the transfer.</para>
/// </remarks>
public static class Fable3ScreenFrames
{
	/// <summary>
	/// One screen's frame: its document, the element root its rows are written under (the
	/// <see cref="Fable3ScreenBoxes.ScreenRoot"/>), the component whose slides gate the frame, the
	/// frame nodes' parent and the frame nodes.
	/// </summary>
	public readonly record struct Frame(string Document, string Root, string SlideOwner, string Parent, string[] Art);

	/// <summary>
	/// Every box screen's frame, read from its .bgf (the art-only nodes under the screen root that
	/// the root's slides switch on).
	/// </summary>
	public static readonly Frame[] Frames =
	{
		new("herostatus", Fable3ScreenModel.HeroStatus, "HeroStatus", "HeroStatus", new[] { "HeroStatus" }),
		new("statsscreen", Fable3ScreenModel.Stats, "StatsBox", "StatsBox", new[] { "Large2" }),
		new("promises", Fable3ScreenModel.Promises, "Promises", "Promises", new[] { "Background2", "Promises" }),
		new("questsummary", Fable3ScreenModel.Quests, "QuestSummary", "QuestSummary", new[] { "Large1" }),
		new("kingsschedule", Fable3ScreenModel.Schedule, "KingsSchedule", "KingsSchedule", new[] { "KingsAgenda" }),
		new("joinoptions", Fable3BoxScreenModel.JoinOptions, "OrbEmote", "OrbEmote", new[] { "Medium1" }),
		new("visability", Fable3BoxScreenModel.Visibility, "Visibilities", "Visibilities", new[] { "Medium1" }),
		new("friendschannel", Fable3BoxScreenModel.FriendsChannel, "FriendsChannel", "FriendsChannel", new[] { "Medium1" }),
		new("chatchannel", Fable3BoxScreenModel.ChatChannel, "ChatChannel", "ChatChannel", new[] { "Small1" }),
		new("treasureaccounts", Fable3BoxScreenModel.Treasury, "TreasureAccounts", "TreasureAccounts", new[] { "Large4", "Medium1" }),
		new("bedmenubox", Fable3BoxScreenModel.BedMenu, "BedMenu", "BedMenu", new[] { "Medium2" }),
		new("foodquerybox", Fable3BoxScreenModel.FoodQuery, "InfoBox", "InfoBox", new[] { "Medium1" }),
		new("compweddingpicker", Fable3BoxScreenModel.WeddingPicker, "WeddingPicker", "WeddingPicker", new[] { "Medium1" }),
		new("cooppartnersscreen", Fable3BoxScreenModel.Partners, "CoopPartnersInfoBox", "CoopPartnersInfoBox", new[] { "Large1" }),
		new("proposalquerybox", Fable3BoxScreenModel.Proposal, "InfoBox", "MarriageProposal", new[] { "Large1", "Medium1" }),
		new("divorcemessagebox", Fable3BoxScreenModel.Divorce, "DivorceMessageBox", "DivorceMessageBox", new[] { "Large1", "Medium1" }),
	};

	/// <summary>The frame of the screen whose element root is <paramref name="root"/>, or null (the save menu, an unknown root).</summary>
	public static Frame? FrameOf(string? root)
	{
		foreach (Frame frame in Frames)
		{
			if (frame.Root == root)
			{
				return frame;
			}
		}

		return null;
	}

	/// <summary>One frame document per box screen, for <see cref="Fable3Hud"/> to stand beside the HUD.</summary>
	public static IEnumerable<Fable3HudDocument> Documents()
	{
		foreach (Frame frame in Frames)
		{
			var groups = new Engine_HudLayout.GroupSubtree[frame.Art.Length];
			for (int i = 0; i < frame.Art.Length; ++i)
			{
				groups[i] = new Engine_HudLayout.GroupSubtree(frame.Art[i], "Frame_" + frame.Document,
					Engine_HudLayout.Anchor.Centre, 0f, false, frame.Parent, AnyNode: true);
			}

			yield return new Fable3HudDocument(frame.Document, groups, layouts: false, groupsOnly: true);
		}
	}

	/// <summary>
	/// The screen root's slide that its page stands on, from the screen's own statements: the stats
	/// page (`@Screen.StatsPage`, the Show's 2 at 0x8296D54C; 2 categories, 3 statistics, 4 friends),
	/// the treasury page `+28` (2 both balances, 3 personal only, 4 transfer, 5 query; the Show and the
	/// Update set it at 0x82B5A134 / 0x82B5A388), the divorce page `+172` (2 asking, 3 told, 4
	/// confirmed: sub_82928C90 / sub_82928F78 / sub_82929378 at 0x82928CB4 / 0x82928F9C /
	/// 0x8292939C), the bed row `+44` (slides Sleep 1 / Sex 2), the join / visibility choice `+52`
	/// and the friends row `+36` (one slide per row), the food and proposal queries' Show (2); every
	/// other screen has the one slide.
	/// </summary>
	public static int RootSlide(string root, Fable3ScreenState screen) => root switch
	{
		Fable3ScreenModel.Stats => screen.StatsPage ?? (screen.Field(0, 2) is >= 2 and <= 5 ? screen.Field(0, 2) : 2),
		Fable3BoxScreenModel.Treasury => screen.Field(0, 3) is >= 2 and <= 5 ? screen.Field(0, 3) : 3,
		Fable3BoxScreenModel.Divorce => Fable3BoxScreenModel.DivorcePageOf(screen.Field(1, 0)) switch
		{
			Fable3BoxScreenModel.DivorceKind.Told => 3,
			Fable3BoxScreenModel.DivorceKind.Confirmed => 4,
			_ => 2,
		},
		Fable3BoxScreenModel.BedMenu => screen.Field(0, 0) == 1 ? 2 : 1,
		Fable3BoxScreenModel.JoinOptions or Fable3BoxScreenModel.Visibility => System.Math.Clamp(screen.Field(1, 1), 1, 3),
		Fable3BoxScreenModel.FriendsChannel => System.Math.Clamp(screen.Field(0, 1), 1, 4),
		Fable3BoxScreenModel.FoodQuery or Fable3BoxScreenModel.Proposal => 2,
		_ => 1,
	};

	private static string? _statedDocument;
	private static int _statedSlide;
	private static ulong _stamp;

	/// <summary>The scene's and the screen root's slides for <paramref name="document"/> while its screen is up.</summary>
	public static IEnumerable<Fable3HudStatements.Statement> StatementsFor(string document)
	{
		if (FrameOf(Fable3ScreenBoxes.ScreenRoot) is not { } frame || frame.Document != document)
		{
			yield break;
		}

		int slide = RootSlide(frame.Root, Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox));
		if (_statedDocument != document || _statedSlide != slide)
		{
			// Entered once per page: a slide is re-entered only by a newer stamp.
			_statedDocument = document;
			_statedSlide = slide;
			_stamp = Fable3Elements.Generation + 1;
		}

		yield return new Fable3HudStatements.Statement(Fable3HudStatements.RootPath, 1, null, null, null, _stamp);
		yield return new Fable3HudStatements.Statement(frame.SlideOwner, slide, null, null, null, _stamp);
	}

	/// <summary>Shows a frame document's view only while its screen is up (<see cref="Engine_HudView.Suppressed"/>).</summary>
	public static void Gate(Fable3HudDocument document)
	{
		if (document.View is not { } view)
		{
			return;
		}

		bool framed = false;
		foreach (Frame frame in Frames)
		{
			framed |= frame.Document == document.Document;
		}

		if (!framed)
		{
			return;
		}

		bool shown = FrameOf(Fable3ScreenBoxes.ScreenRoot) is { } up && up.Document == document.Document;
		if (view.Suppressed == shown)
		{
			view.Suppressed = !shown;
		}

		if (!shown && _statedDocument == document.Document)
		{
			_statedDocument = null;
		}
	}
}
