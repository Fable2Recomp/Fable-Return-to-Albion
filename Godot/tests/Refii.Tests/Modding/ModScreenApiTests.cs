namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Level;
using Refii.Mods;
using Refii.Titles.Fable2;

/// <summary>
/// The Mod API a mod's own screens use: the new models a mod ships (<c>models.json</c>, built from the
/// player's install, never shipped as a <c>.mdl</c>), the level's map bounds from its heightfields, who
/// owns a mod's screen, the inventory actions, and the player argument and Lua route of every new call.
/// Pure: no Godot runtime object is made.
/// </summary>
[TestSuite]
public class ModScreenApiTests
{
	private const string Flintlock = "art/inventory/weapons/dotxsi/rifleflintlock_master/rifleflintlock_master.mdl";

	[TestCase]
	public void AModelsManifestNamesTheModelItsTemplateAndItsTextures()
	{
		var errors = new List<string>();
		Dictionary<string, ModMeshOverlays.NewModel> models = ModMeshOverlays.ParseModels(
			"[{ \"model\": \"Art/My/Gun.mdl\", \"template\": \"" + Flintlock + "\", " +
			"\"materials\": [{ \"diffuse\": \"art/my/gun.tex\" }, { \"specular\": \"\", \"self_illumination\": \"art/my/glow.tex\" }] }]",
			errors);
		AssertThat(errors.Count).OverrideFailureMessage("errors: " + string.Join("; ", errors)).IsEqual(0);
		ModMeshOverlays.NewModel gun = models["art/my/gun.mdl"];
		AssertThat(gun.Template).IsEqual(Flintlock);
		AssertThat(gun.Materials.Count).IsEqual(2);
		AssertThat(gun.Materials[0].Diffuse).IsEqual("art\\my\\gun.tex");
		AssertThat(gun.Materials[0].Specular is null).IsTrue();
		AssertThat(gun.Materials[1].Specular).IsEqual(string.Empty);
		AssertThat(gun.Materials[1].SelfIllumination).IsEqual("art\\my\\glow.tex");

		errors.Clear();
		ModMeshOverlays.ParseModels("[{ \"model\": \"a.mdl\", \"template\": \"a.mdl\" }, { \"model\": \"b.tex\" }, " +
			"{ \"model\": \"c.mdl\", \"template\": \"d.mdl\", \"materials\": [{ \"glow\": \"x\" }] }]", errors);
		AssertThat(errors.Count).IsEqual(3);
		AssertThat(ModValidate.KindOf("models.json")).IsEqual(ModMeshOverlays.ModelsPath);
		AssertThat(ModValidate.Validate("models.json", "{}").Count).IsEqual(1);
	}

