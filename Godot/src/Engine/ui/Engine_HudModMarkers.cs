using System;
using System.Collections.Generic;
using System.Linq;
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
/// THE GAME'S OWN WORLD ICONS go through this one composition too (<see cref="Compose"/> →
/// <see cref="ComposeGame"/>): each icon the guest's vector states — its kind, its texture HASH,
/// its tweened scale (the guest's own distance curve, already applied), its fade colour, its
/// anchor, its two lines — is a sprite of the same list, shown to the view by the one per-player
/// rule (<see cref="GameIconShownTo"/>) and badged for the shared couch screen
/// (<see cref="GameIconBadge"/>). The guest is the sole source of what is lit, where, at what size
/// and with which texture; nothing here re-derives a stated value.
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
/// <para>A WORLD METER (<c>world_meter</c> / <c>mark_meter</c> / <c>mark(…, { meter = … })</c>) is a
/// marker's fill: a bar under its icon and text, <see cref="MeterState"/>, restated in place by id
/// so a value changes without re-marking. A drop leaves a pale trail that holds
/// <see cref="TrailHoldSeconds"/> and drains at <see cref="TrailDrainPerSecond"/> of a full bar
/// (<see cref="MeterTrail"/>); the bar is placed, sized and seen exactly as its marker is, and a
/// style's <c>x&lt;n&gt;</c> draws the marker n times the curve's size (<see cref="MeterSize"/>).</para>
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

		/// <summary>The marker's fill, or null for none.</summary>
		public MeterState? Meter { get; set; }
	}

	/// <summary>How long a world meter's trail holds after a drop before it drains.</summary>
	public const double TrailHoldSeconds = 0.45;

	/// <summary>How fast a world meter's trail drains, in full bars a second.</summary>
	public const double TrailDrainPerSecond = 0.9;

	/// <summary>
	/// A marker's fill as stated: <see cref="Value"/> of <see cref="Max"/>, its <see cref="Style"/>,
	/// and the trail a drop leaves (<see cref="TrailFrom"/> at <see cref="DropAt"/>; NaN for none).
	/// </summary>
	public sealed class MeterState
	{
		public float Value { get; set; }
		public float Max { get; set; } = 1f;
		public string Style { get; set; } = string.Empty;
		public float Fill { get; set; }
		public float TrailFrom { get; set; }
		public double DropAt { get; set; } = double.NaN;
	}

	/// <summary>
	/// The trail's length at <paramref name="clock"/>: what it stood at the drop for
	/// <see cref="TrailHoldSeconds"/>, then draining to the fill; the fill itself with no drop.
	/// </summary>
	public static float MeterTrail(float fill, float trailFrom, double dropAt, double clock)
	{
		if (double.IsNaN(dropAt) || trailFrom <= fill)
		{
			return fill;
		}

		double since = clock - dropAt - TrailHoldSeconds;
		return since <= 0 ? trailFrom : (float)Math.Max(fill, trailFrom - (TrailDrainPerSecond * since));
	}

	/// <summary>
	/// A fill restated on <paramref name="meter"/> at <paramref name="clock"/>: a drop starts the
	/// trail from where it stands, a rise past the trail ends it.
	/// </summary>
	public static void Restate(MeterState meter, float value, float max, string style, double clock)
	{
		float fill = max > 0f && float.IsFinite(value) ? Math.Clamp(value / max, 0f, 1f) : 0f;
		float trail = MeterTrail(meter.Fill, meter.TrailFrom, meter.DropAt, clock);
		if (fill < meter.Fill)
		{
			meter.TrailFrom = Math.Max(trail, meter.Fill);
			meter.DropAt = clock;
		}
		else if (fill >= trail)
		{
			meter.DropAt = double.NaN;
		}

		meter.Fill = fill;
		meter.Value = float.IsFinite(value) ? value : 0f;
		meter.Max = max;
		meter.Style = style ?? string.Empty;
	}

	/// <summary>The fill colour a style names: "health" red, an HTML colour as given, else ("bar", "") the HUD's gold.</summary>
	public static Color MeterColour(string style)
	{
		string first = MeterWords(style).FirstOrDefault(w => !w.Equals("numbers", StringComparison.OrdinalIgnoreCase) && SizeWord(w) is null) ?? string.Empty;
		if (first.Equals("health", StringComparison.OrdinalIgnoreCase))
		{
			return HealthFill;
		}

		return first.StartsWith('#') && Color.HtmlIsValid(first) ? Color.FromHtml(first) : Ink;
	}

	/// <summary>Whether a style asks for the numbers ("health numbers").</summary>
	public static bool MeterNumbers(string style) =>
		MeterWords(style).Any(w => w.Equals("numbers", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// The size a style asks its marker drawn at, over the game's distance curve: a word <c>x&lt;n&gt;</c>
	/// ("health x1.5"), n clamped to 0.25..4; 1 with none.
	/// </summary>
	public static float MeterSize(string style) =>
		MeterWords(style).Select(SizeWord).FirstOrDefault(n => n is not null) ?? 1f;

	private static float? SizeWord(string word) =>
		word.Length > 1 && (word[0] == 'x' || word[0] == 'X') &&
		float.TryParse(word.AsSpan(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n) &&
		float.IsFinite(n) && n > 0f
			? Math.Clamp(n, 0.25f, 4f)
			: null;

	/// <summary>The line a meter's numbers draw: "value / max", rounded.</summary>
	public static string MeterText(float value, float max) =>
		string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Round(value):0} / {Math.Round(max):0}");

	private static string[] MeterWords(string style) =>
		(style ?? string.Empty).Split(new[] { ' ', ',', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>The "health" style's fill.</summary>
	private static readonly Color HealthFill = new(0.72f, 0.09f, 0.07f, 1f);

	/// <summary>The clock a meter's trail runs on: the same monotonic seconds the HUD composes markers with.</summary>
	public static double Clock => Time.GetTicksMsec() / 1000.0;

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
		MeterState? meter = _marks.TryGetValue(id, out Marker? previous) ? previous.Meter : null;
		_marks[id] = new Marker(id, player is 1 or 2 ? player : 0, address, handle, point, icon ?? string.Empty, text ?? string.Empty)
		{
			Meter = meter,
		};
	}

	/// <summary>
	/// A Godot script's world meter: a marker that is a bar, its id issued (0 for no max or an
	/// unknown handle). <paramref name="handle"/> is an entity HANDLE (0 for the point).
	/// </summary>
	public static uint AddMeter(int player, uint handle, Vector3 point, float value, float max, string style)
	{
		uint address = handle != 0 ? Refii.Host.HostBridge.ModHandleAddress(handle) : 0u;
		if (!(max > 0f) || !float.IsFinite(max) || (handle != 0 && address == 0))
		{
			return 0;
		}

		uint id = _nextHostId;
		_nextHostId = _nextHostId >= LastHostId ? FirstHostId : _nextHostId + 1;
		Set(id, player, handle, point, string.Empty, string.Empty);
		SetMeter(id, value, max, style, Clock);
		return id;
	}

	/// <summary>
	/// Puts a fill on marker <paramref name="id"/> or restates it in place (<see cref="Restate"/>); an
	/// empty <paramref name="style"/> keeps the standing one. <paramref name="max"/> 0 or less takes
	/// it off, and a marker left with no icon, text or fill goes. False when nothing stands under the id.
	/// </summary>
	public static bool SetMeter(uint id, float value, float max, string style, double clock)
	{
		if (!_marks.TryGetValue(id, out Marker? mark))
		{
			return false;
		}

		if (!(max > 0f) || !float.IsFinite(max))
		{
			mark.Meter = null;
			if (mark.Icon.Length == 0 && mark.Text.Length == 0)
			{
				_marks.Remove(id);
			}

			return true;
		}

		if (mark.Meter is null)
		{
			float fill = float.IsFinite(value) ? Math.Clamp(value / max, 0f, 1f) : 0f;
			mark.Meter = new MeterState { Fill = fill, TrailFrom = fill };
		}

		Restate(mark.Meter, value, max, string.IsNullOrWhiteSpace(style) ? mark.Meter.Style : style, clock);
		return true;
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

	/// <summary>An opinion icon of the game's: the splashes (1..20) and the relationship hearts (22..26).</summary>
	public static bool IsOpinionIcon(uint type) => type is (>= 1u and <= 20u) or (>= 22u and <= 26u);

	/// <summary>A relationship heart (22..26): the icon that carries a player badge on a shared couch screen.</summary>
	public static bool IsHeartIcon(uint type) => type is >= 22u and <= 26u;

	/// <summary>
	/// THE ONE PER-PLAYER RULE for the game's own world icons (mod_events.cpp, the "_P2" keys): a
	/// view drawn for player 1 shows player one's icons, and player two's too on one shared screen
	/// — a couch screen, or an online joiner's window (it plays player two) — never an online host's
	/// (<paramref name="hostOnline"/>: the joiner is sent player two's). Player two's half on split
	/// screen (<paramref name="viewPlayer"/> 2, only while <paramref name="split"/>) shows player
	/// two's icons and every icon that is not an opinion: each half shows its own player's hearts
	/// and splashes, both halves the signs and markers.
	/// </summary>
	public static bool GameIconShownTo(bool playerTwo, bool opinion, int viewPlayer, bool split, bool hostOnline) =>
		viewPlayer == 2
			? split && (playerTwo || !opinion)
			: !playerTwo || (!split && !hostOnline);

	/// <summary>
	/// The badge a game icon carries: the player a relationship heart is for ("1" or "2") when both
	/// local players share one screen (<paramref name="localCoop"/> and not <paramref name="split"/>);
	/// empty otherwise.
	/// </summary>
	public static string GameIconBadge(uint type, bool playerTwo, bool split, bool localCoop) =>
		!split && localCoop && IsHeartIcon(type) ? (playerTwo ? "2" : "1") : string.Empty;

	/// <summary>
	/// THE WORLD ICONS OF ONE VIEW, composed into <paramref name="into"/>: the game's own first
	/// (<see cref="ComposeGame"/>), the mods' markers after them (<see cref="ComposeMarkers"/>) —
	/// one list for the one sprite layer. <paramref name="game"/> is the guest's live vector as
	/// the bridge reads it; <paramref name="viewPlayer"/> 1 or 2; <paramref name="split"/> whether
	/// the window is split; <paramref name="localCoop"/> whether both players are on this machine;
	/// <paramref name="hostOnline"/> whether this machine hosts an online session.
	/// </summary>
	public static void Compose(List<Engine_HudWorldSprites.Sprite> into, IReadOnlyList<Refii.Host.HostBridge.WorldSprite> game,
		int viewPlayer, bool split, bool localCoop, bool hostOnline, Func<Vector3, float?> distanceSquaredTo, double clock)
	{
		ComposeGame(into, game, viewPlayer, split, localCoop, hostOnline);
		ComposeMarkers(into, viewPlayer, !split && localCoop, distanceSquaredTo, clock);
	}

	/// <summary>
	/// The game's own world icons for one view, each exactly as the guest states it: the kind,
	/// the texture hash (resolved by the layer), the fade colour, the entity, the anchor, the
	/// tweened scale, the title and amount, the object word as its identity — those
	/// <see cref="GameIconShownTo"/> the view, badged by <see cref="GameIconBadge"/>. A pooled
	/// sprite (a prompt on a HUD element) is not an icon of this layer and is skipped.
	/// </summary>
	public static void ComposeGame(List<Engine_HudWorldSprites.Sprite> into, IReadOnlyList<Refii.Host.HostBridge.WorldSprite> game,
		int viewPlayer, bool split, bool localCoop, bool hostOnline)
	{
		foreach (Refii.Host.HostBridge.WorldSprite sprite in game)
		{
			if (sprite.Pooled || !GameIconShownTo(sprite.PlayerTwo, IsOpinionIcon(sprite.Type), viewPlayer, split, hostOnline))
			{
				continue;
			}

			into.Add(new Engine_HudWorldSprites.Sprite(
				sprite.Type, sprite.TextureHash, sprite.Colour, sprite.Entity, sprite.Anchor,
				sprite.Scale, sprite.Title, sprite.Opinion, sprite.Object,
				GameIconBadge(sprite.Type, sprite.PlayerTwo, split, localCoop)));
		}
	}

	/// <summary>
	/// Composes this frame's markers for one view into <paramref name="into"/>, after the game's
	/// own sprites: those <see cref="ShownTo"/> the view, each anchored over its entity's stated
	/// position (lifted by its drawn height) or at its point, sized by
	/// <paramref name="distanceSquaredTo"/> (the view camera's squared distance to a guest point;
	/// null for no camera) through <see cref="DistanceSize"/>. A marker whose entity has had no
	/// position for <see cref="GoneSeconds"/> is dropped. <paramref name="clock"/> is any monotonic
	/// seconds.
	/// </summary>
	public static void ComposeMarkers(List<Engine_HudWorldSprites.Sprite> into, int viewPlayer, bool sharedScreen,
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

			float scale = DistanceSize(distanceSquaredTo(anchor) ?? 0f) * (mark.Meter is { } sized ? MeterSize(sized.Style) : 1f);
			Engine_HudWorldSprites.Bar? bar = null;
			string title = mark.Text;
			if (mark.Meter is { } meter)
			{
				bar = new Engine_HudWorldSprites.Bar(meter.Fill,
					MeterTrail(meter.Fill, meter.TrailFrom, meter.DropAt, clock), MeterColour(meter.Style));
				if (title.Length == 0 && MeterNumbers(meter.Style))
				{
					title = MeterText(meter.Value, meter.Max);
				}
			}

			into.Add(new Engine_HudWorldSprites.Sprite(
				SpriteType, 0u, Ink, mark.Entity, anchor, scale, title, string.Empty, SpriteKey(mark.Id),
				string.Empty, Icon(mark.Icon), bar));
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

	/// <summary>The texture path an icon name loads: an alias's game texture (the title's table, else this one), else the name itself.</summary>
	public static string IconPath(string icon) =>
		(Refii.Core.Engine_TitleGame.Active.MarkerIconAliases ?? IconAliases).TryGetValue(icon ?? string.Empty, out string? path)
			? path
			: icon ?? string.Empty;

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

	/// <summary>
	/// A marker's icon by what the call named: a PNG of an enabled mod (<c>art/x.png</c>, through the
	/// mount or the package itself), else a game texture path through the title's own loader.
	/// </summary>
	public static Texture2D? LoadIconWith(string name, Func<string, Texture2D?> gameTexture)
	{
		string path = name.Replace('\\', '/').TrimStart('/');
		if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			return gameTexture(path);
		}

		byte[]? bytes = Refii.Textures.Engine_BnkTextureLoader.ReadBytes(path) ?? Refii.Mods.ModEntries.ReadEnabledBytes(path);
		if (bytes is null)
		{
			return null;
		}

		var image = new Image();
		return image.LoadPngFromBuffer(bytes) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
	}
}
