namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Globalization;
using Probe = Fable3ScreenModel.Probe;

/// <summary>
/// The pages of the box-manager screens past the five <see cref="Fable3ScreenModel"/> composes —
/// join and visibility options, the friends and chat channels, the treasury statement, the bed
/// menu, the food query, the wedding picker, co-op partners, the marriage proposal, the divorce
/// message — and the save menu, all from the element values the guest STATES through the hooked
/// setters. Pure, so the tests run it against a stated set.
/// </summary>
/// <remarks>
/// <para>THE CLASSES (Fable 3 IDB, disc build; slot 1 Show, 4 input with the EGameAction in `*ev`,
/// 7 the box type that picks the presentation through sub_8291F4C8 and the file through
/// sub_82B3EC40's table): join/visibility 0x820C35F8 (sub_82976A08 / sub_82976BC8, type = +48:
/// 18 JoinOptions.bgf, 19 Visability.bgf), friends channel 0x820C3640 (sub_82977A48 /
/// sub_82977B78, 21), chat channel 0x820C2F58 (sub_82972408 / sub_829727C8, 22), treasury
/// 0x820D84A4 (sub_82B59FA8 / sub_82B5A410, 17 TreasureAccounts.bgf), bed 0x820D7508
/// (sub_82B58F98 / sub_82B59348, 14), food 0x820D72F4 (sub_82B579E8 / sub_82B58048, 9), wedding
/// 0x820D85A4 (sub_82B5BE98 / sub_82B5C260, 23), partners 0x820D8754 (sub_82B5C868 /
/// sub_82B5CA70, 24), proposal 0x820B4640 (sub_8281CCC8 / sub_8281D238, 10), divorce 0x820C0088
/// (sub_829284D8 / sub_82928958, 11). Paths are the classes' static CCharStrings.</para>
/// <para>The save menu is not a box: it is the object at `*(SM+56)` (ctor sub_8296C4D0), opened by
/// sub_8296C5D0 on presentation slot 11 (SaveMenu.bgf), and its root state is the statement —
/// 1..4 the cursor row, 5 closed (sub_8296C858 at 0x8296C88C).</para>
/// <para>Labels the guest never writes are its own tags where it has them (the join/visibility
/// choices are hashed from TEXT_GUI_JOIN_OPTIONS_* / TEXT_GUI_ORB_VISIBILITY_* by the initialisers
/// at 0x831DE340 / 0x831DE500), else the screen's .bgf textstring.</para>
/// </remarks>
public static class Fable3BoxScreenModel
{
	public const string JoinOptions = "OrbEmote";
	public const string Visibility = "Visibilities";
	public const string FriendsChannel = "FriendsChannel";
	public const string ChatChannel = "ChatChannel";
	public const string Treasury = "TreasureAccounts";
	public const string BedMenu = "BedMenu";
	public const string FoodQuery = "InfoBox";
	public const string WeddingPicker = "WeddingPicker";
	public const string Partners = "CoopPartnersInfoBox";
	public const string Proposal = "InfoBox.MarriageProposal";
	public const string Divorce = "DivorceMessageBox";
	public const string SaveMenu = "SaveMenu";

	private const string Gap = "    ";

	/// <summary>The element root each display_box_hooks.cpp `screen:*` kind writes under, or null.</summary>
	public static string? RootOf(string kind) => kind switch
	{
		"screen:join" => JoinOptions,
		"screen:visibility" => Visibility,
		"screen:friends" => FriendsChannel,
		"screen:chat" => ChatChannel,
		"screen:treasury" => Treasury,
		"screen:bed" => BedMenu,
		"screen:food" => FoodQuery,
		"screen:wedding" => WeddingPicker,
		"screen:partners" => Partners,
		"screen:proposal" => Proposal,
		"screen:divorce" => Divorce,
		_ => null,
	};

