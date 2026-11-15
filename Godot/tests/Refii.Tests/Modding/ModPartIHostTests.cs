namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// Part I wave A, the host-side primitives the bundled mods worked around: presses (I.1), Lua to
/// Godot on one machine (I.2), menu rows past one bus record, host lists and the row's typed
/// subject (I.3), and typed player values (I.4) — the pure rules here, and every surface named.
/// No Godot runtime: the enums only.
/// </summary>
[TestSuite]
public class ModPartIHostTests
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

    private static List<KeyValuePair<string, object>> Fields(string text) => RefiiHost.ParseFieldsPlain(text);

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // ---- I.1 presses ---------------------------------------------------------------------------

    [TestCase]
    public void APressNamesInputJsonsButtonsAndTriggers()
    {
        AssertThat(ModInputPresses.TryTarget("A", out ModInputPresses.Target a)).IsTrue();
        AssertThat(a.Bit).IsEqual(ModInputClaims.ButtonA);
        AssertThat(ModInputPresses.TryTarget("rb", out ModInputPresses.Target rb)).IsTrue();
        AssertThat(rb.Bit).IsEqual(ModInputClaims.RightShoulder);
        AssertThat(ModInputPresses.TryTarget("DpadUp", out ModInputPresses.Target up)).IsTrue();
        AssertThat(up.Bit).IsEqual(ModInputClaims.DpadUp);
        AssertThat(ModInputPresses.TryTarget("LT", out ModInputPresses.Target lt)).IsTrue();
        AssertThat(lt.Trigger).IsEqual(1);
        AssertThat(ModInputPresses.TryTarget("RT", out ModInputPresses.Target rt)).IsTrue();
        AssertThat(rt.Trigger).IsEqual(2);
        AssertThat(ModInputPresses.TryTarget("Guide", out _)).IsFalse();
        AssertThat(ModInputPresses.TryTarget("", out _)).IsFalse();
        AssertThat(ModInputPresses.Canonical(a)).IsEqual("A");
        AssertThat(ModInputPresses.Canonical(rt)).IsEqual("RT");
    }

    [TestCase]
    public void AHoldIsOrdIntoItsPlayersPadOnlyUntilReleased()
    {
        ModInputPresses.Clear();
        AssertThat(ModInputPresses.Press(0, "A", 1)).IsFalse();
        AssertThat(ModInputPresses.Press(3, "A", 1)).IsFalse();
        AssertThat(ModInputPresses.Press(2, "A", 1)).IsTrue();
        AssertThat(ModInputPresses.Press(2, "RT", 1)).IsTrue();
        AssertThat(ModInputPresses.Press(2, "LT", 100)).IsTrue();

        uint buttons = ModInputClaims.ButtonB, lt = 0, rt = 0;
        ModInputPresses.Apply(1, ref buttons, ref lt, ref rt, 10, true, 0);
        AssertThat(buttons).IsEqual(ModInputClaims.ButtonB);  // player one's pad is untouched
        ModInputPresses.Apply(2, ref buttons, ref lt, ref rt, 10, true, 0);
        AssertThat(buttons).IsEqual(ModInputClaims.ButtonB | ModInputClaims.ButtonA);
        AssertThat(rt).IsEqual(255u);  // true / 1 is a full pull
        AssertThat(lt).IsEqual(100u);
        AssertThat(string.Join(",", ModInputPresses.For(2).Held)).IsEqual("A,LT,RT");

        AssertThat(ModInputPresses.Press(2, "A", 0)).IsTrue();
        buttons = 0;
        lt = rt = 0;
        ModInputPresses.Apply(2, ref buttons, ref lt, ref rt, 11, true, 0);
        AssertThat(buttons).IsEqual(0u);

        ModInputPresses.Clear();
        AssertThat(ModInputPresses.Any).IsFalse();
    }

    [TestCase]
    public void ATapHoldsForWholeGuestTicksThenLetsGo()
    {
        ModInputPresses.Clear();
        AssertThat(ModInputPresses.Tap(1, "B", 0)).IsFalse();
        AssertThat(ModInputPresses.Tap(1, "B", 2)).IsTrue();

        bool Down(uint tick, bool known = true, ulong ms = 0)
        {
            uint buttons = 0, lt = 0, rt = 0;
            ModInputPresses.Apply(1, ref buttons, ref lt, ref rt, tick, known, ms);
            return (buttons & ModInputClaims.ButtonB) != 0;
        }

        // First published at tick 100: down through ticks 100..102 (two whole ticks after the first), up at 103.
        AssertThat(Down(100)).IsTrue();
        AssertThat(Down(100)).IsTrue();
        AssertThat(Down(101)).IsTrue();
        AssertThat(Down(102)).IsTrue();
        AssertThat(Down(103)).IsFalse();
        AssertThat(ModInputPresses.Any).IsFalse();

        // No world: frames stand in for ticks.
        ModInputPresses.Tap(1, "B", 1);
        AssertThat(Down(0, known: false)).IsTrue();
        AssertThat(Down(0, known: false)).IsTrue();
        AssertThat(Down(0, known: false)).IsFalse();

        // A guest that stops ticking: let go at the ceiling.
        ModInputPresses.Tap(1, "B", 5);
        AssertThat(Down(7, ms: 1000)).IsTrue();
        AssertThat(Down(7, ms: 1000 + ModInputPresses.TapCeilingMs)).IsFalse();
        ModInputPresses.Clear();
    }

    [TestCase]
    public void PressesCrossTheBusAsUiRequests()
    {
        Engine_UiModRequest.Request? press = Engine_UiModRequest.Parse(Fields("op='press',player=2,button='RT',held=200"));
        AssertThat(press is { Op: Engine_UiModRequest.Op.Press, Player: 2, Name: "RT", Slide: 200 }).IsTrue();
        Engine_UiModRequest.Request? tap = Engine_UiModRequest.Parse(Fields("op='tap',player=1,button='A',ticks=3"));
        AssertThat(tap is { Op: Engine_UiModRequest.Op.Tap, Player: 1, Name: "A", Slide: 3 }).IsTrue();
        // A press is one player's pad: player 0 is refused.
        AssertThat(Engine_UiModRequest.Parse(Fields("op='press',player=0,button='A',held=1"))).IsNull();
        AssertThat(Engine_UiModRequest.Parse(Fields("op='tap',player=1,button='',ticks=3"))).IsNull();
    }

    // ---- I.2 Lua to Godot on one machine ---------------------------------------------------------

    [TestCase]
    public void ASignalArrivesExactly()
    {
        string payload = "it's \"quoted\",\nwith = and ; and |";
        List<KeyValuePair<string, object>> fields = Fields($"op='signal',name='quicksave.saved',payload='{B64(payload)}'");
        AssertThat(ModSignals.IsOurs(fields)).IsTrue();
        ModSignals.Request? signal = ModSignals.Parse(fields);
        AssertThat(signal is { Op: ModSignals.Op.Signal, Key: "quicksave.saved" }).IsTrue();
        AssertThat(signal!.Value.Text).IsEqual(payload);
        AssertThat(ModSignals.Split("quicksave.saved.slot")).IsEqual(("quicksave", "saved.slot"));

        AssertThat(ModSignals.Parse(Fields("op='signal',name='nodot',payload=''"))).IsNull();
        AssertThat(ModSignals.Parse(Fields("op='signal',name='m.n',payload='***'"))).IsNull();
        AssertThat(ModSignals.IsOurs(Fields("op='notice',id=1,title='x'"))).IsFalse();
        AssertThat(ModSignals.ValidName(".x")).IsFalse();
        AssertThat(ModSignals.ValidName("m.has space")).IsFalse();
        AssertThat(ModSignals.ValidName("m." + new string('a', ModSignals.MaxNameLength))).IsFalse();
    }

    [TestCase]
    public void APublishedTextReportsOnlyAChange()
    {
        ModSignals.ClearTexts();
        ModSignals.Request? text = ModSignals.Parse(Fields($"op='publish_text',key='hotbar.icon1',text='{B64("Art\\GUI\\icon.tex")}'"));
        AssertThat(text is { Op: ModSignals.Op.PublishText, Key: "hotbar.icon1" }).IsTrue();
        AssertThat(ModSignals.SetText(text!.Value.Key, text.Value.Text, out bool changed)).IsTrue();
        AssertThat(changed).IsTrue();
        AssertThat(ModSignals.Text("hotbar.icon1")).IsEqual("Art\\GUI\\icon.tex");
        AssertThat(ModSignals.SetText("hotbar.icon1", "Art\\GUI\\icon.tex", out changed)).IsTrue();
        AssertThat(changed).IsFalse();
        AssertThat(ModSignals.SetText("hotbar.icon1", new string('x', ModSignals.MaxTextBytes + 1), out _)).IsFalse();
        AssertThat(ModSignals.Text("nobody.here")).IsNull();
        ModSignals.ClearTexts();
    }

    [TestCase]
    public void APublishedNumbersChangeRecordReads()
    {
        AssertThat(ModSignals.TryValueChange(Fields("key='combat.stamina',value=0.75"), out string key, out double value)).IsTrue();
        AssertThat(key).IsEqual("combat.stamina");
        AssertThat(value).IsEqual(0.75);
        AssertThat(ModSignals.TryValueChange(Fields("key='combat.stamina',value=3"), out _, out value)).IsTrue();
        AssertThat(value).IsEqual(3.0);
        AssertThat(ModSignals.TryValueChange(Fields("key='combat.stamina',value=nan"), out _, out value)).IsTrue();
        AssertThat(double.IsNaN(value)).IsTrue();
        AssertThat(ModSignals.TryValueChange(Fields("value=1"), out _, out _)).IsFalse();
    }

    // ---- I.3 menu rows -------------------------------------------------------------------------------

    [TestCase]
    public void RowsLongerThanARecordAssembleFromTheirParts()
    {
        Engine_UiModRows.Request? first = Engine_UiModRows.Parse(Fields("op='menu_rows',player=2,menu='q.choose',seq=7,part=1,parts=2,rows='b|B||0'"));
        AssertThat(first is { Op: Engine_UiModRows.Op.MenuRows, Parts: 2, Part: 1, Seq: 7 }).IsTrue();
        AssertThat(first!.Value.Rows.Count).IsEqual(0);  // parsed only once whole

        // Out of order, split mid-row: the whole text is what the rows parse from.
        AssertThat(Engine_UiModRows.Assemble(2, "q.choose", 7, 1, 2, "B||0")).IsNull();
        string? whole = Engine_UiModRows.Assemble(2, "q.choose", 7, 0, 2, "a|A||0;b|");
        AssertThat(whole).IsEqual("a|A||0;b|B||0");
        AssertThat(Engine_UiModRows.ParseRows(whole!).Select(r => r.Name).ToArray()).IsEqual(new[] { "a", "b" });

        // A newer statement drops an older one left unfinished (a lost record; the lane is FIFO).
        AssertThat(Engine_UiModRows.Assemble(1, "m", 1, 0, 3, "x")).IsNull();
        AssertThat(Engine_UiModRows.Assemble(1, "m", 2, 0, 2, "y")).IsNull();
        AssertThat(Engine_UiModRows.Assemble(1, "m", 2, 1, 2, "z")).IsEqual("yz");

        // One part is the old shape.
        Engine_UiModRows.Request? single = Engine_UiModRows.Parse(Fields("op='menu_rows',player=0,menu='m',rows='a|A||0'"));
        AssertThat(single is { Parts: 1 }).IsTrue();
        AssertThat(single!.Value.Rows.Count).IsEqual(1);
        AssertThat(Engine_UiModRows.Assemble(1, "m", 0, 0, Engine_UiModRows.MaxParts + 1, "x")).IsNull();
    }

    [TestCase]
    public void TheRowsSubjectIsTyped()
    {
        AssertThat(ModRowSubject.Of("Item 3", "Inventory:use:1234")).IsEqual(new ModRowSubject.Subject("item", "0x000004d2", -1));
        AssertThat(ModRowSubject.Of("w", "Inventory:equip:3463155215")).IsEqual(new ModRowSubject.Subject("weapon", "0xce6b8e0f", -1));
        AssertThat(ModRowSubject.Of("AbilityLightningBase", "Ability:learn:7")).IsEqual(new ModRowSubject.Subject("will", string.Empty, 0));
        AssertThat(ModRowSubject.Of("AbilityDeadRisingBase", "Ability:learn:14")).IsEqual(new ModRowSubject.Subject("will", string.Empty, 7));
        AssertThat(ModRowSubject.Of("AbilityPhysiqueBase", "Ability:learn:2").Stated).IsFalse();
        AssertThat(ModRowSubject.Of("Expr", "305419896")).IsEqual(new ModRowSubject.Subject("expression", "0x12345678", -1));
        AssertThat(ModRowSubject.Of("gold", "Mod:lua:my_mod.give:100").Stated).IsFalse();

        // A host list's typed row names, when the command states nothing.
        AssertThat(ModRowSubject.Of(ModRowSubject.ItemRow(0xCE6B8E0Fu), "Mod:lua:q.chosen")).IsEqual(new ModRowSubject.Subject("item", "0xce6b8e0f", -1));
        AssertThat(ModRowSubject.Of(ModRowSubject.ExpressionRow(0x10u), string.Empty)).IsEqual(new ModRowSubject.Subject("expression", "0x00000010", -1));
        AssertThat(ModRowSubject.Of(ModRowSubject.WillRow(4), null)).IsEqual(new ModRowSubject.Subject("will", string.Empty, 4));
        AssertThat(ModRowSubject.Of("will:9", null).Stated).IsFalse();
        AssertThat(ModRowSubject.Of("plain row", string.Empty)).IsEqual(ModRowSubject.Subject.None);
    }

    [TestCase]
    public void TheRowEventOffersItsTypedSubjectOnEverySurface()
    {
        ModApiEvent row = ModApi.Find("mod_menu_row")!;
        AssertThat(string.Join(",", row.Fields.Select(f => f.Name))).IsEqual("menu,row,player,mod,command,button,subject_kind,definition,spell");

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("e.subject_kind = Name(w, 7);", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("subject_kind='%s',definition='%s',spell=%d}) end", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheRowsCapIsLiftedAndACutIsReported()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("char rows[1800]", StringComparison.Ordinal)).IsFalse();
        AssertThat(natives.Contains("lua.LongString(2, REFII_MOD_MENU_ROWS_MAX + 1)", StringComparison.Ordinal)).IsTrue();
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        AssertThat(api.Contains("QuotedField(rows, 1800)", StringComparison.Ordinal)).IsFalse();
        AssertThat(api.Contains("QuotedField(rows, REFII_MOD_MENU_ROWS_MAX + 1)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("refii_bridge_mod_error_post(error, std::strlen(error));", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("',seq=\" + std::to_string(seq) + \",part=\"", StringComparison.Ordinal)).IsTrue();
        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("#define REFII_MOD_MENU_ROWS_MAX 32768", StringComparison.Ordinal)).IsTrue();
        // 20 parts of 1800 hold the limit: Godot's assembly takes as many.
        AssertThat(Engine_UiModRows.MaxParts * 1800 >= 32768).IsTrue();

        string sources = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "ui", "Fable2MenuSources.cs"));
        AssertThat(sources.Contains("Engine_UiModListSources.ExpressionsSource", StringComparison.Ordinal)).IsTrue();
        AssertThat(sources.Contains("Engine_UiModListSources.SpellsKnownSource", StringComparison.Ordinal)).IsTrue();
        string inventory = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "ui", "Engine_UiInventorySource.cs"));
        AssertThat(inventory.Contains("public const string UsableOperation = \"usable\";", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void HostQueriesNeverAnswerAScriptsId()
    {
        AssertThat(ModHostQueries.Take(Fields("id=5,ok=true,count=0"))).IsFalse();
        AssertThat(ModHostQueries.Take(Fields("id=-99999,ok=true"))).IsFalse();  // none pending
    }

    // ---- every surface ------------------------------------------------------------------------------

    [TestCase]
    public void ThePartICallsAreOnEverySurface()
    {
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains(ModApi.PressMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.SignalMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.PlayerTextMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.HostListMeaning, StringComparison.Ordinal)).IsTrue();
        }

        foreach (string call in new[] { "refii_mod_press(", "refii_mod_tap(", "refii_mod_signal(", "refii_mod_publish_text(",
                     "refii_mod_player_text(", "refii_mod_player_save_hash(" })
        {
            AssertThat(native.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage(call).IsTrue();
        }

        foreach (string call in new[] { "Refii.press(", "Refii.tap(", "Refii.signal(", "Refii.publish_text(",
                     "Refii.player_value_text(", "Refii.player_save_hash(", "Refii.player_value_hash(" })
        {
            AssertThat(lua.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage(call).IsTrue();
        }

        foreach (string name in new[] { "press", "tap", "presses", "get_text", "has_text", "publish_text", "player_value",
                     "player_set_text", "player_save_text", "player_save_hash" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name)))
                .OverrideFailureMessage($"'{name}' is not a Godot helper of the catalog").IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        foreach (string declared in new[]
                 {
                     "extern \"C\" int32_t refii_mod_press(int32_t player, const char* button, int32_t held);",
                     "extern \"C\" int32_t refii_mod_tap(int32_t player, const char* button, int32_t ticks);",
                     "extern \"C\" int32_t refii_mod_signal(const char* name, const char* payload);",
                     "extern \"C\" int32_t refii_mod_publish_text(const char* key, const char* text);",
                     "extern \"C\" int32_t refii_mod_player_text(int32_t player, const char* key, char* out, uint32_t capacity);",
                     "extern \"C\" void refii_mod_player_save_hash(int32_t player, const char* key, uint32_t hash);",
                     "#define REFII_MOD_SIGNAL_PAYLOAD_MAX 1024",
                     "#define REFII_MOD_PLAYER_TEXT_MAX 256",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string row in new[]
                 {
                     "{\"press\", &NativePress, 0}", "{\"tap\", &NativeTap, 0}", "{\"signal\", &NativeSignal, 0}",
                     "{\"publish_text\", &NativePublishText, 0}", "{\"player_value_text\", &NativePlayerValueText, 0}",
                     "{\"player_set_text\", &NativePlayerSetText, 0}", "{\"player_save_text\", &NativePlayerSaveText, 0}",
                     "{\"player_value_hash\", &NativePlayerValueHash, 0}", "{\"player_save_hash\", &NativePlayerSaveHash, 0}",
                 })
        {
            AssertThat(natives.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage($"{row} is not registered").IsTrue();
        }

        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        foreach (string op in new[] { "op='press'", "op='tap'", "op='signal'", "op='publish_text'" })
        {
            AssertThat(api.Contains(op, StringComparison.Ordinal)).OverrideFailureMessage($"{op} is never posted").IsTrue();
        }

        // The pad publish ORs the presses after the claims, on both pad paths.
        string input = File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameInput.cs"));
        AssertThat(input.Split("Refii.Mods.ModInput.Press(player, ref state.Buttons, ref state.LeftTrigger, ref state.RightTrigger);").Length - 1).IsEqual(2);

        // The runtime: a published number's change is posted for value_changed; the store keeps texts.
        string bridge = File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "payload_bridge.h"));
        AssertThat(bridge.Contains($"#define REFII_MOD_HOST_VALUE_KIND {RefiiHost.ValueKind}", StringComparison.Ordinal)).IsTrue();
        AssertThat(bridge.Contains("int32_t refii_bridge_player_get_text(", StringComparison.Ordinal)).IsTrue();
        string values = File.ReadAllText(Path.Combine(root, "src", "runtime", "src", "core", "mod_values.cpp"));
        AssertThat(values.Contains("refii_bridge_mod_event_post(REFII_MOD_HOST_VALUE_KIND", StringComparison.Ordinal)).IsTrue();
        string store = File.ReadAllText(Path.Combine(root, "src", "runtime", "src", "core", "player_state.cpp"));
        AssertThat(store.Contains("extern \"C\" void refii_bridge_player_write_text(", StringComparison.Ordinal)).IsTrue();
        AssertThat(store.Contains("constexpr std::string_view kTextSavePrefix = \"t:\";", StringComparison.Ordinal)).IsTrue();
    }

    // ---- wave D: the host additions the ports needed ---------------------------------------------

    /// <summary>Godot's synchronous hash read takes a store text as refii_mod_player_hash does: strtoull base 0, whole, within 32 bits.</summary>
    [TestCase]
    public void TheStoreHashReadsAsTheGuestReadsIt()
    {
        AssertThat(RefiiHost.PlayerHash("0xce6b8e0f")).IsEqual(0xce6b8e0fu);
        AssertThat(RefiiHost.PlayerHash("0XCE6B8E0F")).IsEqual(0xce6b8e0fu);
        AssertThat(RefiiHost.PlayerHash("1234")).IsEqual(1234u);
        AssertThat(RefiiHost.PlayerHash("017")).IsEqual(15u);
        AssertThat(RefiiHost.PlayerHash("0")).IsEqual(0u);
        AssertThat(RefiiHost.PlayerHash("4294967295")).IsEqual(uint.MaxValue);
        AssertThat(RefiiHost.PlayerHash(" 12")).IsEqual(12u);
        AssertThat(RefiiHost.PlayerHash("+0x10")).IsEqual(16u);
        AssertThat(RefiiHost.PlayerHash("-0")).IsEqual(0u);
        foreach (string none in new[] { "", " ", "0x", "0x1ffffffff", "4294967296", "12a", "abc", "08", "0x12 ", "-1", "+" })
        {
            AssertThat(RefiiHost.PlayerHash(none).HasValue).OverrideFailureMessage($"'{none}' read as a hash").IsFalse();
        }

        AssertThat(RefiiHost.PlayerHash(null).HasValue).IsFalse();
    }

    [TestCase]
    public void TheSynchronousTextReadsAndThePerPlayerUsableListAreStated()
    {
        foreach (string name in new[] { "player_text", "player_value_hash", "attack_mode" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name)))
                .OverrideFailureMessage($"'{name}' is not a Godot helper of the catalog").IsTrue();
        }

        AssertThat(ModApi.HostListMeaning.Contains("inventory:usable[:p[:<categories>]]", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.GodotHelpers.Any(h => h.Signature.StartsWith("query inventory P", StringComparison.Ordinal))).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string exports = File.ReadAllText(Path.Combine(root, "src", "hostcore", "src", "host_exports.cpp"));
        AssertThat(exports.Contains("REFII_HOST_ABI_EXPORT int32_t RefiiHostGetPlayerText(int32_t player, const char* key, char* out, uint32_t capacity)", StringComparison.Ordinal)).IsTrue();
        AssertThat(exports.Contains($"RefiiHostBridgeAbiVersion(void) {{ return {Refii.Host.HostBridge.ExpectedAbiVersion}; }}", StringComparison.Ordinal)).IsTrue();

        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("what == \"inventory\"", StringComparison.Ordinal)).IsTrue();
        string source = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "ui", "Engine_UiInventorySource.cs"));
        AssertThat(source.Contains("Engine_UiModListSources.Inventory(usablePlayer)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheAttackModeIsOnEverySurface()
    {
        ModApiEvent changed = ModApi.Find("attack_mode_changed")!;
        AssertThat(changed.Kind).IsEqual(105);
        AssertThat(string.Join(",", changed.Fields.Select(f => f.Name))).IsEqual("player,mode");
        AssertThat(changed.Cancellable).IsFalse();
        AssertThat(changed.Fields.Any(f => f.Writable)).IsFalse();
        AssertThat(RefiiHost.SignalNames.Contains("attack_mode_changed")).IsTrue();
        AssertThat(ModApi.Constants.Single(c => c.Name == "ATTACK_MODE_MELEE").Value).IsEqual(1);
        AssertThat(ModApi.Constants.Single(c => c.Name == "ATTACK_MODE_RANGED").Value).IsEqual(2);
        AssertThat(ModApi.Constants.Single(c => c.Name == "ATTACK_MODE_MAGIC").Value).IsEqual(3);
        AssertThat(ModApi.Helpers.Any(h => h.Signature.Contains("refii_mod_attack_mode(", StringComparison.Ordinal))).IsTrue();
        AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.Contains("Refii.attack_mode(", StringComparison.Ordinal))).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_attack_mode(int32_t player);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_ATTACK_MODE_MAGIC 3", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"attack_mode\", &NativeAttackMode, 0}", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("op == \"attack_mode\"", StringComparison.Ordinal)).IsTrue();
        string input = File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameInput.KeyboardMouse.cs"));
        AssertThat(input.Contains("HostBridge.PostModCommand($\"attack_mode {player} {mode}\");", StringComparison.Ordinal)).IsTrue();
    }
}
