namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// THE PARITY RULE (user 2026-11-02): Lua and Godot have the same access. Every entry of each
/// surface — read from the CODE, not the catalog (the Lua natives table, the preamble's functions and
/// fields, the runtime's own events; RefiiHost's script methods and signals, the guest's query verbs) —
/// stands in a row of <see cref="ModApi.Twins"/> with its twin on the other surface, or in a row that
/// says why the other side has none. Events: both surfaces hear every catalog event, and refuse and
/// write it alike. No Godot runtime object is made here.
/// </summary>
[TestSuite]
public class ModParityTests
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

    private static readonly Regex Name = new("^[a-z][a-z0-9_]*$");

    private static string Preamble => ModLua.RuntimePreamble(Array.Empty<(string, float)>()) + ModLua.FrontendNatives;

    /// <summary>The runtime's own events (<c>Refii._local_events</c>): Refii.on takes them, the bus never raises them.</summary>
    private static HashSet<string> LuaLocalEvents()
    {
        Match table = Regex.Match(ModLua.InfrastructureRuntime(), @"Refii\._local_events = \{([^}]*)\}");
        AssertThat(table.Success).IsTrue();
        return Regex.Matches(table.Groups[1].Value, @"(\w+) = true").Select(m => m.Groups[1].Value).ToHashSet();
    }

    /// <summary>
    /// The Lua surface: every public native (mod_lua_natives.cpp's table), every function and field the
    /// preamble defines on Refii, every Refii name the catalog's Lua section writes (the core chunk's
    /// co-op calls), and the runtime's own events as event:name.
    /// </summary>
    private static HashSet<string> LuaSurface(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string natives = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp");
        foreach (Match m in Regex.Matches(natives, "\\{\"([a-z][a-z0-9_]*)\", &Native"))
        {
            names.Add(m.Groups[1].Value);
        }

        AssertThat(names.Count > 150).OverrideFailureMessage($"only {names.Count} Lua natives read").IsTrue();
        foreach (Match m in Regex.Matches(Preamble, @"function Refii\.([a-z][a-z0-9_]*)\("))
        {
            names.Add(m.Groups[1].Value);
        }

        foreach (Match m in Regex.Matches(Preamble, @"(?m)^Refii\.([a-z][a-z0-9_]*) = "))
        {
            names.Add(m.Groups[1].Value);
        }

        foreach (ModApiHelper helper in ModApi.LuaHelpers)
        {
            foreach (Match m in Regex.Matches(helper.Signature, @"Refii\.([a-z][a-z0-9_]*)"))
            {
                names.Add(m.Groups[1].Value);
            }
        }

        foreach (string e in LuaLocalEvents())
        {
            names.Add("event:" + e);
        }

        return names;
    }

    private static HashSet<string> GodotMethods() =>
        typeof(RefiiHost).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && Name.IsMatch(m.Name))
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> GodotSignals() =>
        typeof(RefiiHost).GetNestedTypes(BindingFlags.Public)
            .Where(t => t.Name.EndsWith("EventHandler", StringComparison.Ordinal) && typeof(Delegate).IsAssignableFrom(t))
            .Select(t => t.Name[..^"EventHandler".Length]).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> QueryVerbs(string root) =>
        Regex.Matches(Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"), "what == \"([a-z_]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The Godot surface: RefiiHost's script methods, its signals that are not a catalog event's (those
    /// are held to the catalog by name), and every query verb the guest answers.
    /// </summary>
    private static HashSet<string> GodotSurface(string root)
    {
        var events = ModApi.Events.Select(e => e.LuaName).ToHashSet(StringComparer.Ordinal);
        var names = GodotMethods();
        AssertThat(names.Count > 150 && names.Contains("on_verdict")).OverrideFailureMessage($"only {names.Count} RefiiHost methods read").IsTrue();
        AssertThat(GodotSignals().Contains("mod_signal") && GodotSignals().Contains("hit")).OverrideFailureMessage("RefiiHost's signals not read").IsTrue();
        AssertThat(QueryVerbs(root).Count > 60).OverrideFailureMessage("the query verbs not read").IsTrue();
        names.UnionWith(GodotSignals().Where(s => !events.Contains(s)).Select(s => "signal:" + s));
        names.UnionWith(QueryVerbs(root).Select(v => "query:" + v));
        return names;
    }

    private static string Missing(IEnumerable<string> names) => string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal));

    [TestCase]
    public void EveryLuaEntryHasAGodotTwin()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        var covered = ModApi.Twins.SelectMany(t => t.LuaTokens).ToHashSet(StringComparer.Ordinal);
        List<string> alone = LuaSurface(root).Where(n => !covered.Contains(n)).ToList();
        AssertThat(alone.Count == 0)
            .OverrideFailureMessage($"Lua entries with no row in ModApi.Twins (add the Godot twin, or a row saying why Godot has none): {Missing(alone)}")
            .IsTrue();
    }

    [TestCase]
    public void EveryGodotEntryHasALuaTwin()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        var covered = ModApi.Twins.SelectMany(t => t.GodotTokens).ToHashSet(StringComparer.Ordinal);
        List<string> alone = GodotSurface(root).Where(n => !covered.Contains(n)).ToList();
        AssertThat(alone.Count == 0)
            .OverrideFailureMessage($"Godot entries with no row in ModApi.Twins (add the Lua twin, or a row saying why Lua has none): {Missing(alone)}")
            .IsTrue();
    }

    [TestCase]
    public void EveryRowNamesWhatExistsAndSaysWhyWhenOneSided()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        HashSet<string> lua = LuaSurface(root);
        HashSet<string> methods = GodotMethods();
        HashSet<string> signals = GodotSignals();
        HashSet<string> verbs = QueryVerbs(root);
        var luaEvents = ModApi.Events.Select(e => e.LuaName).Concat(LuaLocalEvents()).ToHashSet(StringComparer.Ordinal);
        string luaCatalog = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godotCatalog = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        var stale = new List<string>();
        foreach (ModApiTwin twin in ModApi.Twins)
        {
            AssertThat(twin.Exempt == (twin.Why.Length != 0))
                .OverrideFailureMessage($"'{twin.Lua}' / '{twin.Godot}': a one-sided row says why, a two-sided row needs no reason")
                .IsTrue();
            AssertThat(twin.Lua.Length != 0 || twin.Godot.Length != 0).IsTrue();
            AssertThat(twin.Why.Contains('|')).IsFalse();
            foreach (string token in twin.LuaTokens)
            {
                bool exists =
                    token.StartsWith("event:", StringComparison.Ordinal) ? luaEvents.Contains(token[6..]) :
                    token.StartsWith("ask_host:", StringComparison.Ordinal) ? ModApi.HostCalls.Contains(token[9..]) && lua.Contains("ask_host") :
                    Name.IsMatch(token) && !ModApi.LuaGlobals.Contains(token) ? lua.Contains(token) :
                    luaCatalog.Contains(token, StringComparison.Ordinal);
                if (!exists)
                {
                    stale.Add("lua " + token);
                }
            }

            foreach (string token in twin.GodotTokens)
            {
                bool exists =
                    token.StartsWith("query:", StringComparison.Ordinal) ? verbs.Contains(token[6..]) :
                    token.StartsWith("signal:", StringComparison.Ordinal) ? signals.Contains(token[7..]) :
                    Name.IsMatch(token) ? methods.Contains(token) :
                    godotCatalog.Contains(token, StringComparison.Ordinal);
                if (!exists)
                {
                    stale.Add("godot " + token);
                }
            }
        }

        AssertThat(stale.Count == 0).OverrideFailureMessage($"ModApi.Twins names what neither surface has: {Missing(stale)}").IsTrue();
    }

    [TestCase]
    public void TheCatalogDocumentsEveryEntryOfBothSurfaces()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        var godotNames = ModApi.GodotHelpers.SelectMany(h => ModApi.GodotMethodNames(h.Signature)).ToHashSet(StringComparer.Ordinal);
        List<string> undocumented = GodotMethods().Where(m => !godotNames.Contains(m)).ToList();
        AssertThat(undocumented.Count == 0)
            .OverrideFailureMessage($"RefiiHost methods missing from ModApi.GodotHelpers: {Missing(undocumented)}")
            .IsTrue();

        string godotCatalog = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        List<string> unwritten = QueryVerbs(root).Where(v => !Regex.IsMatch(godotCatalog, @"\b" + v + @"\b")).ToList();
        AssertThat(unwritten.Count == 0).OverrideFailureMessage($"query verbs missing from ModApi.GodotHelpers: {Missing(unwritten)}").IsTrue();

        string luaCatalog = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        List<string> luaUndocumented = LuaSurface(root)
            .Where(n => n.StartsWith("event:", StringComparison.Ordinal)
                ? !luaCatalog.Contains(n[6..] + " — Refii.on(", StringComparison.Ordinal)
                : !Regex.IsMatch(luaCatalog, @"Refii\." + n + @"\b"))
            .ToList();
        AssertThat(luaUndocumented.Count == 0)
            .OverrideFailureMessage($"Lua entries missing from ModApi.LuaHelpers: {Missing(luaUndocumented)}")
            .IsTrue();
    }

    [TestCase]
    public void LuaAsksTheHostForEveryReadGodotMakesAtTheCall()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        HashSet<string> methods = GodotMethods();
        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        var asked = ModApi.Twins.SelectMany(t => t.LuaTokens).Where(t => t.StartsWith("ask_host:", StringComparison.Ordinal))
            .Select(t => t[9..]).ToHashSet(StringComparer.Ordinal);
        foreach (string call in ModApi.HostCalls)
        {
            AssertThat(methods.Contains(call)).OverrideFailureMessage($"host call '{call}' is no RefiiHost method").IsTrue();
            AssertThat(host.Contains($"case \"{call}\":", StringComparison.Ordinal))
                .OverrideFailureMessage($"RefiiHost.HostCall does not answer '{call}'").IsTrue();
            // A read is asked by name (ask_host:x); an action is a Lua function of the same name calling ask_host.
            bool action = ModLua.LaneRuntime.Contains($"Refii.ask_host('{call}'", StringComparison.Ordinal);
            AssertThat(asked.Contains(call) || action)
                .OverrideFailureMessage($"host call '{call}' has no Lua form (ask_host:{call} in ModApi.Twins, or a Refii.{call} that asks)").IsTrue();
        }
    }

    [TestCase]
    public void EveryEventIsHeardRefusedAndWrittenAlikeOnBothSurfaces()
    {
        // Heard: Lua's Refii.on names (the preamble's kinds table) and Godot's signals are the catalog's.
        string preamble = ModLua.RuntimePreamble(Array.Empty<(string, float)>());
        foreach (ModApiEvent e in ModApi.Events)
        {
            AssertThat(preamble.Contains($"{e.LuaName}={e.Kind},", StringComparison.Ordinal)).OverrideFailureMessage("lua " + e.LuaName).IsTrue();
        }

        AssertThat(string.Join(",", RefiiHost.SignalNames)).IsEqual(string.Join(",", ModApi.Events.Select(e => e.LuaName)));

        // Written: the same catalog fields go back from both (Lua's _writable table, Godot's verdict loop).
        foreach (IGrouping<int, (int Kind, string Field)> kind in ModApi.WritableFields.GroupBy(w => w.Kind))
        {
            string row = $"[{kind.Key}]={{" + string.Concat(kind.Select(w => $"'{w.Field}',")) + "}";
            AssertThat(preamble.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage("lua writable " + row).IsTrue();
        }

        // Refused: a Lua handler's return true or e.cancel = true, as a verdict's.
        AssertThat(preamble.Contains("elseif cancel == true or e.cancel == true then keep = false end", StringComparison.Ordinal)).IsTrue();

        // The runtime's own events have a Godot signal of the same name.
        HashSet<string> signals = GodotSignals();
        foreach (string local in LuaLocalEvents())
        {
            AssertThat(signals.Contains(local)).OverrideFailureMessage($"Lua's own event '{local}' has no Godot signal").IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        AssertThat(host.Contains("ModApi.Events[kind].Fields.Where(f => f.Writable && f.Name != \"cancel\")", StringComparison.Ordinal)).IsTrue();
        AssertThat(host.Contains("if (result.VariantType == Variant.Type.Bool && result.AsBool())", StringComparison.Ordinal)).IsTrue();
        AssertThat(host.Contains("e.TryGetValue(\"cancel\", out Variant cancelled)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheNamedNumbersAreOneListOnBothSurfaces()
    {
        string preamble = ModLua.RuntimePreamble(Array.Empty<(string, float)>());
        foreach ((string name, int value) in ModApi.NamedNumbers)
        {
            AssertThat(preamble.Contains($"Refii.{name} = {value}", StringComparison.Ordinal)).OverrideFailureMessage(name).IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        AssertThat(host.Contains("foreach ((string name, int value) in ModApi.NamedNumbers)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheCallsReferenceShowsEachRowSideBySide()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string doc = Read(root, "Godot", "docs", "modding", "reference-calls.md");
        List<string> absent = ModApi.Twins.Select(ModApi.TwinMarkdown).Where(row => !doc.Contains(row, StringComparison.Ordinal)).ToList();
        AssertThat(absent.Count == 0)
            .OverrideFailureMessage("reference-calls.md's parity table lacks:\n" + string.Join("\n", absent))
            .IsTrue();
        AssertThat(Read(root, "Godot", "docs", "modding", "handles-and-players.md").Contains("## Lua and Godot have the same access", StringComparison.Ordinal)).IsTrue();
        AssertThat(Read(root, "Godot", "docs", "modding", "events.md").Contains("### Lua and Godot alike", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A Godot verdict reads at the call as a Lua handler does (RefiiHost.read): the verbs it may read are
    /// mod_commands.cpp's kReadOnlyVerbs, the catalog's VerdictReads in the same order — every query verb
    /// but the two that change something — the result codes are payload_bridge.h's, and both sides refuse
    /// a read outside the verdict before anything runs.
    /// </summary>
    [TestCase]
    public void AVerdictReadsTheQueryVerbsThatChangeNothing()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string commands = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp");
        Match table = Regex.Match(commands, @"kReadOnlyVerbs\[\] = \{([^}]*)\}");
        AssertThat(table.Success).IsTrue();
        List<string> guest = Regex.Matches(table.Groups[1].Value, "\"([a-z_]+)\"").Select(m => m.Groups[1].Value).ToList();
        AssertThat(guest).IsEqual(ModApi.VerdictReads.Select(r => r.Verb).ToList());
        AssertThat(guest.Distinct().Count()).IsEqual(guest.Count);
        AssertThat(QueryVerbs(root).Except(guest).OrderBy(v => v, StringComparer.Ordinal).ToList()).IsEqual(new List<string> { "lua_int", "spawn" });
        AssertThat(guest.All(QueryVerbs(root).Contains)).IsTrue();
        AssertThat(ModApi.Twins.Count(t => t.GodotTokens.Contains("read")) == 1 && ModApi.Twins.Single(t => t.GodotTokens.Contains("read")).Lua == ModApi.VerdictReadLua).IsTrue();

        string bridge = Read(root, "src", "runtime", "include", "core", "payload_bridge.h");
        AssertThat(bridge.Contains($"#define REFII_MOD_READ_OUTSIDE ({ModVerdictRead.Outside})", StringComparison.Ordinal)).IsTrue();
        AssertThat(bridge.Contains($"#define REFII_MOD_READ_REFUSED ({ModVerdictRead.Refused})", StringComparison.Ordinal)).IsTrue();
        string exports = Read(root, "src", "hostcore", "src", "host_exports.cpp");
        AssertThat(exports.Contains("REFII_HOST_ABI_EXPORT int32_t RefiiHostModVerdictRead(const char* line, size_t line_length, char* out, size_t capacity)", StringComparison.Ordinal)).IsTrue();
        AssertThat(exports.Contains($"RefiiHostBridgeAbiVersion(void) {{ return {Refii.Host.HostBridge.ExpectedAbiVersion}; }}", StringComparison.Ordinal)).IsTrue();
        AssertThat(Read(root, "Godot", "src", "host", "HostBridge.cs").Contains("private static extern int RefiiHostModVerdictRead(byte[] line, UIntPtr lineLength, [Out] byte[] output, UIntPtr capacity);", StringComparison.Ordinal)).IsTrue();

        // The payload answers only on the thread running the verdict, while it runs; the host only inside its handlers.
        string events = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp");
        AssertThat(Regex.IsMatch(events, @"t_verdict_event = static_cast<Event\*>\(event\);\s*const int32_t length = handler\(.*\);\s*t_verdict_event = nullptr;")).IsTrue();
        AssertThat(events.Contains("thread_local Event* t_verdict_event = nullptr;", StringComparison.Ordinal)).IsTrue();
        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        AssertThat(host.Contains("[ThreadStatic]\n\tprivate static bool _inVerdict;", StringComparison.Ordinal) ||
            host.Contains("[ThreadStatic]\r\n\tprivate static bool _inVerdict;", StringComparison.Ordinal)).IsTrue();
        AssertThat(host.Contains("if (!_inVerdict)", StringComparison.Ordinal)).IsTrue();
    }

    // ---- the lane and the runtime's Godot twins, pure ---------------------------------------------

    [TestCase]
    public void TheLaneWritesLuaLiteralsAndReadsLuaValues()
    {
        AssertThat(ModLuaLane.Literal(null)).IsEqual("nil");
        AssertThat(ModLuaLane.Literal(true)).IsEqual("true");
        AssertThat(ModLuaLane.Literal(3L)).IsEqual("3");
        AssertThat(ModLuaLane.Literal(1.5)).IsEqual("1.5");
        AssertThat(ModLuaLane.Literal(double.NaN)).IsEqual("0");
        AssertThat(ModLuaLane.Literal("a\"b\\c\nd\u0001")).IsEqual("\"a\\\"b\\\\c\\nd\\001\"");
        AssertThat(ModLuaLane.Literal(new List<object?> { 1L, "x", null })).IsEqual("{1,\"x\",nil,}");
        AssertThat(ModLuaLane.Literal(new SortedDictionary<string, object?> { ["a"] = 1L, ["b"] = new List<object?> { false } }))
            .IsEqual("{[\"a\"]=1,[\"b\"]={false,},}");

        AssertThat(ModLuaLane.Decode("n2.5")).IsEqual(2.5);
        AssertThat(ModLuaLane.Decode("b1")).IsEqual(true);
        AssertThat(ModLuaLane.Decode("b0")).IsEqual(false);
        AssertThat(ModLuaLane.Decode("sline\\nnext\\\\")).IsEqual("line\nnext\\");
        AssertThat(ModLuaLane.Decode("-")).IsNull();

        ModLuaLane.Request? ask = ModLuaLane.ParseAsk("4\ninput_claimed\nn2\nsattack\n-\n-");
        AssertThat(ask).IsNotNull();
        AssertThat(ask!.Value.Id).IsEqual(4L);
        AssertThat(ask.Value.What).IsEqual("input_claimed");
        AssertThat(ask.Value.Args[0]).IsEqual(2.0);
        AssertThat(ask.Value.Args[1]).IsEqual("attack");
        AssertThat(ModLuaLane.ParseAsk("x\nfade_state")).IsNull();

        ModLuaLane.Reply? reply = ModLuaLane.ParseAnswer("9\nb1\nn17\n-\n-\n-\n-");
        AssertThat(reply!.Value.Ok).IsTrue();
        AssertThat(reply.Value.Values.Length).IsEqual(1);
        AssertThat(reply.Value.Values[0]).IsEqual(17.0);
        AssertThat(ModLuaLane.ParseAnswer("0\nb1")).IsNull();

        AssertThat(ModLuaLane.ReplyChunk(3, true)).IsEqual("if Refii and Refii._host_reply then Refii._host_reply(3, true) end");
        AssertThat(ModLuaLane.SignalChunk("my_mod.ping", "a b")).IsEqual(
            "if Refii and Refii._godot_signal then Refii._godot_signal(\"my_mod.ping\", \"a b\") end");
        AssertThat(ModLuaLane.CallChunk(5, "Refii.pool_spend(1, \"k\", 2)\n")).IsEqual(
            "if Refii and Refii._godot_call then Refii._godot_call(5, function() return Refii.pool_spend(1, \"k\", 2)  end) end");
        AssertThat(ModLuaLane.ValuesChunk(new[] { ("m.t", (object)"hi", true), ("m.n", (object)2.0, false) }, true)).IsEqual(
            "if Refii and Refii._host_values then Refii._host_values({{\"m.t\",\"hi\",true},{\"m.n\",2,false},}, true) end");
        AssertThat(ModLuaLane.PoolChunk(2, "m.stamina", 100, 0, 12.5, 1, string.Empty)).IsEqual(
            "if Refii and Refii.pool then Refii.pool(2, \"m.stamina\", {max=100,floor=0,regen=12.5,delay=1}) end");
    }

    [TestCase]
    public void TheLaneChunksAndTheRuntimeCompile()
    {
        string chunk = ModLua.RuntimePreamble(Array.Empty<(string, float)>()) + "\n" +
            ModLuaLane.ReplyChunk(1, new List<object?> { 1L, "a" }) + "\n" +
            ModLuaLane.CallChunk(2, "Refii.now()") + "\n" +
            ModLuaLane.SignalChunk("m.s", "x\ny") + "\n" +
            ModLuaLane.ValuesChunk(new[] { ("m.t", (object)"t", true) }, false) + "\n" +
            ModLuaLane.PoolChunk(1, "m.p", 10, -5, 1, 0.5, "m.p_max") + "\n";
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(chunk, "@lane", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;
        }

        AssertThat(ok).OverrideFailureMessage("the lane's chunks do not compile: " + error).IsTrue();
    }

    [TestCase]
    public void GodotsClockKeepsLuasRule()
    {
        var clock = new ModClock<string>();
        AssertThat(clock.After(1.0, "a").Equals(1L)).IsTrue();
        AssertThat(clock.After(0.5, "b", "k").Equals("k")).IsTrue();
        AssertThat(clock.After(0.25, "c", "k").Equals("k")).IsTrue();   // the key replaces
        AssertThat(clock.Count).IsEqual(2);
        AssertThat(clock.Tick(0.2, false).Count).IsEqual(0);
        AssertThat(clock.Tick(0.5, true).Count).IsEqual(0);              // paused: the real clock only
        AssertThat(clock.Now).IsEqual(0.2);
        AssertThat(clock.RealNow).IsEqual(0.7);
        AssertThat(string.Join(",", clock.Tick(0.1, false))).IsEqual("c");
        long id = (long)clock.After(0.1, "d");
        AssertThat(clock.Cancel(id)).IsTrue();
        AssertThat(clock.Cancel("nothing")).IsFalse();
        AssertThat(clock.After(0.0, "e").Equals(id + 1)).IsTrue();
        AssertThat(string.Join(",", clock.Tick(1.0, false))).IsEqual("e,a");   // in due order
        AssertThat(clock.Count).IsEqual(0);
    }

    [TestCase]
    public void GodotsEntityStoreKeepsLuasRule()
    {
        var store = new ModEntityStore<object?>();
        store.Set(7, "hits", 3L, clear: false);
        AssertThat(store.Value(7, "hits", null)).IsEqual(3L);
        AssertThat(store.Value(7, "other", "none")).IsEqual("none");
        AssertThat(store.Count).IsEqual(1);
        store.Set(7, "hits", null, clear: true);
        AssertThat(store.Count).IsEqual(0);
        store.Set(0, "x", 1L, clear: false);
        AssertThat(store.Count).IsEqual(0);
        store.Set(8, "x", 1L, clear: false);
        store.Forget(8);
        AssertThat(store.Value(8, "x", null)).IsNull();
    }

    [TestCase]
    public void GodotsHashIsTheGamesFnv1()
    {
        AssertThat(RefiiHost.Fnv1(string.Empty)).IsEqual(0x811C9DC5u);
        AssertThat(RefiiHost.Fnv1("OBJECT_HEALTH_POTION")).IsEqual(Refii.Animations.Engine_AnimGdbHashes.Fnv1("OBJECT_HEALTH_POTION"));
    }
}