	/// <summary>The page of the screen whose element root is <paramref name="root"/>; null for a root this model does not draw.</summary>
	public static Fable3ScreenPage? Compose(string root, IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve) => root switch
	{
		JoinOptions => ChoicePage(stated, resolve, JoinOptions, "TEXT_GUI_JOIN_OPTIONS_TITLE", ".Emotes", ".ThisEmote",
			new[] { ("Any1", "TEXT_GUI_JOIN_OPTIONS_ANYONE"), ("Invite", "TEXT_GUI_JOIN_OPTIONS_INVITE"), ("Off", "TEXT_GUI_JOIN_OPTIONS_OFF") }),
		Visibility => ChoicePage(stated, resolve, Visibility, "TEXT_GUI_ORB_VISIBILITY_TITLE", ".Visibilities", ".ThisEmote",
			new[] { ("Everyone", "TEXT_GUI_ORB_VISIBILITY_EVERYONE"), ("Friends", "TEXT_GUI_ORB_VISIBILITY_FRIENDS"), ("None", "TEXT_GUI_ORB_VISIBILITY_NONE") }),
		FriendsChannel => FriendsPage(stated, resolve),
		ChatChannel => ChatPage(stated, resolve),
		Treasury => TreasuryPage(stated, resolve),
		BedMenu => BedPage(stated, resolve),
		FoodQuery => FoodPage(stated, resolve),
		WeddingPicker => WeddingPage(stated, resolve),
		Partners => PartnersPage(stated, resolve),
		Proposal => ProposalPage(stated, resolve),
		Divorce => DivorcePage(stated, resolve),
		SaveMenu => SaveMenuPage(stated, resolve),
		_ => null,
	};

	/// <summary>
	/// Whether the save menu is up: its root's state is the cursor row 1..4 while open — the open
	/// sub_8296C5D0 writes 1 or 3 (0x8296C74C), the row writer sub_8296CB08 restates it (0x8296CB74) —
	/// and 5 once sub_8296C858 closes it (0x8296C88C).
	/// </summary>
	public static bool SaveMenuOpen(IReadOnlyDictionary<string, Probe> stated) =>
		At(stated, SaveMenu) is { State: >= 1 and <= 4 };

