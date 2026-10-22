using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// A MOD'S WORLD MARKERS (<c>mark</c> / <c>unmark</c> on every surface: <c>refii_mod_mark</c>,
/// <c>Refii.mark</c>, <c>RefiiHost.mark</c>): an icon and a line of text over an entity or a
/// world point, on the HUD's world-icon channel — the sprite layer the game's own property
/// signs, quest markers and opinion splashes are drawn on (<see cref="Engine_HudWorldSprites"/>).
/// A marker is composed into each player's sprite list the frame it is fed, so it is projected
/// through that player's camera into that player's half, pinned and clipped exactly as the game's
/// icons are, and rides the drawn body (the anchor carries the entity, <c>GameEntities.WorldAnchorFor</c>).
/// </summary>
/// <remarks>
/// <para>SIZED BY THE GAME'S OWN DISTANCE CURVE (<see cref="DistanceSize"/>): the guest sizes a
/// world sprite by <c>+56 * curve(+132)</c>, +132 its squared distance to the hero
/// (<c>sub_821AA758</c>, read in host_bridge.cpp RefiiHostGetWorldSprites) — the curve here is
/// that one, fed the viewing camera's squared distance to the anchor.</para>
/// <para>PER PLAYER: a marker names player 1 or 2, or 0 for every player; a view composes the
/// markers its player sees (<see cref="ShownTo"/>), and a shared couch screen shows both players'.
/// A marker for a player this machine does not play is never drawn here (the mod's code runs on
/// the other machine too).</para>
/// <para>IDS: the payload issues 1 .. 2^23-1, Godot <see cref="FirstHostId"/> (2^23) .. 2^24-1, so
/// every id is exact as a Lua number and any surface's <c>unmark</c> removes any surface's id.</para>
/// </remarks>
public static class Engine_HudModMarkers
{
	/// <summary>The first id a Godot script's marker takes (the payload's end at 2^23-1).</summary>
	public const uint FirstHostId = 0x800000u;
	private const uint LastHostId = 0xFFFFFFu;

	/// <summary>The sprite type the markers carry on the sprite layer: none of the game's (its types end at 54).</summary>
	public const uint SpriteType = 9000u;

	/// <summary>How long a marker on an entity outlives the entity's last stated position.</summary>
	public const double GoneSeconds = 5.0;

	/// <summary>The lift of a point marker over its point, in metres (a marker on an entity is lifted by the drawn height).</summary>
	public const float PointLift = 0.25f;

	/// <summary>
	/// One marker. <paramref name="Entity"/> is a guest ADDRESS (0 for a point) and
	/// <paramref name="Handle"/> the handle the call gave for it (what a script holds);
	/// <paramref name="Point"/> is in the guest's axes.
	/// </summary>
	public sealed record Marker(uint Id, int Player, uint Entity, uint Handle, Vector3 Point, string Icon, string Text)
	{
		/// <summary>The clock at which the entity last had a stated position; NaN before any.</summary>
		public double SeenAt { get; set; } = double.NaN;
	}

	private static readonly Dictionary<uint, Marker> _marks = new();
	private static readonly Dictionary<string, Texture2D?> _icons = new(StringComparer.OrdinalIgnoreCase);
	private static readonly List<uint> _gone = new();
	private static uint _nextHostId = FirstHostId;

	/// <summary>The text colour of the line under the icon: the HUD's gold.</summary>
	private static readonly Color Ink = new(1f, 224f / 255f, 128f / 255f, 1f);

	/// <summary>
	/// Loads an icon by what the call named: a game texture path (<c>art/gui/…/x.tex</c>) or an
	/// enabled mod's PNG (<c>art/x.png</c>). Set by the game's UI owner; null (no loader, or no such
	/// file) draws the marker's text alone.
	/// </summary>
	public static Func<string, Texture2D?>? LoadIcon { get; set; }

	public static int Count => _marks.Count;

	/// <summary>The markers standing, for the debug channel and <c>RefiiHost</c>.</summary>
	public static IEnumerable<Marker> All => _marks.Values;

