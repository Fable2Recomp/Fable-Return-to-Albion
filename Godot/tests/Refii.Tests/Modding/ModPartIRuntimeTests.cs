namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Mods;
using Refii.Ui;
using Refii.Titles.Fable2;

/// <summary>
/// Part I, items I.5 – I.9 (hard_mods_plan): the world meter on the marker channel, the HUD a mod
/// fills (hud_icon on a mod picture piece), the Lua runtime every mod would write (clock, timers,
/// per-entity values, pools, in_play, constants, spell_info, setting_changed), the record getters,
/// add_item's count, the engine-enforced pick exclude and legacy keys, the sticky Lua wheel lock,
/// the deferred interrupt grant and the refused press's release — each on every surface it fits.
/// No Godot runtime object is made here (Godot structs only).
/// </summary>
[TestSuite]
public class ModPartIRuntimeTests
{
    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    private static string Read(string root, params string[] parts) => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    private static string Native => string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
    private static string Lua => string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
    private static string GodotSurface => string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));

    private static bool GodotNames(string name) => ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name));

    // ---- I.5 the world meter ------------------------------------------------------------------

    [TestCase]
    public void AWorldMeterCrossesTheBusAsAMarkerWithAFill()
    {
        Engine_UiModRequest.Request? bar = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(
            "op='mark',id=7,player=2,entity=44,x=0,y=0,z=0,icon='',text='',meter=1,value=30,max=120,style='health numbers'"));
        AssertThat(bar).IsNotNull();
        AssertThat(bar!.Value.Op).IsEqual(Engine_UiModRequest.Op.Mark);
        AssertThat(bar.Value.HasMeter).IsTrue();
        AssertThat(bar.Value.Value).IsEqual(30f);
        AssertThat(bar.Value.Max).IsEqual(120f);
        AssertThat(bar.Value.Style).IsEqual("health numbers");
        AssertThat(bar.Value.Player).IsEqual(2);

        // A fill with no max is no meter, and a mark with nothing to draw is no request.
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(
            "op='mark',id=8,player=0,entity=0,x=0,y=0,z=0,icon='',text='',meter=1,value=3,max=0,style=''"))).IsNull();
        Engine_UiModRequest.Request? plain = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(
            "op='mark',id=9,player=0,entity=0,x=0,y=0,z=0,icon='quest',text=''"));
        AssertThat(plain!.Value.HasMeter).IsFalse();

        Engine_UiModRequest.Request? restate = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(
            "op='mark_meter',id=7,value=12.5,max=120,style=''"));
        AssertThat(restate!.Value.Op).IsEqual(Engine_UiModRequest.Op.MarkMeter);
        AssertThat(restate.Value.Id).IsEqual(7u);
        AssertThat(restate.Value.Value).IsEqual(12.5f);
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='mark_meter',id=0,value=1,max=2"))).IsNull();
    }

    [TestCase]
    public void AWorldMetersTrailHoldsThenDrains()
    {
        var meter = new Engine_HudModMarkers.MeterState { Fill = 1f, TrailFrom = 1f };
        Engine_HudModMarkers.Restate(meter, 50f, 100f, "health", 10.0);
        AssertThat(meter.Fill).IsEqual(0.5f);
        // Held at the drop's start for the hold, then drained at 0.9 a second, never below the fill.
        AssertThat(Engine_HudModMarkers.MeterTrail(meter.Fill, meter.TrailFrom, meter.DropAt, 10.2)).IsEqual(1f);
        float draining = Engine_HudModMarkers.MeterTrail(meter.Fill, meter.TrailFrom, meter.DropAt, 10.0 + Engine_HudModMarkers.TrailHoldSeconds + 0.25);
        AssertThat(Math.Abs(draining - (1f - (0.9f * 0.25f))) < 1e-4f).IsTrue();
        AssertThat(Engine_HudModMarkers.MeterTrail(meter.Fill, meter.TrailFrom, meter.DropAt, 20.0)).IsEqual(0.5f);
        // A rise past the trail ends it; a fill clamps to the bar.
        Engine_HudModMarkers.Restate(meter, 150f, 100f, "health", 30.0);
        AssertThat(meter.Fill).IsEqual(1f);
        AssertThat(double.IsNaN(meter.DropAt)).IsTrue();
        AssertThat(Engine_HudModMarkers.MeterTrail(meter.Fill, meter.TrailFrom, meter.DropAt, 31.0)).IsEqual(1f);
        AssertThat(Engine_HudModMarkers.MeterNumbers("health numbers")).IsTrue();
        AssertThat(Engine_HudModMarkers.MeterNumbers("health")).IsFalse();
        AssertThat(Engine_HudModMarkers.MeterText(37.6f, 120f)).IsEqual("38 / 120");
        // x<n> draws the marker n times the curve's size, clamped; any other word leaves it at 1.
        AssertThat(Engine_HudModMarkers.MeterSize("health numbers x1.5")).IsEqual(1.5f);
        AssertThat(Engine_HudModMarkers.MeterSize("X0.6 health")).IsEqual(0.6f);
        AssertThat(Engine_HudModMarkers.MeterSize("health x9")).IsEqual(4f);
        AssertThat(Engine_HudModMarkers.MeterSize("health x0.1")).IsEqual(0.25f);
        AssertThat(Engine_HudModMarkers.MeterSize("health numbers")).IsEqual(1f);
        AssertThat(Engine_HudModMarkers.MeterSize("x")).IsEqual(1f);
        AssertThat(Engine_HudModMarkers.MeterSize("x-2")).IsEqual(1f);
    }

    [TestCase]
    public void TheWorldMeterIsOnEverySurface()
    {
        foreach (string surface in new[] { Native, Lua, GodotSurface })
        {
            AssertThat(surface.Contains(ModApi.WorldMeterMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(Native.Contains("refii_mod_world_meter(player, entity, xyz, value, max, style)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Lua.Contains("Refii.world_meter(target, value, max [, style [, p]])", StringComparison.Ordinal)).IsTrue();
        AssertThat(GodotNames("world_meter") && GodotNames("mark_meter")).IsTrue();
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = Read(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h");
        AssertThat(header.Contains("extern \"C\" uint32_t refii_mod_world_meter(int32_t player, uint32_t entity, const float* xyz, float value, float max,", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_mark_meter(uint32_t id, float value, float max, const char* style);", StringComparison.Ordinal)).IsTrue();
        string api = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp");
        AssertThat(api.Contains("\"op='mark_meter',id=\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains(",icon='',text='',meter=1,value=", StringComparison.Ordinal)).IsTrue();
        string natives = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp");
        AssertThat(natives.Contains("{\"_world_meter\", &NativeWorldMeter, 0}", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"mark_meter\", &NativeMarkMeter, 0}", StringComparison.Ordinal)).IsTrue();
        string preamble = Read(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs");
        AssertThat(preamble.Contains("function Refii.world_meter(target, value, max, style, p)", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("function Refii.mark(target, icon, text, p, o)", StringComparison.Ordinal)).IsTrue();
    }

    // ---- I.6 the HUD a mod fills ---------------------------------------------------------------

    [TestCase]
    public void HudIconFillsAModsPicturePieceByItsId()
    {
        Engine_UiHudOverrides.Clear();
        try
        {
            AssertThat(Engine_UiHudOverrides.SetIcon(2, "my_mod.slot1", "art/gui/wheelicons/abilities/will_shock.tex")).IsTrue();
            AssertThat(Engine_UiHudOverrides.IconForPiece(2, "my_mod.slot1")).IsEqual("art/gui/wheelicons/abilities/will_shock.tex");
            // Each player's own: player one's copy states nothing.
            AssertThat(Engine_UiHudOverrides.IconForPiece(1, "my_mod.slot1")).IsNull();
            // Player 0 states both; "" clears.
            Engine_UiHudOverrides.SetIcon(0, "my_mod.slot2", "art/my_mod/potion.png");
            AssertThat(Engine_UiHudOverrides.IconForPiece(1, "my_mod.slot2")).IsEqual("art/my_mod/potion.png");
            AssertThat(Engine_UiHudOverrides.IconForPiece(2, "my_mod.slot2")).IsEqual("art/my_mod/potion.png");
            Engine_UiHudOverrides.SetIcon(0, "my_mod.slot2", string.Empty);
            AssertThat(Engine_UiHudOverrides.IconForPiece(1, "my_mod.slot2")).IsNull();
        }
        finally
        {
            Engine_UiHudOverrides.Clear();
        }
    }

    [TestCase]
    public void AnEachPlayerPieceKeepsItsFlagThroughTheTwins()
    {
        // (Engine_HudModPieces' statics are a GodotObject's: the region rule is pinned by its text below.)
        var piece = new Engine_HudModPieces.Piece("m.p", "m", Engine_HudModPieces.Kind.Picture, default, Vector2.Zero, new Vector2(48f, 48f),
            string.Empty, "1", default, default, string.Empty, Each: true);
        AssertThat(piece.Each).IsTrue();
        AssertThat(Engine_HudModTwins.WithPlayerTwoTwins(new List<Engine_HudModPieces.Piece> { piece })[0].Each).IsTrue();
    }

    [TestCase]
    public void TheHudPiecesAreFilledAndDrawnInEachPlayersHalf()
    {
        AssertThat(ModApi.HudOverrideMeaning.Contains("MOD'S OWN picture piece", StringComparison.Ordinal)).IsTrue();
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string pieces = Read(root, "Godot", "src", "Engine", "ui", "Engine_HudModPieces.cs");
        AssertThat(pieces.Contains("Engine_UiHudOverrides.IconForPiece(player, built.Piece.Id)", StringComparison.Ordinal)).IsTrue();
        AssertThat(pieces.Contains("public static Texture2D? LoadAnyTexture(string path)", StringComparison.Ordinal)).IsTrue();
        AssertThat(pieces.Contains("material.Uv1Scale = new Vector3(region.Size.X, region.Size.Y, 1f);", StringComparison.Ordinal)).IsTrue();
        AssertThat(pieces.Contains("w = Mathf.Clamp(w, 0f, 1f - u);", StringComparison.Ordinal)).IsTrue();
        string view = Read(root, "Godot", "src", "Engine", "ui", "Engine_HudView.cs");
        AssertThat(view.Contains("piece.Follow.Length != 0 || piece.Each", StringComparison.Ordinal)).IsTrue();
        string guide = Read(root, "Godot", "docs", "modding", "ui-and-hud.md");
        foreach (string words in new[] { "### A mod's own pieces: meter, panel, picture", "`region`", "`each`", "## Textures from a Godot script: game://", "### World meters", "{QUICKSLOTS_SLOT_1}", "`player:<key>`" })
        {
            AssertThat(guide.Contains(words, StringComparison.Ordinal)).OverrideFailureMessage(words).IsTrue();
        }
    }

    // ---- I.7 the Lua runtime -------------------------------------------------------------------

    [TestCase]
    public void TheRuntimeComposesTheNamedNumbersAndTheSpellTable()
    {
        string runtime = ModLua.InfrastructureRuntime();
        foreach ((string name, int value) in ModApi.Constants)
        {
            AssertThat(runtime.Contains($"Refii.{name} = {value}\n", StringComparison.Ordinal)).OverrideFailureMessage(name).IsTrue();
        }

        AssertThat(ModApi.Constants.Select(c => c.Name).Distinct().Count()).IsEqual(ModApi.Constants.Count);
        AssertThat(ModApi.Constants.Single(c => c.Name == "REQUEST_MELEE_PRESSED").Value).IsEqual(16);
        AssertThat(ModApi.Constants.Single(c => c.Name == "ACTION_LOCK_SET").Value).IsEqual(49);
        AssertThat(ModApi.Constants.Single(c => c.Name == "RELATION_ENEMY").Value).IsEqual(4);
        AssertThat(ModApi.Constants.Single(c => c.Name == "ABILITY_RAISE_DEAD").Value).IsEqual(14);

        // spell_info answers the will table the binary builds the wheel from.
        for (int type = 0; type < Engine_UiScreenConstants.Wills.Length; ++type)
        {
            (string tag, string icon) = ModSpells.Info(type)!.Value;
            AssertThat(tag).IsEqual(Engine_UiScreenConstants.Wills[type].Tag);
            AssertThat(icon).IsEqual($"art/gui/wheelicons/abilities/{Engine_UiScreenConstants.Wills[type].Icon}.tex");
            AssertThat(runtime.Contains($"[{type}]={{'{tag}','{icon}'}}", StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(ModSpells.Info(8)).IsNull();
        AssertThat(ModSpells.Info(-1)).IsNull();
    }

    [TestCase]
    public void TheRuntimeHearsItsOwnKindsAndCompiles()
    {
        string runtime = ModLua.InfrastructureRuntime();
        foreach (string kind in new[] { "tick", "entity_removed", "paused", "level_unloading", "level_loaded" })
        {
            int k = ModApi.Find(kind)!.Kind;
            AssertThat(runtime.Contains($"Refii._runtime[{k}] = function(e)", StringComparison.Ordinal)).OverrideFailureMessage(kind).IsTrue();
        }

        foreach (string call in new[] { "function Refii.now()", "function Refii.after(seconds, fn, key)", "function Refii.cancel(key)",
                     "function Refii.entity_value(e, key, default)", "function Refii.entity_set(e, key, v)", "function Refii.pool(p, key, o)",
                     "function Refii.pool_spend(p, key, n)", "function Refii.pool_value(p, key)", "function Refii.in_play(p)",
                     "function Refii.spell_info(t)", "function Refii._setting_changed(key, value)" })
        {
            AssertThat(runtime.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage(call).IsTrue();
        }

        // The runtime stands on the preamble's masks; compiled with the game's own compiler.
        string chunk = "Refii = {} Refii._masks = {} Refii._handlers = {}\n" + runtime;
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(chunk, "@runtime", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;
        }

        AssertThat(ok).OverrideFailureMessage("the runtime does not compile: " + error).IsTrue();
    }

    [TestCase]
    public void TheWholePreambleCompiles()
    {
        // The preamble as a session composes it (no settings in force here): Refii.on's own events
        // and _fire's runtime consumers inside it.
        string preamble = ModLua.RuntimePreamble(Array.Empty<(string, float)>());
        AssertThat(preamble.Contains(ModLua.InfrastructureRuntime(), StringComparison.Ordinal)).IsTrue();
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(preamble, "@preamble", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;
        }

        AssertThat(ok).OverrideFailureMessage("the preamble does not compile: " + error).IsTrue();
    }

    [TestCase]
    public void TheRuntimeIsWiredIntoThePreambleAndTheSettingSeam()
    {
        AssertThat(ModSettings.LuaAssignment("my_mod.size", 2f)).IsEqual(
            "if Refii and Refii._setting_changed then Refii._setting_changed(\"my_mod.size\", 2) elseif RefiiSettings then RefiiSettings[\"my_mod.size\"] = 2 end");
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string preamble = Read(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs");
        AssertThat(preamble.Contains("text.Append(InfrastructureRuntime());", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("\"\\tlocal runtime = Refii._runtime and Refii._runtime[kind]\\n\" +", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("Refii._local_events and Refii._local_events[name]", StringComparison.Ordinal)).IsTrue();
        string settings = Read(root, "Godot", "src", "Engine", "mods", "Engine_ModSettings.cs");
        AssertThat(settings.Contains("ModGodot.Host?.RaiseSettingChanged(key, value);", StringComparison.Ordinal)).IsTrue();
        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        AssertThat(host.Contains("[Signal] public delegate void setting_changedEventHandler(string key, float value);", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void InPlayComposesThePauseModesAndTheLoad()
    {
        var play = new ModPlayState();
        AssertThat(play.InPlay(true, false)).IsTrue();
        AssertThat(play.InPlay(false, false)).IsFalse();
        AssertThat(play.InPlay(true, true)).IsFalse();
        play.Paused(1, true);
        AssertThat(play.WorldRuns).IsFalse();
        play.Paused(2, true);
        play.Paused(1, false);
        AssertThat(play.WorldRuns).IsFalse();
        play.Paused(2, false);
        AssertThat(play.WorldRuns).IsTrue();
        play.Unloading();
        AssertThat(play.InPlay(true, false)).IsFalse();
        play.Loaded();
        AssertThat(play.InPlay(true, false)).IsTrue();
    }

    [TestCase]
    public void TheRuntimeCallsAreOnTheirSurfaces()
    {
        foreach (string meaning in new[] { ModApi.TimerMeaning, ModApi.EntityStoreMeaning, ModApi.PoolMeaning, ModApi.SettingChangedMeaning })
        {
            AssertThat(Lua.Contains(meaning, StringComparison.Ordinal)).IsTrue();
        }

        foreach (string meaning in new[] { ModApi.InPlayMeaning, ModApi.SpellInfoMeaning, ModApi.ConstantsMeaning, ModApi.SettingChangedMeaning })
        {
            AssertThat(Lua.Contains(meaning, StringComparison.Ordinal) && GodotSurface.Contains(meaning, StringComparison.Ordinal)).IsTrue();
        }

        foreach (string name in new[] { "in_play", "constants", "spell_info" })
        {
            AssertThat(GodotNames(name)).OverrideFailureMessage(name).IsTrue();
        }

        AssertThat(ModApi.ConstantsMeaning.Contains("REQUEST_MELEE_PRESSED 16", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheRecordGettersAndTheItemCountAreOnEverySurface()
    {
        foreach (string surface in new[] { Native, Lua, GodotSurface })
        {
            AssertThat(surface.Contains(ModApi.RecordNumberMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.AddItemsMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(Lua.Contains("Refii.record_number(def, field) / Refii.record_bool(def, field) / Refii.record_exists(def)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Lua.Contains("Refii.add_item(e, definition, n) -> item, added", StringComparison.Ordinal)).IsTrue();
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = Read(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h");
        foreach (string declared in new[]
                 {
                     "extern \"C\" int32_t refii_mod_record_number(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,",
                     "extern \"C\" int32_t refii_mod_record_bool(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,",
                     "extern \"C\" int32_t refii_mod_record_exists(PPCContext* ctx, uint8_t* base, uint32_t definition);",
                     "extern \"C\" int32_t refii_mod_add_items(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition, int32_t count,",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage(declared).IsTrue();
        }

        string natives = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp");
        foreach (string row in new[] { "{\"record_number\", &NativeRecordNumber, 0}", "{\"record_bool\", &NativeRecordBool, 0}", "{\"record_exists\", &NativeRecordExists, 0}" })
        {
            AssertThat(natives.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }

        string commands = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp");
        foreach (string what in new[] { "record_number", "record_bool", "record_exists" })
        {
            AssertThat(commands.Contains("what == \"" + what + "\"", StringComparison.Ordinal)).OverrideFailureMessage(what).IsTrue();
        }

        AssertThat(commands.Contains("refii_mod_add_items(&ctx, base, E(w, 1), U(w, 2), w.size() >= 4 ? I(w, 3) : 1, nullptr);", StringComparison.Ordinal)).IsTrue();
        // The float and bool getters ask the database's own type bytes (Engine_GdbData.GdbFieldType).
        string api = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp");
        AssertThat(api.Contains($"kGdbTypeBool = {(int)Refii.Gdb.GdbFieldType.Bool};", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains($"kGdbTypeFloat = {(int)Refii.Gdb.GdbFieldType.Float};", StringComparison.Ordinal)).IsTrue();
    }

    // ---- I.8 picks, the wheel lock, the deferred grant, the refused press -----------------------

    [TestCase]
    public void APickIsDeclaredToTheEngineWithItsExcludeAndLegacyKeys()
    {
        var pick = new Engine_UiSpellPicks.Pick("spell_weaving.partner", "spell_weaving", "spell_weaving.partner_{slot}",
            Excluded: new HashSet<int> { 7, 2 }, LegacySaveKey: "SpellWeavingPartner{slot}", LegacySaveKeyTwo: "SpellWeavingPartner2_{slot}");
        AssertThat(Engine_UiSpellPicks.DeclarationLine(pick)).IsEqual(
            "spell_pick_declare spell_weaving.partner spell_weaving spell_weaving.partner_{slot} 2,7 SpellWeavingPartner{slot} SpellWeavingPartner2_{slot}");
        AssertThat(Engine_UiSpellPicks.DeclarationLine(new Engine_UiSpellPicks.Pick("m.p", string.Empty, "m.p_{slot}"))).IsEqual(
            "spell_pick_declare m.p - m.p_{slot} - - -");
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string abilities = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_abilities.cpp");
        AssertThat(abilities.Contains("op == \"spell_pick_declare\"", StringComparison.Ordinal)).IsTrue();
        // Refused before the event and after a handler's rewrite.
        AssertThat(Regex.Matches(abilities, @"if \(Excluded\(declared, ").Count).IsEqual(2);
        AssertThat(abilities.Contains("ReadLegacyPicks(ctx, base, player);", StringComparison.Ordinal)).IsTrue();
        string title = Read(root, "Godot", "src", "Titles", "Fable2", "Fable2Title.cs");
        AssertThat(title.Contains("Engine_UiSpellPicks.Pump(guestUp);", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheStickyWheelAndTheDeferredGrantAreOnEverySurface()
    {
        AssertThat(Native.Contains("refii_mod_wheel_switches_target_sticky(ctx, base, hero, on)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Lua.Contains("Refii.wheel_switches_target(e, on) " + ModApi.WheelStickyMeaning, StringComparison.Ordinal)).IsTrue();
        foreach (string surface in new[] { Native, Lua, GodotSurface })
        {
            AssertThat(surface.Contains(ModApi.InterruptAtMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(GodotNames("allow_action_interrupt_at")).IsTrue();
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = Read(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h");
        AssertThat(header.Contains("extern \"C\" void refii_mod_wheel_switches_target_sticky(PPCContext* ctx, uint8_t* base, uint32_t hero, int32_t on);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_allow_action_interrupt_at(PPCContext* ctx, uint8_t* base, uint32_t action, float seconds);", StringComparison.Ordinal)).IsTrue();
        string commands = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp");
        AssertThat(commands.Contains("StateWheelSwitches(*ctx, base, hero, on != 0);", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("op == \"allow_action_interrupt_at\"", StringComparison.Ordinal)).IsTrue();
        string natives = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp");
        AssertThat(natives.Contains("refii_mod_wheel_switches_target_sticky(&lua.ctx, lua.base, lua.Entity(1), on ? 1 : 0);", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"allow_action_interrupt_at\", &NativeAllowActionInterruptAt, 0}", StringComparison.Ordinal)).IsTrue();
        string api = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp");
        AssertThat(api.Contains("PumpActionInterrupts(ctx, base);", StringComparison.Ordinal)).IsTrue();
        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        AssertThat(host.Contains("Post($\"allow_action_interrupt_at {action} {Num(seconds)}\")", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void ARefusedMeleePressRefusesItsReleaseOnTheBus()
    {
        ModApiEvent request = ModApi.Find("ability_request")!;
        AssertThat(request.Cancel.Contains("the bus does it", StringComparison.Ordinal)).IsTrue();
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string events = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp");
        AssertThat(events.Contains("const bool release_refused = type == kMeleeReleased && RefusedPress(ctx.r3.u32, true);", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("e.cancel = release_refused;", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("RememberPress(ctx.r3.u32, cancel);", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.Constants.Single(c => c.Name == "REQUEST_MELEE_RELEASED").Value).IsEqual(17);
    }

    // ---- I.9 the docs of what exists --------------------------------------------------------------

    [TestCase]
    public void TheTagLabelsAreStatedOnEveryRowSurface()
    {
        AssertThat(ModApi.MenuLabelMeaning.Contains("{QUICKSLOTS_SLOT_1}", StringComparison.Ordinal)).IsTrue();
        foreach (string surface in new[] { Native, Lua, GodotSurface })
        {
            AssertThat(surface.Contains(ModApi.MenuLabelMeaning, StringComparison.Ordinal)).IsTrue();
        }
    }
}
