namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Mods;

/// <summary>
/// The GDScript bridge (Engine_RefiiHost) is held to the catalog: one signal per event, named by
/// its Lua name, in kind order; and the bus's field text parses to the typed Dictionary a script
/// reads; and every short form the catalog and the guide write is a method a script can call.
/// </summary>
[TestSuite]
public class RefiiHostTests
{
    [TestCase]
    public void SignalListMatchesTheCatalog()
    {
        AssertThat(RefiiHost.SignalNames.Length).IsEqual(ModApi.Events.Count);
        for (int i = 0; i < ModApi.Events.Count; i++)
        {
            AssertThat(RefiiHost.SignalNames[i]).IsEqual(ModApi.Events[i].LuaName);
        }

        AssertThat(RefiiHost.SignalNames.Distinct().Count()).IsEqual(RefiiHost.SignalNames.Length);
        AssertThat(RefiiHost.SignalNames.Contains(RefiiHost.ModEventSignal)).IsFalse();
    }

    // The node's signals and methods are checked in the editor, not here: a Godot Node cannot be
    // constructed outside the engine (the test host crashes on the native call).

    [TestCase]
    public void FieldsParseToTypedValues()
    {
        var e = new Dictionary<string, object>(RefiiHost.ParseFieldsPlain(
            "victim=123,victim_player=1,attacker=0,damage=4.5,name='ALLOW_ACTION_INTERRUPT',unblockable=false,big=4000000000,tiny=1e-06,yes=true"));

        AssertThat(e["victim"]).IsEqual(123L);
        AssertThat(e["victim_player"]).IsEqual(1L);
        AssertThat(e["attacker"]).IsEqual(0L);
        AssertThat(e["damage"]).IsEqual(4.5);
        AssertThat(e["name"]).IsEqual("ALLOW_ACTION_INTERRUPT");
        AssertThat(e["unblockable"]).IsEqual(false);
        AssertThat(e["yes"]).IsEqual(true);
        AssertThat(e["big"]).IsEqual(4000000000L);
        AssertThat(e["tiny"] is double).IsTrue();
        AssertThat(e.Count).IsEqual(9);
    }

    [TestCase]
    public void FieldsParseTheEmptyAndTheOddCases()
    {
        AssertThat(RefiiHost.ParseFieldsPlain(string.Empty).Count).IsEqual(0);
        var e = new Dictionary<string, object>(RefiiHost.ParseFieldsPlain("seconds=0.0333,name='',k=-7"));
        AssertThat((double)e["seconds"] > 0.03).IsTrue();
        AssertThat(e["name"]).IsEqual(string.Empty);
        AssertThat(e["k"]).IsEqual(-7L);
    }

    [TestCase]
    public void GodotScriptPathsAreNotOverlays()
    {
        AssertThat(ModGodot.IsGodotScriptPath("godot/hud.gd")).IsTrue();
        AssertThat(ModGodot.IsGodotScriptPath("Godot/sub/hud.GD")).IsTrue();
        AssertThat(ModGodot.IsGodotScriptPath("/godot/hud.gd")).IsTrue();
        AssertThat(ModGodot.IsGodotScriptPath("godot/hud.png")).IsFalse();
        AssertThat(ModGodot.IsGodotScriptPath("mod/init.lua")).IsFalse();
        AssertThat(ModGodot.IsGodotScriptPath("hud.gd")).IsFalse();
    }

    // use_item posts the Items page's own action, by the record's hash in decimal (the pump's
    // DispatchInventoryAction reads it with strtoul base 10).
    [TestCase]
    public void UseItemPostsTheInventoryUseAction()
    {
        AssertThat(RefiiHost.UseItemAction("0xCE6B8E0F")).IsEqual("Inventory:use:3463155215");
        AssertThat(RefiiHost.UseItemAction("0xce6b8e0f")).IsEqual("Inventory:use:3463155215");
        AssertThat(RefiiHost.UseItemAction("3463155215")).IsEqual("Inventory:use:3463155215");
        AssertThat(RefiiHost.UseItemAction(" 0x1 ")).IsEqual("Inventory:use:1");
    }

    [TestCase]
    public void UseItemRefusesWhatIsNotARecord()
    {
        AssertThat(RefiiHost.UseItemAction(string.Empty)).IsNull();
        AssertThat(RefiiHost.UseItemAction("0x0")).IsNull();
        AssertThat(RefiiHost.UseItemAction("0")).IsNull();
        AssertThat(RefiiHost.UseItemAction("potion")).IsNull();
        AssertThat(RefiiHost.UseItemAction("0x1FFFFFFFF")).IsNull();
        AssertThat(RefiiHost.UseItemAction("-5")).IsNull();
    }