	/// <summary>
	/// A Godot script's marker: issues its id. <paramref name="handle"/> is an entity HANDLE (0 for
	/// the point); returns 0 for no icon and no text, or an unknown handle.
	/// </summary>
	public static uint Add(int player, uint handle, Vector3 point, string icon, string text)
	{
		uint address = handle != 0 ? Refii.Host.HostBridge.ModHandleAddress(handle) : 0u;
		if ((icon.Length == 0 && text.Length == 0) || (handle != 0 && address == 0))
		{
			return 0;
		}

		uint id = _nextHostId;
		_nextHostId = _nextHostId >= LastHostId ? FirstHostId : _nextHostId + 1;
		Set(id, player, handle, point, icon, text);
		return id;
	}

	/// <summary>A marker from the bus (its id given), or a restated one. <paramref name="handle"/> is the entity's HANDLE (0 for a point).</summary>
	public static void Set(uint id, int player, uint handle, Vector3 point, string icon, string text)
	{
		if (id == 0)
		{
			return;
		}

		uint address = handle != 0 ? Refii.Host.HostBridge.ModHandleAddress(handle) : 0u;
		_marks[id] = new Marker(id, player is 1 or 2 ? player : 0, address, handle, point, icon ?? string.Empty, text ?? string.Empty);
	}

	/// <summary>Removes any surface's id; false when nothing stands under it.</summary>
	public static bool Remove(uint id) => _marks.Remove(id);

	/// <summary>Everything goes (the session ends).</summary>
	public static void Clear()
	{
		_marks.Clear();
		_icons.Clear();
	}

	/// <summary>
	/// The game's size curve for a world sprite by its squared distance to the viewer
	/// (<c>sub_821AA758</c>): 1 − d²/400 within 10 m, 0.75 − 0.00075·(d² − 100) to 17.3 m, 0.6 beyond.
	/// </summary>
	public static float DistanceSize(float distanceSquared)
	{
		if (!float.IsFinite(distanceSquared) || distanceSquared < 0f)
		{
			return 1f;
		}

		if (distanceSquared >= 300f)
		{
			return 0.6f;
		}

		return distanceSquared < 100f
			? 1f - (distanceSquared / 400f)
			: 0.75f - (0.00075f * (distanceSquared - 100f));
	}

	/// <summary>
	/// Whether a view drawn for <paramref name="viewPlayer"/> (1 or 2) shows a marker for
	/// <paramref name="markerPlayer"/> (0: every player): its own player's, every player's, and —
	/// on a shared couch screen (<paramref name="sharedScreen"/>: one window, both local players) —
	/// the other local player's too.
	/// </summary>
	public static bool ShownTo(int markerPlayer, int viewPlayer, bool sharedScreen) =>
		markerPlayer == 0 || markerPlayer == viewPlayer || (sharedScreen && markerPlayer is 1 or 2);

	/// <summary>The sprite key a marker takes on the layer: outside every guest address (the layer keys on the guest's object words).</summary>
	public static uint SpriteKey(uint id) => 0xFE000000u | (id & 0xFFFFFFu);

