namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// The art Fable 3 names for each world-icon type: the guest's own table sub_82B530E8, which
/// yields `art\&lt;path&gt;.tex` and hashes it into the icon's texture id (sub_82B512C0 at
/// 0x82B51304). The strings are the binary's, spelled as it spells them, so the hash matches;
/// <see cref="BankPath"/> turns one into the mount's lower-case entry.
///
/// `GUI_MemMount` IS A DIRECTORY, NOT A MOUNT ALIAS: the HUD's memory-mounted art lives in its own
/// pair of banks, `art\gui_memmount\gui_memmount_texture_headers.bnk` / `…_textures.bnk` (both
/// nested in levels.bnk with their `.dat` entries beside them), whose 451 entries are keyed by
/// the FNV-1 of the lower-cased path with `art\gui_memmount\` kept — the guild seal is
/// `art\gui_memmount\hudassests\version4\upperelements\followercollection\guildseal_gold.tex`
/// (C51DF0BA, the id the live icon carries). The `art\gui\gui_texture_headers.bnk` pair holds
/// the other 1723 (the START prompt among them) and none of these. Run r2 drew every type-72
/// icon UNRESOLVED because the path was rewritten to `art/gui/…`, which no bank holds.
/// </summary>
public static class Fable3IconArt
{
	private const string Wheel = "GUI_MemMount\\wheelicons\\";
	private const string Splash = Wheel + "splashopinions\\";
	private const string Status = Wheel + "npcstatus\\";
	private const string Hud = "GUI_MemMount\\HUDAssests\\";
	private const string World = Hud + "WorldIcons\\";
	private const string Buttons = Hud + "Version4\\Buttons\\";

	/// <summary>Type to path, every case of sub_82B530E8 (0x82B5314C jump table); 54 and 64 name nothing.</summary>
	public static readonly IReadOnlyList<KeyValuePair<uint, string>> Paths = Build();

	private static List<KeyValuePair<uint, string>> Build()
	{
		var paths = new List<KeyValuePair<uint, string>>();
		void Add(uint type, string path) => paths.Add(new KeyValuePair<uint, string>(type, path));

		string[] opinions = { "attractiveness", "ridiculousness", "love", "morality", "respect", "ugly", "fear", "hate", "evil", "disrespect" };
		for (uint i = 0; i < opinions.Length; ++i)
		{
			Add(1u + i, Splash + "opinion_" + opinions[i]);
			Add(11u + i, Splash + "opinion_" + opinions[i] + "_gold");
		}

		Add(21, Splash + "opinion_renown");
		Add(22, Wheel + "icon_romantically_interested");
		Add(23, Wheel + "opinions\\opinion_love");
		Add(24, Status + "icon_npc_engaged");
		Add(25, Status + "icon_npc_married");
		Add(26, Status + "icon_npc_child");
		Add(27, "GUI_MemMount\\hud\\inworld_reward_icon_quest");
		Add(28, "GUI_MemMount\\hud\\inworld_reward_icon_quest");
		Add(29, Status + "icon_npc_follow");
		Add(30, Status + "icon_npc_wait");
		Add(31, Wheel + "icon_level_exit");
		foreach (uint type in new uint[] { 32, 33, 34, 66 })
		{
			Add(type, Buttons + "A\\24x24_NoRim\\A_Complete");
		}

		Add(36, Wheel + "icon_gold_coin");
		Add(37, Wheel + "icon_gold_coin");
		Add(38, Splash + "opinion_morality");
		Add(39, Splash + "opinion_evil");
		string[] morphs = { "fat", "thin", "tall", "short", "strong", "weak", "attractive", "ugly", "good", "evil" };
		for (uint i = 0; i < morphs.Length; ++i)
		{
			Add(40u + i, Splash + "morph_" + morphs[i]);
		}

		Add(50, "GUI_MemMount\\hud\\inworld_reward_icon");
		Add(51, World + "Gift");
		Add(52, Splash + "icon_player1");
		Add(53, Splash + "icon_player2");
		Add(55, World + "dog_digging");
		Add(56, World + "dog_treasure");
		Add(57, World + "dog_quest");
		Add(58, World + "Owned_Property");
		Add(59, World + "Marital_Home");
		Add(60, World + "Rented_Property");
		Add(61, World + "Co_Owned_Property");
		Add(62, World + "Co_Owned_Marital_Property");
		Add(63, "gui\\multiplayer\\orb_icon_talk");
		Add(65, Splash + "opinion_love");
		Add(67, Buttons + "B\\24x24_NoRim\\B_Complete");
		Add(68, Buttons + "X\\24x24_NoRim\\X_Complete");
		Add(69, Buttons + "Y\\24x24_NoRim\\Y_Complete");
		Add(70, Hud + "version4\\upperelements\\followercollection\\GuildSeal_Bronze");
		Add(71, Hud + "version4\\upperelements\\followercollection\\GuildSeal_Silver");
		Add(72, Hud + "version4\\upperelements\\followercollection\\GuildSeal_Gold");
		Add(73, Wheel + "opinions\\opinion_fear");
		Add(74, Wheel + "opinions\\opinion_hate");
		Add(75, Wheel + "opinions\\opinion_funny");
		Add(76, World + "Heart");
		Add(0, "gui\\icon_lock");
		return paths;
	}

	/// <summary>The mount entry for a type-table path: the guest's own `art\&lt;path&gt;.tex`, lower-cased, forward slashes.</summary>
	public static string BankPath(string path) => ("art\\" + path + ".tex").Replace('\\', '/').ToLowerInvariant();

	/// <summary>The button a pooled prompt icon stands for, by the type the registry gave it; empty for the markers.</summary>
	public static string ButtonFor(uint type) => type switch
	{
		32u or 33u or 34u or 66u or 35u => "A",
		67u => "B",
		68u => "X",
		69u => "Y",
		_ => string.Empty,
	};
}