    // read: only a verb that changes nothing crosses, as a query's words without the id; the answer
    // parses as ask's would; outside a verdict or for another verb, ok false and an error naming ask.
    [TestCase]
    public void AVerdictReadCrossesOnlyAReadAndAnswersAsAsk()
    {
        AssertThat(ModVerdictRead.Line("sprinting", new[] { "1" })).IsEqual("sprinting 1");
        AssertThat(ModVerdictRead.Line("record_string", new[] { "0x00000001", "a b" })).IsEqual("record_string 0x00000001 a_b");
        AssertThat(ModVerdictRead.Line("time_of_day", Array.Empty<string>())).IsEqual("time_of_day");
        AssertThat(ModVerdictRead.Line("spawn", new[] { "0x1", "0", "0", "0" })).IsNull();
        AssertThat(ModVerdictRead.Line("lua_int", new[] { "1", "0", "1" })).IsNull();
        AssertThat(ModVerdictRead.Line("give_gold", new[] { "1", "5" })).IsNull();
        AssertThat(ModVerdictRead.Line(string.Empty, Array.Empty<string>())).IsNull();

        var answered = new Dictionary<string, object>(ModVerdictRead.Fields(20, "ok=true,sprinting=true", "sprinting"));
        AssertThat(answered["ok"]).IsEqual(true);
        AssertThat(answered["sprinting"]).IsEqual(true);
        AssertThat(answered.ContainsKey("error")).IsFalse();
        var unanswered = new Dictionary<string, object>(ModVerdictRead.Fields(33, "ok=false,x=0,y=0,z=0", "velocity"));
        AssertThat(unanswered["ok"]).IsEqual(false);
        AssertThat(unanswered["x"]).IsEqual(0L);

        var outside = new Dictionary<string, object>(ModVerdictRead.Fields(ModVerdictRead.Outside, string.Empty, "health"));
        AssertThat(outside["ok"]).IsEqual(false);
        AssertThat(((string)outside["error"]).Contains("on_verdict", StringComparison.Ordinal)).IsTrue();
        AssertThat(((string)outside["error"]).Contains("ask(\"health\"", StringComparison.Ordinal)).IsTrue();
        var refused = new Dictionary<string, object>(ModVerdictRead.Fields(ModVerdictRead.Refused, string.Empty, "spawn"));
        AssertThat(refused["ok"]).IsEqual(false);
        AssertThat(((string)refused["error"]).Contains("changes nothing", StringComparison.Ordinal)).IsTrue();
        AssertThat(new Dictionary<string, object>(ModVerdictRead.Fields(0, string.Empty, "health"))["ok"]).IsEqual(false);
    }