	/// <summary>
	/// Composes this frame's markers for one view into <paramref name="into"/>, after the game's
	/// own sprites: those <see cref="ShownTo"/> the view, each anchored over its entity's stated
	/// position (lifted by its drawn height) or at its point, sized by
	/// <paramref name="distanceSquaredTo"/> (the view camera's squared distance to a guest point;
	/// null for no camera) through <see cref="DistanceSize"/>. A marker whose entity has had no
	/// position for <see cref="GoneSeconds"/> is dropped. <paramref name="clock"/> is any monotonic
	/// seconds.
	/// </summary>
	public static void Compose(List<Engine_HudWorldSprites.Sprite> into, int viewPlayer, bool sharedScreen,
		Func<Vector3, float?> distanceSquaredTo, double clock)
	{
		if (_marks.Count == 0)
		{
			return;
		}

		_gone.Clear();
		foreach (Marker mark in _marks.Values)
		{
			Vector3 anchor;
			if (mark.Entity != 0)
			{
				if (!Refii.Game.GameEntities.TryGuestPosition(mark.Entity, out Vector3 guest))
				{
					if (double.IsNaN(mark.SeenAt))
					{
						mark.SeenAt = clock;
					}
					else if (clock - mark.SeenAt > GoneSeconds)
					{
						_gone.Add(mark.Id);
					}

					continue;
				}

				mark.SeenAt = clock;
				float lift = Refii.Game.GameEntities.Current?.DrawnHeight(mark.Entity) ?? 1.8f;
				anchor = guest + new Vector3(0f, 0f, lift);
			}
			else
			{
				anchor = mark.Point + new Vector3(0f, 0f, PointLift);
			}

			if (!ShownTo(mark.Player, viewPlayer, sharedScreen))
			{
				continue;
			}

			float scale = DistanceSize(distanceSquaredTo(anchor) ?? 0f);
			into.Add(new Engine_HudWorldSprites.Sprite(
				SpriteType, 0u, Ink, mark.Entity, anchor, scale, mark.Text, string.Empty, SpriteKey(mark.Id),
				string.Empty, Icon(mark.Icon)));
		}

		foreach (uint id in _gone)
		{
			_marks.Remove(id);
		}
	}