	/// <summary>The bundled models are glTF and textures only: no model or socket file built from a shipped one.</summary>
	[TestCase]
	public void TheLevelBoundsAreTheHeightfieldsHeadersUnion()
	{
		static byte[] Header(float x, float y, int width, int height)
		{
			var bytes = new byte[0x14];
			void Word(int at, uint value)
			{
				bytes[at] = (byte)(value >> 24);
				bytes[at + 1] = (byte)(value >> 16);
				bytes[at + 2] = (byte)(value >> 8);
				bytes[at + 3] = (byte)value;
			}

			Word(0x00, (uint)BitConverter.SingleToInt32Bits(x));
			Word(0x04, (uint)BitConverter.SingleToInt32Bits(y));
			Word(0x0C, (uint)width);
			Word(0x10, (uint)height);
			return bytes;
		}

		AssertThat(Engine_LevelMapBounds.TryRect(Header(-512.7f, 100.2f, 513, 257), out Engine_LevelMapBounds.Bounds a)).IsTrue();
		AssertThat(a).IsEqual(new Engine_LevelMapBounds.Bounds(-512, 100, -256, 228));

		var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["worlds/albion/a/a.ghf"] = Header(-512f, 100f, 513, 257),
			["worlds/albion/a/b.ghf"] = Header(0f, -50f, 129, 1025),
		};
		var manifest = Engine_LevelManifest.Parse("data\\worlds\\albion\\a\\a.ghf\ndata\\worlds\\albion\\a\\b.ghf\n");
		Engine_LevelMapBounds.Bounds? union = Engine_LevelMapBounds.Of(manifest, p => files.TryGetValue(p, out byte[]? b) ? b : null);
		AssertThat(union).IsEqual(new Engine_LevelMapBounds.Bounds(-512, -50, 64, 462));
		AssertThat(union!.Value.Extent).IsEqual(576f);
		AssertThat(Engine_LevelMapBounds.TryRect(new byte[4], out _)).IsFalse();
	}

	[TestCase]
	public void AModsScreenIsThatModsForItsPlayer()
	{
		object set = new(), other = new();
		ModScreen.Release();
		ModScreen.Take(set, "satchel", 2);
		AssertThat(ModScreen.Owned && ModScreen.Player == 2 && ModScreen.Mod == "satchel").IsTrue();
		ModScreen.Release(set, "minimap", 2);
		ModScreen.Release(set, "satchel", 1);
		ModScreen.Release(other, "satchel", 2);
		AssertThat(ModScreen.Owned).OverrideFailureMessage("only its mod, of its set, for its player, releases it").IsTrue();
		ModScreen.Release(set, "satchel", 2);
		AssertThat(ModScreen.Owned || ModScreen.Player != 0 || ModScreen.Mod.Length != 0).IsFalse();

		ModScreen.Take(set, "satchel", 1);
		ModScreen.Take(set, "quests", 1);
		ModScreen.Release(set, "satchel", 1);
		AssertThat(ModScreen.Owned && ModScreen.Mod == "quests").OverrideFailureMessage("a later mod takes it over, and the first one's off leaves it").IsTrue();
		ModScreen.ReleaseFor(other);
		AssertThat(ModScreen.Owned).OverrideFailureMessage("another set's stop leaves it").IsTrue();
		ModScreen.ReleaseFor(set);
		AssertThat(ModScreen.Owned).OverrideFailureMessage("the set's stop or reload releases it").IsFalse();

		ModScreen.Take(set, "quests", 2, fromLua: true);
		ModScreen.ReleaseLua(other);
		AssertThat(ModScreen.Owned).IsTrue();
		ModScreen.ReleaseLua(set);
		AssertThat(ModScreen.Owned).OverrideFailureMessage("a Lua reload releases a Lua mod's screen").IsFalse();
		ModScreen.Take(set, "satchel", 2);
		ModScreen.ReleaseLua(set);
		AssertThat(ModScreen.Owned).OverrideFailureMessage("a Lua reload leaves a script's screen").IsTrue();
		ModScreen.Release();
		AssertThat(ModScreen.Owned).IsFalse();
	}

	[TestCase]
	public void AnAskNamesTheModThatAsked()
	{
		ModLuaLane.Request? named = ModLuaLane.ParseAsk("3\nown_screen\nn2\nb1\n-\n-\nssatchel");
		AssertThat(named!.Value.Mod).IsEqual("satchel");
		AssertThat(named.Value.Args.Length).IsEqual(ModLuaLane.AskValues);
		AssertThat(named.Value.Args[1]).IsEqual(true);
		AssertThat(ModLuaLane.ParseAsk("3\nown_screen\nn2\nb1\n-\n-\n-")!.Value.Mod).IsEqual(string.Empty);
		AssertThat(ModLuaLane.ParseAsk("3\nown_screen\nn2\nb1\n-\n-")!.Value.Mod).IsEqual(string.Empty);
		AssertThat(ModLua.RequireRuntime.Contains("function Refii._as_mod(mod)", StringComparison.Ordinal)).IsTrue();
		AssertThat(ModLua.RequireRuntime.Contains("Refii._ask_as(mod, what, fn, a, b, c, d)", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void EachPlayersMoveStickIsTheirOwnPads()
	{
		ModInputSticks.Clear();
		ModInputSticks.Set(1, 32767, 0, 10);
		ModInputSticks.Set(2, 0, -16384, 10);
		AssertThat(ModInputSticks.Move(1, 10)).IsEqual((1f, 0f));
		AssertThat(ModInputSticks.Move(2, 11).Y).IsLess(-0.49f);
		AssertThat(ModInputSticks.Move(2, 11).X).IsEqual(0f);
		AssertThat(ModInputSticks.Move(2, 12)).OverrideFailureMessage("a pad not published since reads zero").IsEqual((0f, 0f));
		AssertThat(ModInputSticks.Move(3, 10)).IsEqual((0f, 0f));
		ModInputSticks.Set(1, 32767, 32767, 20);
		(float x, float y) = ModInputSticks.Move(1, 20);
		AssertThat(MathF.Sqrt(x * x + y * y)).IsLessEqual(1.0001f);
		ModInputSticks.Clear();
		AssertThat(ModInputSticks.Move(1, 20)).IsEqual((0f, 0f));
	}

	[TestCase]
	public void AnInventoryActionIsThePagesOwnRow()
	{
		AssertThat(Engine_UiInventorySource.ModAction("use", 123u, 6)).IsEqual("Inventory:use:123");
		AssertThat(Engine_UiInventorySource.ModAction("outfit", 7u, Engine_UiInventorySource.Category.Suits)).IsEqual("Inventory:outfit:7");
		AssertThat(Engine_UiInventorySource.ModAction("remove", 7u, Engine_UiInventorySource.Category.Hats)).IsEqual("Inventory:unequip:91");
		AssertThat(Engine_UiInventorySource.ModAction("remove", 7u, Engine_UiInventorySource.Category.Suits) is null)
			.OverrideFailureMessage("an outfit is no slot").IsTrue();
		AssertThat(Engine_UiInventorySource.ModAction("drop", 7u, 6) is null).IsTrue();
		AssertThat(Engine_UiInventorySource.ModAction("equip", 9u, 1, 2)).IsEqual("InventoryFor:2:equip:9");
		AssertThat(Engine_UiInventorySource.ModAction("remove", 7u, Engine_UiInventorySource.Category.Hats, 1)).IsEqual("InventoryFor:1:unequip:91");
		AssertThat(Engine_UiInventorySource.ModAction("use", 123u, 6, 0)).IsEqual("Inventory:use:123");
	}

	/// <summary>
	/// The new calls take the player they serve on every surface, Lua reaches each through ask_host as plain
	/// data, the mod's map reads no memory walk, and a mod row's label goes through the one text lookup.
	/// </summary>
	[TestCase]
	public void TheNewCallsTakeAPlayerAndReachLua()
	{
		string? root = GodotModScripts.RepoRoot();
		if (root == null)
		{
			return;
		}

		string host = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
		foreach (string signature in new[]
		{
			"public Godot.Collections.Dictionary minimap(long player)",
			"public Godot.Collections.Array inventory_items(long player)",
			"public bool inventory_action(long player, string operation, Variant definition)",
			"public void own_screen(long player, bool on, Node script)",
			"public void own_screen(long player, bool on)",
			"public Vector2 move_stick(long player)",
			"public Texture2D? input_glyph(string name)",
			"public string input_label(string name)",
		})
		{
			AssertThat(host.Contains(signature, StringComparison.Ordinal)).OverrideFailureMessage("RefiiHost lacks " + signature).IsTrue();
		}

		foreach (string call in new[]
		{
			"case \"minimap\": return Plain(minimap(Player(0)));",
			"case \"inventory_items\": return Plain(Variant.From(inventory_items(Player(0))));",
			"case \"inventory_action\": return inventory_action(Player(0), Text(1), Text(2));",
			"OwnScreen(Player(0), Number(1) != 0f, mod, fromLua: true);",
			"case \"move_stick\": return Plain(move_stick(Player(0)));",
			"case \"touch_mode\":",
			"case \"touch_stick\":",
			"case \"input_label\":",
			"case Variant.Type.PackedStringArray:",
		})
		{
			AssertThat(host.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage("RefiiHost.HostCall lacks " + call).IsTrue();
		}

		foreach (string call in new[] { "touch_mode", "touch_stick", "move_stick", "input_label" })
		{
			AssertThat(ModApi.HostCalls.Contains(call)).IsTrue();
		}

		string map = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "ui", "Engine_UiMinimapMod.cs"));
		foreach (string walk in new[] { "GetMinimapLive", "157.5" })
		{
			AssertThat(map.Contains(walk, StringComparison.Ordinal)).OverrideFailureMessage("the mod's map still reads " + walk).IsFalse();
		}

		// The other quests' objectives: the quest log's non-primary quests, through the shared placement.
		AssertThat(map.Contains("Fable2Bridge.GetQuestLog(_questLog);", StringComparison.Ordinal)).IsTrue();
		AssertThat(map.Contains("!quest.Primary && Engine_UiMinimapPage.Place(info, Vector2.Zero, quest.ObjectiveEntity, extent)", StringComparison.Ordinal)).IsTrue();
		AssertThat(map.Contains("said[\"quests\"] = quests;", StringComparison.Ordinal)).IsTrue();
		string gd = File.ReadAllText(Path.Combine(root, "Fable2", "mods-src", "minimap", "godot", "minimap.gd"));
		AssertThat(gd.Contains("for place in state.get(\"quests\", []):", StringComparison.Ordinal)).OverrideFailureMessage("the minimap mod draws the other quests").IsTrue();
		AssertThat(gd.Contains("const OTHER_QUEST := Color(0.25, 0.68, 0.38)", StringComparison.Ordinal)).IsTrue();
		foreach (string gone in new[] { "\"merchants\"", "state.quest", "\"quest_here\"" })
		{
			AssertThat(gd.Contains(gone, StringComparison.Ordinal) || map.Contains(gone, StringComparison.Ordinal))
				.OverrideFailureMessage("the trail target and merchants stay out: " + gone).IsFalse();
		}

		string rows = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "ui", "Engine_UiModRows.cs"));
		AssertThat(rows.Contains("public static string BookLabel(string label) => Refii.Mods.ModText.Text(label);", StringComparison.Ordinal)).IsTrue();
		AssertThat(rows.Contains("Regex", StringComparison.Ordinal)).OverrideFailureMessage("a second {KEY} resolver").IsFalse();
	}

	/// <summary>
	/// Either player's inventory is one host export served from the pump's per-player snapshot, on the
	/// guest's own route to a player's hero; the addressed action reaches the pump; the skate steers with
	/// the rider's own stick and the satchel names itself as its screen's owner.
	/// </summary>
	[TestCase]
	public void EitherPlayersInventoryAndStickAreTheirOwn()
	{
		string? root = GodotModScripts.RepoRoot();
		if (root == null)
		{
			return;
		}

		string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
		string bridge = Read("Fable2", "src", "hostbridge", "src", "host_bridge.cpp");
		AssertThat(bridge.Contains("REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPlayerInventory(int32_t player, uint32_t* words,", StringComparison.Ordinal)).IsTrue();
		AssertThat(bridge.Contains("return refii_bridge_player_inventory_snapshot(player, words, word_capacity);", StringComparison.Ordinal)).IsTrue();
		string binding = Read("Godot", "src", "Titles", "Fable2", "host", "Fable2Bridge.cs");
		AssertThat(binding.Contains("private static extern uint RefiiHostGetPlayerInventory(int player, uint[]? outWords, uint wordCapacity);", StringComparison.Ordinal)).IsTrue();
		string pump = Read("Fable2", "src", "vfs", "src", "hooks", "guest_tick_pump.cpp");
		AssertThat(pump.Contains("const uint32_t hero = refii_mod_player_hero(base, player);", StringComparison.Ordinal)).IsTrue();
		AssertThat(pump.Contains("refii_bridge_publish_player_inventory(player,", StringComparison.Ordinal)).IsTrue();
		AssertThat(pump.Contains("constexpr const char* kActionInventoryForPrefix = \"InventoryFor:\";", StringComparison.Ordinal)).IsTrue();
		AssertThat(Engine_UiInventorySource.PlayerInventoryAction).IsEqual("InventoryFor:");
		string mod = Read("Godot", "src", "Titles", "Fable2", "ui", "Engine_UiInventoryMod.cs");
		AssertThat(mod.Contains("GetHeroInventory", StringComparison.Ordinal) || mod.Contains("GetWornItems", StringComparison.Ordinal))
			.OverrideFailureMessage("the mod's list reads the menus' hero").IsFalse();

		string skate = Read("Fable2", "mods-src", "skate", "godot", "skate.gd");
		AssertThat(skate.Contains("host.move_stick(player)", StringComparison.Ordinal)).IsTrue();
		foreach (string first in new[] { "get_joy_axis(0", "KEY_W", "KEY_A" })
		{
			AssertThat(skate.Contains(first, StringComparison.Ordinal)).OverrideFailureMessage("the skate still reads " + first).IsFalse();
		}

		string satchel = Read("Fable2", "mods-src", "satchel", "godot", "satchel.gd");
		AssertThat(satchel.Contains("own_screen(player, true, self)", StringComparison.Ordinal)).IsTrue();
		AssertThat(satchel.Contains("own_screen(player, true)", StringComparison.Ordinal) || satchel.Contains("own_screen(player, false)", StringComparison.Ordinal))
			.OverrideFailureMessage("the satchel's screen names no owner").IsFalse();
	}
}