    // Godot's script bridge registers no C# default and dispatches by name and argument count:
    // host.notice("T", "x") reaches a method only if one takes exactly two arguments. A default
    // here would read as callable from C# and fail from GDScript ("Nonexistent function").
    [TestCase]
    public void NoMemberTakesACSharpDefault()
    {
        foreach (MethodInfo method in typeof(RefiiHost).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                AssertThat(parameter.IsOptional)
                    .OverrideFailureMessage($"RefiiHost.{method.Name}'s '{parameter.Name}' has a C# default, which a script cannot leave off: add an overload")
                    .IsFalse();
            }
        }
    }

    [TestCase]
    public void TheShortFormsAreOverloads()
    {
        AssertThat(Arities("notice")).IsEqual(new[] { 2, 3, 4 });
        AssertThat(Arities("in_cutscene")).IsEqual(new[] { 0, 1, 2 });
        AssertThat(Arities("cutscene_kinds")).IsEqual(new[] { 0, 1 });
        AssertThat(Arities("message_box")).IsEqual(new[] { 3, 4 });
        AssertThat(Arities("menu_box")).IsEqual(new[] { 3, 4, 5 });
        AssertThat(Arities("mark")).IsEqual(new[] { 2, 3, 4, 5 });
        AssertThat(Arities("world_meter")).IsEqual(new[] { 3, 4, 5 });
        AssertThat(Arities("mark_meter")).IsEqual(new[] { 3, 4 });
        AssertThat(Arities("add_item")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("fade")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("meter")).IsEqual(new[] { 4, 5, 6 });
        AssertThat(Arities("light")).IsEqual(new[] { 4, 5 });
        AssertThat(Arities("open_menu")).IsEqual(new[] { 1, 2 });
        AssertThat(Arities("open_screen")).IsEqual(new[] { 1, 2 });
        AssertThat(Arities("set_menu_rows")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("send_to_partner")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("entity_tint")).IsEqual(new[] { 4, 5 });
        AssertThat(Arities("lock_target")).IsEqual(new[] { 1, 2 });
        AssertThat(Arities("wheel_switches_target")).IsEqual(new[] { 1, 2 });
        AssertThat(Arities("apply_status")).IsEqual(new[] { 3, 4, 5 });
        AssertThat(Arities("knockback")).IsEqual(new[] { 4, 5 });
        AssertThat(Arities("play_sound")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("play_sound_at")).IsEqual(new[] { 4, 5 });
        AssertThat(Arities("stop_sound")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("play_animation")).IsEqual(new[] { 2, 3, 4, 5 });
        AssertThat(Arities("npc_follow")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("set_weather")).IsEqual(new[] { 1, 2, 3 });

        // The answering forms a Godot mod needs where Lua reads the call's result, and the row verb with its row.
        AssertThat(Arities("charge_spell")).IsEqual(new[] { 3, 4 });
        AssertThat(Arities("perform_expression")).IsEqual(new[] { 2, 3, 4, 5 });
        AssertThat(Arities("register_command")).IsEqual(new[] { 2, 3 });
        AssertThat(Arities("player_here")).IsEqual(new[] { 1 });
        AssertThat(typeof(RefiiHost).GetMethod("charge_spell", new[] { typeof(int), typeof(int), typeof(bool), typeof(Callable) })!.ReturnType).IsEqual(typeof(bool));
        AssertThat(typeof(RefiiHost).GetMethod("player_here", new[] { typeof(int) })!.ReturnType).IsEqual(typeof(bool));
    }

    // Each Godot helper signature, "name(a, b = 0) -> r / other(c)": every argument count from the
    // required ones to all of them is a method of that name.
    [TestCase]
    public void EveryCatalogShortFormIsCallable()
    {
        foreach (ModApiHelper helper in ModApi.GodotHelpers)
        {
            foreach (string part in helper.Signature.Split(" / "))
            {
                Match call = CallForm.Match(part.Split(" ->")[0].Trim());
                if (call.Success && ModApi.GodotMethodNames(part).Any())
                {
                    AssertCallable(call, $"the catalog's '{helper.Signature}'");
                }
            }
        }
    }

    // The guide writes the Godot calls as "name(a, b = 0)" or "host.name(a)" in its text and
    // tables (Lua's Refii.name and native refii_mod_name are other surfaces): each must resolve.
    [TestCase]
    public void EveryGuideShortFormIsCallable()
    {
        string? folder = GuideFolder();
        if (folder is null)
        {
            return;
        }

        var names = typeof(RefiiHost).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToHashSet();
        int seen = 0;
        foreach (string path in Directory.GetFiles(folder, "*.md"))
        {
            string text = Regex.Replace(File.ReadAllText(path), "```.*?```", string.Empty, RegexOptions.Singleline);
            foreach (Match span in Regex.Matches(text, "`([^`\n]+)`"))
            {
                foreach (Match call in GuideCall.Matches(span.Groups[1].Value))
                {
                    // "name(...)" names a call without stating its arguments.
                    if (names.Contains(call.Groups["name"].Value) && !call.Groups["args"].Value.Contains("..."))
                    {
                        AssertCallable(call, $"{Path.GetFileName(path)}'s `{span.Groups[1].Value}`");
                        seen++;
                    }
                }
            }
        }

        AssertThat(seen > 40).OverrideFailureMessage($"only {seen} Godot calls found in the guide").IsTrue();
    }

    private static readonly Regex CallForm = new(@"^(?<name>\w+)\((?<args>[^()\[\]{}]*)\)$");

    // A call not reached through another surface's prefix (Refii., a C++ scope or member): bare or host.
    private static readonly Regex GuideCall = new(@"(?<![\w.:>])(?:host\.)?(?<name>[a-z_][a-z0-9_]*)\((?<args>[^()\[\]{}`]*)\)");

    private static int[] Arities(string name) =>
        typeof(RefiiHost).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name).Select(m => m.GetParameters().Length).OrderBy(n => n).ToArray();

    private static void AssertCallable(Match call, string where)
    {
        string name = call.Groups["name"].Value;
        string[] args = call.Groups["args"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        int required = args.Count(a => !a.Contains('='));
        int[] arities = Arities(name);
        for (int n = required; n <= args.Length; n++)
        {
            AssertThat(arities.Contains(n))
                .OverrideFailureMessage($"{where}: a script calling {name} with {n} argument(s) finds no method (RefiiHost has {string.Join(", ", arities)})")
                .IsTrue();
        }
    }

    private static string? GuideFolder()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return Path.Combine(at, "Godot", ModGuide.ProjectFolder);
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }
}