	/// <summary>
	/// THE GAME'S OWN WORLD-ICON NAMES, as a short alias each: the textures the guest's world-icon
	/// builder <c>sub_82820640</c> (TU1) names per icon kind — <c>"art\gui\" + name + ".tex"</c>
	/// (0x82820a0c..0x82820a44) — so a mod can say <c>icon = "quest"</c> and draw the quest marker
	/// the game draws. A name that is not an alias is a path as given.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> IconAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		// hud\ — the in-world reward icons (kinds 0x1B/0x1C, 0x32, 0x33)
		["quest"] = "art/gui/hud/inworld_reward_icon_quest.tex",
		["reward"] = "art/gui/hud/inworld_reward_icon.tex",
		["freebie"] = "art/gui/hud/inworld_reward_icon_freebie.tex",
		["reward_quest"] = "art/gui/wheelicons/rewards/reward_quest.tex",
		// wheelicons\npcstatus\ (0x18..0x1A, 0x1D, 0x1E) and the wheel's singles
		["engaged"] = "art/gui/wheelicons/npcstatus/icon_npc_engaged.tex",
		["married"] = "art/gui/wheelicons/npcstatus/icon_npc_married.tex",
		["child"] = "art/gui/wheelicons/npcstatus/icon_npc_child.tex",
		["follow"] = "art/gui/wheelicons/npcstatus/icon_npc_follow.tex",
		["wait"] = "art/gui/wheelicons/npcstatus/icon_npc_wait.tex",
		["romance"] = "art/gui/wheelicons/icon_romantically_interested.tex",
		["love"] = "art/gui/wheelicons/opinions/opinion_love.tex",
		["exit"] = "art/gui/wheelicons/icon_level_exit.tex",
		["gold"] = "art/gui/wheelicons/icon_gold_coin.tex",
		["button_a"] = "art/gui/controller/icon_button_a.tex",
		["player1"] = "art/gui/wheelicons/splashopinions/icon_player1.tex",
		["player2"] = "art/gui/wheelicons/splashopinions/icon_player2.tex",
		// wheelicons\pet\ (0x37..0x39) and the co-op orb (0x3B)
		["dig"] = "art/gui/wheelicons/pet/icon_pet_point_dig.tex",
		["chest"] = "art/gui/wheelicons/pet/icon_pet_point_chest.tex",
		["pet_quest"] = "art/gui/wheelicons/pet/icon_pet_point_quest.tex",
		["talk"] = "art/gui/multiplayer/orb_icon_talk.tex",
		["lock"] = "art/gui/icon_lock.tex",
		// wheelicons\splashopinions\ — the opinion splashes (1..0x15, the _gold set 0xB..0x14) and morphs (0x28..0x31)
		["opinion_attractiveness"] = "art/gui/wheelicons/splashopinions/opinion_attractiveness.tex",
		["opinion_ridiculousness"] = "art/gui/wheelicons/splashopinions/opinion_ridiculousness.tex",
		["opinion_love"] = "art/gui/wheelicons/splashopinions/opinion_love.tex",
		["opinion_morality"] = "art/gui/wheelicons/splashopinions/opinion_morality.tex",
		["opinion_respect"] = "art/gui/wheelicons/splashopinions/opinion_respect.tex",
		["opinion_ugly"] = "art/gui/wheelicons/splashopinions/opinion_ugly.tex",
		["opinion_fear"] = "art/gui/wheelicons/splashopinions/opinion_fear.tex",
		["opinion_hate"] = "art/gui/wheelicons/splashopinions/opinion_hate.tex",
		["opinion_evil"] = "art/gui/wheelicons/splashopinions/opinion_evil.tex",
		["opinion_disrespect"] = "art/gui/wheelicons/splashopinions/opinion_disrespect.tex",
		["opinion_renown"] = "art/gui/wheelicons/splashopinions/opinion_renown.tex",
		["opinion_attractiveness_gold"] = "art/gui/wheelicons/splashopinions/opinion_attractiveness_gold.tex",
		["opinion_ridiculousness_gold"] = "art/gui/wheelicons/splashopinions/opinion_ridiculousness_gold.tex",
		["opinion_love_gold"] = "art/gui/wheelicons/splashopinions/opinion_love_gold.tex",
		["opinion_morality_gold"] = "art/gui/wheelicons/splashopinions/opinion_morality_gold.tex",
		["opinion_respect_gold"] = "art/gui/wheelicons/splashopinions/opinion_respect_gold.tex",
		["opinion_ugly_gold"] = "art/gui/wheelicons/splashopinions/opinion_ugly_gold.tex",
		["opinion_fear_gold"] = "art/gui/wheelicons/splashopinions/opinion_fear_gold.tex",
		["opinion_hate_gold"] = "art/gui/wheelicons/splashopinions/opinion_hate_gold.tex",
		["opinion_evil_gold"] = "art/gui/wheelicons/splashopinions/opinion_evil_gold.tex",
		["opinion_disrespect_gold"] = "art/gui/wheelicons/splashopinions/opinion_disrespect_gold.tex",
		["morph_fat"] = "art/gui/wheelicons/splashopinions/morph_fat.tex",
		["morph_thin"] = "art/gui/wheelicons/splashopinions/morph_thin.tex",
		["morph_tall"] = "art/gui/wheelicons/splashopinions/morph_tall.tex",
		["morph_short"] = "art/gui/wheelicons/splashopinions/morph_short.tex",
		["morph_strong"] = "art/gui/wheelicons/splashopinions/morph_strong.tex",
		["morph_weak"] = "art/gui/wheelicons/splashopinions/morph_weak.tex",
		["morph_attractive"] = "art/gui/wheelicons/splashopinions/morph_attractive.tex",
		["morph_ugly"] = "art/gui/wheelicons/splashopinions/morph_ugly.tex",
		["morph_good"] = "art/gui/wheelicons/splashopinions/morph_good.tex",
		["morph_evil"] = "art/gui/wheelicons/splashopinions/morph_evil.tex",
	};

	/// <summary>The texture path an icon name loads: an alias's game texture, else the name itself.</summary>
	public static string IconPath(string icon) =>
		IconAliases.TryGetValue(icon ?? string.Empty, out string? path) ? path : icon ?? string.Empty;

	/// <summary>The icon's texture, loaded once per name (a miss is remembered too); an alias loads its game texture.</summary>
	private static Texture2D? Icon(string name)
	{
		if (name.Length == 0)
		{
			return null;
		}

		if (!_icons.TryGetValue(name, out Texture2D? texture))
		{
			texture = LoadIcon?.Invoke(IconPath(name));
			if (texture is null)
			{
				GD.PushWarning($"[mod-ui] mark: no icon '{name}' in the game's textures or the mods' art; the text is drawn alone");
			}

			_icons[name] = texture;
		}

		return texture;
	}
}