	/// <summary>
	/// JOIN OPTIONS and VISIBILITY: one class, three choices. The cursor is the root's state 1..3
	/// (sub_829770A8 at 0x82977364, sub_82976E58 at 0x82976F70 and its twin sub_82976F80 write `+52` to
	/// `OrbEmote` or `Visibilities`); the Show sub_82976A08 writes the SAVED setting as `<root>.Emotes` /
	/// `.Visibilities` state (0x82976AD0) and its title tag into `<root>.ThisEmote` (0x82976B60). Input sub_82976BC8: the
	/// stick (26/27, 0x82976D60 / 0x82976D68) moves, A (74, 0x82976D54) commits through
	/// sub_82977370 and closes, B (75, 0x82976E04) closes.
	/// </summary>
	private static Fable3ScreenPage ChoicePage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve,
		string root, string titleTag, string savedPath, string currentPath, (string Name, string Tag)[] choices)
	{
		int cursor = At(stated, root)?.State ?? 1;
		int saved = At(stated, root + savedPath)?.State ?? 0;
		var rows = new List<Fable3ScreenPage.Row>(choices.Length);
		for (int i = 0; i < choices.Length; ++i)
		{
			string label = resolve(choices[i].Tag + "_TITLE");
			rows.Add(new(choices[i].Name, saved == i + 1 ? label + Gap + "✓" : label, false, choices[i].Tag + "_DESC"));
		}

		string current = Text(stated, root + currentPath) ?? string.Empty;
		return new Fable3ScreenPage(resolve(titleTag), rows, Clamp(cursor - 1, rows.Count), current,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// FRIENDS CHANNEL. The root's state is the cursor 1..3 (the Show writes 1 at 0x82977ADC; the
	/// input restates it at 0x82977E2C); 4 parks it. Input sub_82977B78: the stick (26/27,
	/// 0x82977D10 / 0x82977D18) moves 1..3, A (74, 0x82977D04) runs the row (sub_82365190 /
	/// sub_82365220 / sub_82365328) and closes, B (75, 0x82977F04) closes. Labels: friendschannel.bgf.
	/// </summary>
	private static Fable3ScreenPage FriendsPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		int cursor = At(stated, FriendsChannel)?.State ?? 1;
		var rows = new List<Fable3ScreenPage.Row>
		{
			new("Friends", "Friends"),
			new("Join", "Join a Party"),
			new("Invite", "Invite into Party"),
		};

		return new Fable3ScreenPage("Friends Channel", rows, cursor is >= 1 and <= 3 ? cursor - 1 : -1, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// CHAT CHANNEL. sub_82972AB0 writes the working mute byte `+36` as `ChatChannel.Emotes` and
	/// `ChatChannel.OnOff` state 2 muted / 1 audible (0x82972AF4 / 0x82972B08), the SAVED setting's
	/// tag into `ChatChannel.CurrSetting` (0x82972B60) and the working one's description into
	/// `ChatChannel.SelectionDescription.Mute_Description` (0x82972B90). Input sub_829727C8: left/right (31/32 via
	/// sub_82972BA0) toggle `+36`, A (72, 0x8297294C) saves it and closes, B (73, 0x82972A58) closes.
	/// </summary>
	private static Fable3ScreenPage ChatPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		bool muted = At(stated, ChatChannel + ".OnOff")?.State == 2;
		string value = resolve(muted ? "TEXT_GUI_AUDIO_CHAT_MUTED" : "TEXT_GUI_AUDIO_CHAT_AUDIBLE");
		var rows = new List<Fable3ScreenPage.Row> { new("Mute", "Mute" + Gap + "◀ " + value + " ▶") };
		string description = string.Join("\n", Present(
			Text(stated, ChatChannel + ".CurrSetting") is { } current ? "Currently set to: " + current : null,
			Text(stated, ChatChannel + ".SelectionDescription.Mute_Description")));
		return new Fable3ScreenPage("Chat Channel", rows, 0, description,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// THE TREASURY STATEMENT. The root's state is the page `+28` (the Show at 0x82B5A134, the
	/// Update sub_82B5A328 at 0x82B5A388): 2 with the transfer widget, 3 without, 4/5 the confirmation query.
	/// sub_82B5AEA8 writes the balances (`Personal.Personal Balance Value`, `Treasury.Treasury Balance
	/// Value`, the `Treasury Balance` label) and the transfer row's tags; sub_82B5B410 /
	/// sub_82B5B628 the direction's title, transfer label and X caption; sub_82B5A6C8 the query
	/// text (`QueryBox.Group.QueryBox.Text` at 0x82B5AAE0, root state 5 at 0x82B5AACC). Input sub_82B5A410: A (74,
	/// 0x82B5A5A0) steps the transfer, B (75) backs out of the query or closes, X (76) flips the
	/// direction, left/right reach the amount widget sub_82B56830.
	/// </summary>
	private static Fable3ScreenPage TreasuryPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		int page = At(stated, Treasury)?.State ?? 3;
		var rows = new List<Fable3ScreenPage.Row>();
		if (Text(stated, Treasury + ".Personal.Personal Balance Value") is { } personal)
		{
			rows.Add(new("Personal", "Personal Balance" + Gap + personal));
		}

		if (Text(stated, Treasury + ".Treasury.Treasury Balance Value") is { } treasury)
		{
			rows.Add(new("Treasury", (Text(stated, Treasury + ".Treasury.Treasury Balance") ?? "Treasury Balance") + Gap + treasury));
		}

		if (page == 2 && Text(stated, Treasury + ".MoneyTransfer.TransferFundsComponent.Text.Text") is { } transfer)
		{
			rows.Add(new("Transfer", transfer + Gap + "◀ " + (Text(stated, Treasury + ".MoneyTransfer.TransferFundsComponent.Value.Value") ?? "0") + " ▶"));
		}

		foreach ((int index, string path, Probe row) in Fable3ScreenModel.Indexed(stated, Treasury + ".Transactions.Transaction Name.Transaction "))
		{
			if (row.Hidden || row.Text is not { Length: > 0 } name)
			{
				continue;
			}

			string value = Text(stated, Treasury + ".Transactions.Transaction Value.Transaction " + index.ToString(CultureInfo.InvariantCulture)) ?? string.Empty;
			rows.Add(new("Transaction" + index.ToString(CultureInfo.InvariantCulture), name + Gap + value));
		}

		var prompts = new List<Fable3ScreenPage.Prompt> { new("A", resolve("GUI_ACCEPT")), new("B", resolve("GUI_BACK")) };
		if (page == 2 && Text(stated, Treasury + ".Buttons.X_Button.Group.Text") is { } flip)
		{
			prompts.Add(new("X", flip));
		}

		Fable3ScreenPage.QueryBox? query = page is 4 or 5 && Text(stated, Treasury + ".QueryBox.Group.QueryBox.Text") is { } question
			? new Fable3ScreenPage.QueryBox(string.Empty, question,
				new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) })
			: null;
		return new Fable3ScreenPage(Text(stated, Treasury + ".Title") ?? resolve("TEXT_GUI_MONEY_SCREEN_TITLE"), rows,
			page == 2 ? rows.FindIndex(row => row.Name == "Transfer") : -1,
			page == 2 ? Text(stated, Treasury + ".MoneyTransfer.DescriptionText") ?? string.Empty : string.Empty, prompts, page == 2, query);
	}

	/// <summary>
	/// THE BED MENU. sub_82B591E8 states the row `+44`: `BedMenu.Sleep` / `BedMenu.Sex` state 2 for
	/// the cursor row, 1 for the other (0x82B5922C..0x82B59278); the Show writes the row titles by
	/// tag (TEXT_GUI_BED_MENU_SLEEP, TEXT_GUI_BED_MENU_SEX[_GROUP]) and hides the Sex row when it is
	/// not offered (`+56`, sub_82B59BB8); sub_82B590C8 writes each row's option and description by
	/// tag (`.Widget.Text`, `.Description`). Input sub_82B59348: up/down (29/30, the stick through
	/// sub_82B3F6F0) move, left/right (31/32) cycle the option, 41 accepts, 42 cancels.
	/// </summary>
	private static Fable3ScreenPage BedPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		foreach (string name in new[] { "Sleep", "Sex" })
		{
			string path = BedMenu + "." + name;
			if (At(stated, path) is not { } row || row.Hidden || Text(stated, path + ".Title") is not { } title)
			{
				continue;
			}

			if (row.State == 2)
			{
				selected = rows.Count;
			}

			string option = Text(stated, path + ".Widget.Text") is { } widget ? Gap + "◀ " + widget + " ▶" : string.Empty;
			rows.Add(new(name, title + option, false, Text(stated, path + ".Description") ?? string.Empty));
		}

		return new Fable3ScreenPage("Bed Activities", rows, selected, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// THE FOOD QUERY. The Show sub_82B579E8 writes the item eaten now and the one offered
	/// (`InfoBox.Current.*` / `InfoBox.Replacement.*`: Item_Name, Item_Health, Item_Fatness) and the
	/// root state. Input sub_82B58048: 41 (0x82B581D8) takes the replacement and 42 (0x82B581E0)
	/// declines, each posting its reply (0x82B5843C / 0x82B58E44). Labels: foodquerybox.bgf.
	/// </summary>
	private static Fable3ScreenPage FoodPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		foreach ((string group, string label) in new[] { ("Current", "Current:"), ("Replacement", "Replacement:") })
		{
			string path = FoodQuery + "." + group;
			if (Text(stated, path + ".Item_Name") is not { } item)
			{
				continue;
			}

			string line = label + " " + item;
			if (Text(stated, path + ".Item_Health") is { } health)
			{
				line += Gap + "Health: " + health;
			}

			if (Text(stated, path + ".Item_Fatness") is { } fatness)
			{
				line += Gap + "Fatness: " + fatness;
			}

			rows.Add(new(group, line));
		}

		return new Fable3ScreenPage(string.Empty, rows, -1, Text(stated, FoodQuery + ".Text") ?? string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_YES")), new Fable3ScreenPage.Prompt("B", resolve("GUI_NO")) }, false);
	}

	/// <summary>
	/// THE WEDDING PICKER. The Show sub_82B5BE98 writes the level's name (`WeddingPicker.Title`) and
	/// three tiers `WeddingPicker.Details.WeddingDetails%d.Group.{Title,Details,Cost}` (titles and
	/// descriptions by tag name, sub_82819230; the price as text); sub_82B5C158 states the tier
	/// `+68` as `WeddingPicker.Details` state tier+1 and each row 2 selected / 1 not (sub_82B5C1F0).
	/// Input sub_82B5C260: up/down move the tier, 41 books an affordable one (event 69 with the
	/// tier, 0x82B5C498), 42 backs out (event 70, 0x82B5C524).
	/// </summary>
	private static Fable3ScreenPage WeddingPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		for (int i = 0; i < 3; ++i)
		{
			string path = WeddingPicker + ".Details.WeddingDetails" + i.ToString(CultureInfo.InvariantCulture);
			if (Text(stated, path + ".Group.Title") is not { } title)
			{
				continue;
			}

			if (At(stated, path)?.State == 2)
			{
				selected = rows.Count;
			}

			string cost = Text(stated, path + ".Group.Cost") is { } price ? Gap + price : string.Empty;
			rows.Add(new("Tier" + i.ToString(CultureInfo.InvariantCulture), title + cost, false, Text(stated, path + ".Group.Details") ?? string.Empty));
		}

		return new Fable3ScreenPage(Text(stated, WeddingPicker + ".Title") ?? resolve("TEXT_GUI_SELECT_WEDDING_LOCATION"), rows, selected, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// CO-OP PARTNERS. sub_82B5D6A8 writes `CoopPartnersInfoBox.NumOnline` (0x82B5D7E4), the cursor `+56` as
	/// `CoopPartnersInfoBox.Details` state cursor+1 (0x82B5D850) and eight rows
	/// `CoopPartnersInfoBox.Details.PartnerDetails%d`: hidden past the list, else state 2 for the
	/// cursor row / 1 otherwise, +2 for a partnership that is not a marriage (0x82B5D92C), shown at 0x82B5D93C;
	/// each row's `.GamerTag`, `.NumBuildings`, `.NumChildren` texts and `.Online` / `.Offline`
	/// toggles (sub_82B5D9E0); the cursor row's `Details.LocationFormedOn.Location` / `.Date`.
	/// Input sub_82B5CA70: the stick (26/27) moves, A (74) / X (76) / Y (77) act on the row (their
	/// buttons shown per row, sub_82B5C868), B (75) closes.
	/// </summary>
	private static Fable3ScreenPage PartnersPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		foreach ((int index, string path, Probe row) in Fable3ScreenModel.Indexed(stated, Partners + ".Details.PartnerDetails"))
		{
			if (row.Hidden || Text(stated, path + ".GamerTag") is not { } gamer)
			{
				continue;
			}

			if (row.State is 2 or 4)
			{
				selected = rows.Count;
			}

			string line = gamer;
			if (Text(stated, path + ".NumBuildings") is { } buildings)
			{
				line += Gap + buildings;
			}

			if (row.State is 1 or 2 && Text(stated, path + ".NumChildren") is { } children)
			{
				line += Gap + children;
			}

			line += Gap + (At(stated, path + ".Online") is { Hidden: false } ? "Online" : "Offline");
			rows.Add(new("Partner" + index.ToString(CultureInfo.InvariantCulture), line));
		}

		string description = string.Join("\n", Present(
			Text(stated, Partners + ".Details.LocationFormedOn.Location") is { } location ? "Location: " + location : null,
			Text(stated, Partners + ".Details.LocationFormedOn.Date") is { } date ? "Formed on: " + date : null));
		var prompts = new List<Fable3ScreenPage.Prompt>();
		if (At(stated, Partners + ".Buttons.Buttons.A_Button") is not { Hidden: true })
		{
			prompts.Add(new("A", resolve("GUI_SELECT")));
		}

		prompts.Add(new("B", resolve("GUI_BACK")));
		string title = "Partnerships" + (Text(stated, Partners + ".NumOnline") is { } online ? Gap + "Online: " + online : string.Empty);
		return new Fable3ScreenPage(title, rows, selected, description, prompts, true);
	}

	/// <summary>
	/// THE MARRIAGE PROPOSAL. The Show sub_8281CCC8 writes the proposer line
	/// `InfoBox.MarriageProposal.GamerTag.From` (0x8281CD60) and six figures
	/// `….GamerInfo.{Gold,TimesMarried,TimesDivorced,TimesWidowed,NumberOfChildren,CrimesCommitted}`
	/// from `+40..+60` (0x8281CD80..0x8281CE48), the X caption by tag (0x8281CEB0) and `InfoBox` state 2
	/// (0x8281CEC4). Input
	/// sub_8281D238: 41 accepts, 42 refuses (0x8281D3D4 / 0x8281D3DC), X (76, 0x8281D3C8) shows the
	/// proposer's card. Labels: proposalquerybox.bgf (`….GamerInfo.*2`).
	/// </summary>
	private static Fable3ScreenPage ProposalPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		foreach ((string name, string label) in new[]
		{
			("Gold", "Current Gold"), ("TimesMarried", "Times Married"), ("TimesDivorced", "Times Divorced"),
			("TimesWidowed", "Times Widowed"), ("NumberOfChildren", "Number of Children"), ("CrimesCommitted", "Number of Crimes Committed"),
		})
		{
			if (Text(stated, Proposal + ".GamerInfo." + name) is { } value)
			{
				rows.Add(new(name, label + Gap + value));
			}
		}

		var prompts = new List<Fable3ScreenPage.Prompt> { new("A", resolve("GUI_YES")), new("B", resolve("GUI_NO")) };
		if (Text(stated, Proposal + ".Buttons.X_Button.small.Text") is { } card)
		{
			prompts.Add(new("X", card));
		}

		return new Fable3ScreenPage("Marriage Proposal", rows, -1, Text(stated, Proposal + ".GamerTag.From") ?? string.Empty, prompts, false);
	}

	/// <summary>
	/// THE DIVORCE MESSAGE. Its pages (sub_82928C90 / sub_82928F78 / sub_82929378) state the root
	/// and `Foreground.GamerTagPics` / `Foreground.JointAssetsBreakdown` states and commit the title
	/// and action line (sub_82928BB0: `Foreground.Title`, `Foreground.ActionDescription`), the two
	/// names (`GamerTagPics.Pic1.Tag` / `.Pic2.Tag`), the reason (`ReasonsPicker.Reasons.ReasonText`,
	/// sub_829297D8; `ReasonReport.*`), the asset split (`JointAssetsBreakdown.Tag/Amount1`,
	/// `.Tag2/Amount2`) and the children line (`Foreground.Kids`). Input sub_82928958: A (74,
	/// 0x829289A8) advances, B (75, 0x829289F4) backs out, left/right (31/32) pick the reason.
	/// </summary>
	private static Fable3ScreenPage DivorcePage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		string fore = Divorce + ".Foreground.";
		var rows = new List<Fable3ScreenPage.Row>();
		void Line(string name, params string?[] parts)
		{
			var present = new List<string>(Present(parts));
			if (present.Count > 0)
			{
				rows.Add(new(name, string.Join(Gap, present)));
			}
		}

		if (At(stated, fore + "GamerTagPics") is not { Hidden: true })
		{
			Line("Names", Text(stated, fore + "GamerTagPics.Pic1.Tag"), Text(stated, fore + "GamerTagPics.Pic2.Tag"));
		}

		Line("Reason", Text(stated, fore + "ReasonReport.ReasonReportTitle"), Text(stated, fore + "ReasonsPicker.Reasons.ReasonText") ?? Text(stated, fore + "ReasonReport.ReasonText"));
		if (At(stated, fore + "JointAssetsBreakdown") is not { Hidden: true })
		{
			Line("Assets1", Text(stated, fore + "JointAssetsBreakdown.Tag"), Text(stated, fore + "JointAssetsBreakdown.Amount1"));
			Line("Assets2", Text(stated, fore + "JointAssetsBreakdown.Tag2"), Text(stated, fore + "JointAssetsBreakdown.Amount2"));
		}

		Line("Kids", Text(stated, fore + "Kids"));
		return new Fable3ScreenPage(Text(stated, fore + "Title") ?? string.Empty, rows, -1, Text(stated, fore + "ActionDescription") ?? string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, false);
	}

	/// <summary>
	/// THE SAVE MENU. The open sub_8296C5D0 writes `SaveMenu.Foreground.Text.Save` and
	/// `.SaveAndQuit` state 1 when saving is allowed, 2 when not (0x8296C76C..0x8296C790);
	/// sub_8296CB08 states the cursor `+12` on the root and each row's `.Layout` 2 highlighted / 1
	/// not (0x8296CBA0..0x8296CC24) — 1 Save, 2 SaveAndQuit, 4 CharacterManagement, else Quit. Input sub_8296C918: up/down
	/// (29/30 and the stick) move, 72 runs the row (sub_8296CDB0), 73 closes (sub_8296C858).
	/// The rows' captions are SaveMenu.bgf's, not extracted here: their element names stand in.
	/// </summary>
	private static Fable3ScreenPage SaveMenuPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		string text = SaveMenu + ".Foreground.Text.";
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		foreach ((string name, string label) in new[] { ("Save", "Save"), ("SaveAndQuit", "Save and Quit"), ("CharacterManagement", "Character Management"), ("Quit", "Quit") })
		{
			if (At(stated, text + name + ".Layout")?.State == 2)
			{
				selected = rows.Count;
			}

			rows.Add(new(name, label, At(stated, text + name)?.State == 2));
		}

		return new Fable3ScreenPage(string.Empty, rows, selected, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_SELECT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	private static int Clamp(int index, int count) => index >= 0 && index < count ? index : -1;

	private static Probe? At(IReadOnlyDictionary<string, Probe> stated, string path) =>
		stated.TryGetValue(path, out Probe probe) ? probe : null;

	private static string? Text(IReadOnlyDictionary<string, Probe> stated, string path) =>
		stated.TryGetValue(path, out Probe probe) && probe.Text is { Length: > 0 } text ? text : null;

	private static IEnumerable<string> Present(params string?[] texts)
	{
		foreach (string? text in texts)
		{
			if (text is { Length: > 0 })
			{
				yield return text;
			}
		}
	}
}
