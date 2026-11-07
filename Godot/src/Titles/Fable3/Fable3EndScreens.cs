namespace Refii.Titles.Fable3;

using System.Globalization;
using Refii.Ui;

/// <summary>
/// Fable 3's epilogue (questcompletion.bgf) and credits (creditsscreen.bgf), drawn on the SHARED
/// HUD view as two more <see cref="Fable3HudDocument"/>s. Both are screen-manager screens, not
/// box-manager boxes, so no `@Box.Show` names them: end_screen_hooks.cpp states when each is up
/// and which presentation it is; everything on them is an element statement.
/// </summary>
/// <remarks>
/// THE EPILOGUE. The show (sub_8276B460) writes the title, description, reward caption, renown
/// and button texts through the HUD text registry, whose flush commits each to its text box
/// (sub_8223A888 -> sub_82B45268 at 0x8223A8DC: `@Element.Text`) — all behind its presentation
/// (0x8276B4BC), so the title and description are taken from the show's own arguments
/// (`@Epilogue.Open`, <see cref="Opened"/>); the reward rows
/// (AddItemRewardToEpilogueScreen -> sub_8276B978) toggle `EpilogueItem1/2` through SetState
/// (sub_8281A490 at 0x8281A4AC) and write their texts the same way; the gold
/// (SetGoldRewardInEpilogueScreen -> sub_8276B848) writes `EpilogueRenown` / `EpilogueRenownAmt`.
/// The image is not an element write — the show stores it into the `ScreenShot` texture
/// (0x8276B7F8) — so it arrives only as `@Epilogue.Open`'s second argument. INTERACT (13) closes
/// it in the guest (vt+20 sub_8276B410): Godot draws only.
///
/// THE CREDITS. The roll (sub_82B3F980, then its tick sub_82B3FD70) writes the `GUI_CREDITS` lines
/// into `Text.Text0..23` (sub_82B45268 at 0x82B3FC0C / 0x82B3FE64 / 0x82B3FEA4) and scrolls them
/// and `credits_logos` with SetPosition (0x82B3FC50, 0x82B3FED0, 0x82B3FCE8, 0x82B3FF1C), so the
/// view follows the stated positions. It ends itself, or on GUI_SELECT / GUI_CANCEL (72/73,
/// sub_82B3FF60) in the guest.
/// </remarks>
public static class Fable3EndScreens
{
	/// <summary>The epilogue's widgets: the quest card (`elements`) and its button row.</summary>
	public static readonly Engine_HudLayout.GroupSubtree[] EpilogueGroups =
	{
		new("elements", "Epilogue", null, 0f, true),
		new("Buttons", "EpilogueButtons", null, 0f, true),
	};

	/// <summary>The credits' widgets: the rolling lines, the logo sheet and the edge fades.</summary>
	public static readonly Engine_HudLayout.GroupSubtree[] CreditsGroups =
	{
		new("Text", "CreditsText", null, 0f, true),
		new("credits_logos", "CreditsLogos", null, 0f, true),
		new("Fade", "CreditsFade", null, 0.5f, true),
	};

	/// <summary>The epilogue's presentation while it is up, else 0.</summary>
	public static uint EpiloguePresentation { get; private set; }

	/// <summary>The image the epilogue was shown with (the `ScreenShot` texture's source), or empty.</summary>
	public static string EpilogueImage { get; private set; } = string.Empty;

	/// <summary>The credits' presentation while they roll, else 0.</summary>
	public static uint CreditsPresentation { get; private set; }

	/// <summary>The two documents, for <see cref="Fable3Hud"/> to stand beside `hud` and `interactionmode`.</summary>
	public static Fable3HudDocument[] Documents() => new[]
	{
		new Fable3HudDocument("questcompletion", EpilogueGroups, layouts: false),
		new Fable3HudDocument("creditsscreen", CreditsGroups, layouts: false),
	};

	/// <summary>
	/// Shows an end screen's view only while its screen is up (the HUD view keeps its last feed
	/// otherwise). Held through <see cref="Engine_HudView.Suppressed"/>: the view's walk restates
	/// its own visibility every guest frame, so a bare hide would not hold. Other documents are left alone.
	/// </summary>
	public static void Gate(Fable3HudDocument document)
	{
		if (document.View is not { } view)
		{
			return;
		}

		bool? up = document.Document switch
		{
			"questcompletion" => EpiloguePresentation != 0u,
			"creditsscreen" => CreditsPresentation != 0u,
			_ => null,
		};
		if (up is { } shown && view.Suppressed == shown)
		{
			view.Suppressed = !shown;
		}
	}

	public static void Register(Engine_UiGuiNatives router)
	{
		router.Register("@Epilogue.Open", call =>
		{
			EpiloguePresentation = Parse(call.Argument(0).Text);
			EpilogueImage = call.Argument(1).Text;
			Opened(call.Argument(2).Text, call.Argument(3).Text, (int)call.Argument(4).Number,
				Parse(call.Argument(5).Text), Parse(call.Argument(6).Text));
		});
		// The screenshot's texture as the show computed it (sub_826EFBD8 at 0x8276B788), for the
		// elements filed under `*dword_8355AC44` in the ImageName registry.
		router.Register("@Epilogue.Image", call =>
		{
			uint name = Parse(call.Argument(0).Text);
			uint texture = Parse(call.Argument(1).Text);
			Fable3HudRegistry.StateImage(name, texture);
			Fable3Elements.Log($"[fable3-stated] epilogue screenshot '{call.Argument(2).Text}' texture {texture:X8} under image name {name:X8}");
		});
		router.Register("@Epilogue.Close", _ =>
		{
			EpiloguePresentation = 0u;
			EpilogueImage = string.Empty;
		});
		router.Register("@Credits.Open", call => CreditsPresentation = Parse(call.Argument(0).Text));
		router.Register("@Credits.Close", _ => CreditsPresentation = 0u);
	}

	/// <summary>The renown the epilogue was shown with (Epilogue_Show's r6; 0 none).</summary>
	public static int EpilogueRenown { get; private set; }

	/// <summary>
	/// The show's own arguments (end_screen_hooks.cpp): the title and description land on the
	/// elements questcompletion.bgf binds under the text names the show writes them to
	/// (`*dword_8355AC24` / `*dword_8355AC28`, 0x8276B500 / 0x8276B524) — the writes the show makes
	/// only behind its presentation (0x8276B4BC).
	/// </summary>
	public static void Opened(string title, string description, int renown, uint titleName, uint descriptionName)
	{
		EpilogueRenown = renown;
		if (titleName != 0u)
		{
			Fable3HudRegistry.StateText(titleName, title);
		}

		if (descriptionName != 0u)
		{
			Fable3HudRegistry.StateText(descriptionName, description);
		}
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}
