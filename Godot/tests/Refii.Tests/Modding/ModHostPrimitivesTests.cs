namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The host-side primitives a quickslot-style mod is built from — input chords and claims, HUD
/// overrides by element name, row actions on the game's screen lists — on every surface, with their
/// pure rules held here (no Godot runtime: the enums only).
/// </summary>
[TestSuite]
public class ModHostPrimitivesTests
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

    [TestCase]
    public void TheClaimsAndHudOverridesAreOnEverySurface()
    {
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains(ModApi.InputClaimMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.HudOverrideMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(native.Contains("refii_mod_input_claim(player, action, on)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_hud_icon(player, element, texture)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_hud_slide(player, element, slide)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_hud_hold(player, piece, on)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.input_claim(p, action, on)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.hud_icon(p, element, texture)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.hud_slide(p, element, slide)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.hud_hold(p, piece, on)", StringComparison.Ordinal)).IsTrue();
        foreach (string name in new[] { "claim_input", "input_claimed", "input_claims", "hud_icon", "hud_slide", "hud_hold", "hud_overrides" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name)))
                .OverrideFailureMessage($"'{name}' is not a Godot helper of the catalog").IsTrue();
        }

        // The trailing fields do not bump the version; they are catalogued on the event.
        ModApiEvent row = ModApi.Find("mod_menu_row")!;
        AssertThat(row.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "menu", "row", "player", "mod", "command", "button", "subject_kind", "definition", "spell" });

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h"));
        foreach (string declared in new[]
                 {
                     "extern \"C\" int32_t refii_mod_input_claim(int32_t player, const char* action, int32_t on);",
                     "extern \"C\" int32_t refii_mod_hud_icon(int32_t player, const char* element, const char* texture);",
                     "extern \"C\" int32_t refii_mod_hud_slide(int32_t player, const char* element, int32_t slide);",
                     "extern \"C\" int32_t refii_mod_hud_hold(int32_t player, const char* piece, int32_t on);",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string events = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        AssertThat(events.Contains("const char* command;\n  const char* button;\n", StringComparison.Ordinal)).IsTrue();
        // The typed subject trails them (Part I.3), so a mod built against the old struct reads the same.
        AssertThat(events.Contains("const char* subject_kind;\n  const char* definition;\n  int32_t spell;\n};", StringComparison.Ordinal)).IsTrue();

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string registered in new[] { "{\"input_claim\", &NativeInputClaim, 0}", "{\"hud_icon\", &NativeHudIcon, 0}", "{\"hud_slide\", &NativeHudSlide, 0}", "{\"hud_hold\", &NativeHudHold, 0}" })
        {
            AssertThat(natives.Contains(registered, StringComparison.Ordinal)).OverrideFailureMessage($"{registered} is not registered").IsTrue();
        }

        string api = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp"));
        foreach (string op in new[] { "op='input_claim'", "op='hud_icon'", "op='hud_slide'", "op='hud_hold'" })
        {
            AssertThat(api.Contains(op, StringComparison.Ordinal)).OverrideFailureMessage($"{op} is never posted").IsTrue();
        }

        // The guest's menu_row command carries the two trailing words and the front-end Lua table has them.
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("e.command = command.c_str();", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("e.button = Name(w, 6);", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("mod='%s',command='%s',button='%s'", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheRequestsCrossTheBus()
    {
        Engine_UiModRequest.Request claim = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='input_claim',player=2,action='quickslots.up',on=1"))!.Value;
        AssertThat(claim.Op).IsEqual(Engine_UiModRequest.Op.InputClaim);
        AssertThat(claim.Player).IsEqual(2);
        AssertThat(claim.Name).IsEqual("quickslots.up");
        AssertThat(claim.On).IsTrue();
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='input_claim',player=1,action='',on=1")).HasValue).IsFalse();

        Engine_UiModRequest.Request icon = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hud_icon',player=0,element='DPadUp',texture='art/x.png'"))!.Value;
        AssertThat(icon.Op).IsEqual(Engine_UiModRequest.Op.HudIcon);
        AssertThat(icon.Player).IsEqual(0);
        AssertThat(icon.Icon).IsEqual("art/x.png");

        Engine_UiModRequest.Request slide = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hud_slide',player=1,element='DPadUp',slide=2"))!.Value;
        AssertThat(slide.Op).IsEqual(Engine_UiModRequest.Op.HudSlide);
        AssertThat(slide.Slide).IsEqual(2);
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hud_slide',player=1,element='DPadUp',slide=-1")).HasValue).IsFalse();

        Engine_UiModRequest.Request hold = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hud_hold',player=1,piece='dpad',on=0"))!.Value;
        AssertThat(hold.Op).IsEqual(Engine_UiModRequest.Op.HudHold);
        AssertThat(hold.Name).IsEqual("dpad");
        AssertThat(hold.On).IsFalse();
    }

    [TestCase]
    public void TheOverridesAreHeldPerPlayer()
    {
        Engine_UiHudOverrides.Clear();
        int generation = Engine_UiHudOverrides.Generation;
        AssertThat(Engine_UiHudOverrides.SetIcon(0, "DPadUp", "art/x.png")).IsTrue();
        AssertThat(Engine_UiHudOverrides.SetIcon(1, "", "art/x.png")).IsFalse();
        AssertThat(Engine_UiHudOverrides.IconsFor(1).Any(i => i == ("DPadUp", "art/x.png"))).IsTrue();
        AssertThat(Engine_UiHudOverrides.IconsFor(2).Any(i => i == ("DPadUp", "art/x.png"))).IsTrue();
        AssertThat(Engine_UiHudOverrides.SetIcon(2, "DPadUp", "")).IsTrue();
        AssertThat(Engine_UiHudOverrides.IconsFor(2).Count).IsEqual(0);
        AssertThat(Engine_UiHudOverrides.SetSlide(1, "DPadUp", 2)).IsTrue();
        AssertThat(Engine_UiHudOverrides.SetSlide(1, "DPadUp", -1)).IsFalse();
        AssertThat(Engine_UiHudOverrides.SlidesFor(1).Any(i => i == ("DPadUp", 2))).IsTrue();
        AssertThat(Engine_UiHudOverrides.SetSlide(1, "DPadUp", 0)).IsTrue();
        AssertThat(Engine_UiHudOverrides.SlidesFor(1).Count).IsEqual(0);
        AssertThat(Engine_UiHudOverrides.SetHold(2, "dpad", true)).IsTrue();
        AssertThat(Engine_UiHudOverrides.HoldsFor(2).ToArray()).IsEqual(new[] { "dpad" });
        AssertThat(Engine_UiHudOverrides.AnyFor(2)).IsTrue();
        AssertThat(Engine_UiHudOverrides.Generation > generation).IsTrue();
        Engine_UiHudOverrides.Clear();
        AssertThat(Engine_UiHudOverrides.Count).IsEqual(0);

        // Element paths match by trailing segments; a longer key is retried shorter while two segments remain.
        AssertThat(Engine_UiHudOverrides.MatchesPath("dpad.dpad.dpadmenu.group.up.up", "group.up.up")).IsTrue();
        AssertThat(Engine_UiHudOverrides.MatchesPath("dpad.dpad.dpadmenu.group.up.up", "hud.dpad.dpad.dpadmenu.group.up.up")).IsTrue();
        AssertThat(Engine_UiHudOverrides.MatchesPath("dpad.dpad.dpadmenu.group.up.up", "down.down")).IsFalse();
        AssertThat(Engine_UiHudOverrides.MatchesPath("dpad.dpad.dpadmenu.group.up.up", "up")).IsTrue();

        // The fit: 84% of the slot on the longer side, centred; a slot-sized icon needs none.
        AssertThat(Engine_UiHudOverrides.FitIcon(64, 32)).IsEqual((108, 54, 10, 37));
        AssertThat(Engine_UiHudOverrides.FitIcon(0, 32).Width).IsEqual(0);
        AssertThat(Engine_UiHudOverrides.FitsSlot(128, 128)).IsTrue();
        AssertThat(Engine_UiHudOverrides.FitsSlot(64, 128)).IsFalse();
    }

    [TestCase]
    public void TheChordRulesArePure()
    {
        AssertThat(ModInputClaims.ParseModifier("lt")).IsEqual(ModInputClaims.Modifier.LT);
        AssertThat(ModInputClaims.ParseModifier("RB")).IsEqual(ModInputClaims.Modifier.RB);
        AssertThat(ModInputClaims.ParseModifier("Shift")).IsEqual(ModInputClaims.Modifier.Key);
        AssertThat(ModInputClaims.ParseModifier(" ")).IsEqual(ModInputClaims.Modifier.None);
        AssertThat(ModInputClaims.PadBit(JoyButton.DpadUp)).IsEqual(0x0001u);
        AssertThat(ModInputClaims.PadBit(JoyButton.Y)).IsEqual(0x8000u);
        AssertThat(ModInputClaims.PadBit(JoyButton.Guide)).IsEqual(0u);
        AssertThat(ModInputClaims.PadBits(new[] { JoyButton.A, JoyButton.RightShoulder })).IsEqual(0x1200u);
        AssertThat(ModInputClaims.ModifierHeld(ModInputClaims.Modifier.LT, 0, 31, 0)).IsTrue();
        AssertThat(ModInputClaims.ModifierHeld(ModInputClaims.Modifier.LT, 0, 30, 0)).IsFalse();
        AssertThat(ModInputClaims.ModifierHeld(ModInputClaims.Modifier.LB, 0x0100, 0, 0)).IsTrue();
        AssertThat(ModInputClaims.ModifierBit(ModInputClaims.Modifier.RB)).IsEqual(0x0200u);

        var plain = new ModInput.Action("m.up", "m", "Up", new[] { Key.F1 }, new[] { JoyButton.DpadUp }, Array.Empty<JoyAxis>(), null, 0);
        var chord = new ModInput.Action("m.alt_up", "m", "Alt up", new[] { Key.F1 }, new[] { JoyButton.DpadUp }, Array.Empty<JoyAxis>(), null, 0) { Chord = "LT" };
        var other = new ModInput.Action("m.dash", "m", "Dash", Array.Empty<Key>(), new[] { JoyButton.A }, Array.Empty<JoyAxis>(), null, 0) { Chord = "LT" };
        var actions = new List<ModInput.Action> { plain, chord, other };
        AssertThat(chord.ChordModifier).IsEqual(ModInputClaims.Modifier.LT);
        // The plain action is silent while the chord on its button is held, on the pad and the keyboard.
        AssertThat(ModInputClaims.PlainSuppressed(plain, actions, a => a.Chord == "LT", keyboard: false)).IsTrue();
        AssertThat(ModInputClaims.PlainSuppressed(plain, actions, a => a.Chord == "LT", keyboard: true)).IsTrue();
        AssertThat(ModInputClaims.PlainSuppressed(plain, actions, _ => false, keyboard: false)).IsFalse();
        // A chord on another button leaves it alone; a chord action is never suppressed.
        AssertThat(ModInputClaims.PlainSuppressed(plain, new List<ModInput.Action> { plain, other }, _ => true, keyboard: false)).IsFalse();
        AssertThat(ModInputClaims.PlainSuppressed(chord, actions, _ => true, keyboard: false)).IsFalse();
    }

    [TestCase]
    public void RowActionsComposeOnTheTemplateSet()
    {
        var set = new Engine_UiTemplateSet();
        var warnings = new List<string>();
        set.Warn = warnings.Add;
        int taken = set.ApplyText("""
            { "rowActions": {
                "ItemsMenu": [ { "button": "Y", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ],
                "AbilitiesMenu": [ { "button": "x", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign:spell" },
                                   { "button": "LB", "tag": "NO", "command": "Mod:lua:no" } ],
                "ExpressionsMenu": [ { "button": "Y", "tag": "T" } ]
            } }
            """, "quickslots", "quickslots:ui/templates.json");
        AssertThat(taken).IsEqual(2);
        AssertThat(warnings.Count).IsEqual(2);
        IReadOnlyList<Engine_UiRowActions.RowAction> items = set.RowActions.For("ItemsMenu");
        AssertThat(items.Count).IsEqual(1);
        AssertThat(items[0]).IsEqual(new Engine_UiRowActions.RowAction("Y", "QUICKSLOTS_ASSIGN", "Mod:lua:quickslots.assign", "quickslots"));
        AssertThat(set.RowActions.For("AbilitiesMenu")[0].Button).IsEqual("X");
        AssertThat(set.RowActions.For("ExpressionsMenu").Count).IsEqual(0);
        AssertThat(set.Owners["rowActions:ItemsMenu"]).IsEqual("quickslots");

        // A later file replaces a menu's list whole, or removes it with null / [].
        set.ApplyText("""{ "rowActions": { "ItemsMenu": null, "AbilitiesMenu": [ { "button": "Y", "tag": "B", "command": "Opens:m" } ] } }""", "other", "other");
        AssertThat(set.RowActions.For("ItemsMenu").Count).IsEqual(0);
        AssertThat(set.RowActions.For("AbilitiesMenu").Single().Mod).IsEqual("other");
        AssertThat(set.Conflicts.Any(c => c.Piece == "rowActions:AbilitiesMenu")).IsTrue();
        JsonElement snapshot = JsonDocument.Parse(set.SnapshotText()).RootElement.GetProperty("rowActions");
        AssertThat(snapshot.GetProperty("AbilitiesMenu")[0].GetProperty("command").GetString()).IsEqual("Opens:m");
    }

    [TestCase]
    public void TheRowEventCarriesItsTrailingFields()
    {
        Engine_UiModMenuCommand.Parsed command = Engine_UiModMenuCommand.Parse("Mod:lua:quickslots.assign:spell");
        string chunk = Engine_UiModMenuCommand.LuaCall(command, "ItemsMenu", "Item 3", 2, "quickslots", "Inventory:use:1234", "Y");
        AssertThat(chunk.Contains(",command='Inventory:use:1234',button='Y',subject_kind='item',definition='0x000004d2',spell=-1})", StringComparison.Ordinal)).IsTrue();
        AssertThat(Engine_UiModMenuCommand.LuaCall(command, "m", "r", 1, "q").Contains(",command='',button=''", StringComparison.Ordinal)).IsTrue();

        string line = Engine_UiModMenuCommand.RowCommandLine("ItemsMenu", "Item 3", 2, "quickslots", "Inventory:use:1234", "Y");
        string[] words = line.Split(' ');
        AssertThat(words.Length).IsEqual(10);
        AssertThat(words[0]).IsEqual("menu_row");
        AssertThat(words[3]).IsEqual("Item_3");
        AssertThat(Encoding.UTF8.GetString(Convert.FromBase64String(words[5]))).IsEqual("Inventory:use:1234");
        AssertThat(words[6]).IsEqual("Y");
        // The typed subject trails: kind, "0x…" definition, spell (-1 for none).
        AssertThat(words[7]).IsEqual("item");
        AssertThat(words[8]).IsEqual("0x000004d2");
        AssertThat(words[9]).IsEqual("-1");
        AssertThat(Engine_UiModMenuCommand.RowCommandLine("m", "r", 1, "q").EndsWith(" - - - - -1", StringComparison.Ordinal)).IsTrue();
    }
}
