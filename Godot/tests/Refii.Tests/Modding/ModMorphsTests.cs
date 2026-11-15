namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Gdb;
using Refii.Hero;
using Refii.Mods;
using Refii.Titles.Fable2;

/// <summary>
/// MODS' OWN MORPHS (Engine_ModMorphs): a morph's type is the same on every surface and never a
/// shipped one; <c>morphs.json</c> is read and refused as its reader says; the registry the hook
/// reads back recombines; <c>@morph:</c> and <c>@model:</c> rows compile to the type and the model
/// hash; a new morph target is held to its template's topology; the Mirror's mod rows are live and
/// put back on Cancel; and the preview falls back to the declared default.
/// Pure .NET: no Godot type reaches the test host.
/// </summary>
[TestSuite]
public class ModMorphsTests
{
    private static uint Fnv1Lower(string text)
    {
        uint hash = 0x811C9DC5u;
        foreach (char c in text.ToLowerInvariant())
        {
            hash = (hash * 0x01000193u) ^ (byte)c;
        }

        return hash;
    }

    [TestCase]
    public void TheIdIsTheLowerCaseFnv1PositiveAndPastTheShippedTypes()
    {
        foreach (string name in new[] { "hollow.hollow", "My_Mod.Glow", "a.b", "souls.Bulk2" })
        {
            uint id = Engine_ModMorphs.Id(name);
            uint raw = Fnv1Lower(name) & 0x7FFFFFFFu;
            AssertThat(id).IsEqual(raw < 0x100u ? raw + 0x100u : raw);
            AssertThat(id >= Engine_ModMorphs.FirstId && id < 0x80000000u).IsTrue();
            AssertThat(Engine_ModMorphs.Id(name.ToUpperInvariant())).IsEqual(id);
            AssertThat(Engine_ModMorphs.Id("  " + name + " ")).IsEqual(id);
        }

        AssertThat(Engine_ModMorphs.Hex(0x1234abcd)).IsEqual("0x1234abcd");
        AssertThat(Engine_ModMorphs.ParseId("0x1234ABCD")).IsEqual(0x1234abcdu);
        AssertThat(Engine_ModMorphs.ParseId("305441741")).IsNull();
        AssertThat(Engine_ModMorphs.Resolve("glow", "mymod")).IsEqual(Engine_ModMorphs.Id("mymod.glow"));
        AssertThat(Engine_ModMorphs.Resolve("other.glow", "mymod")).IsEqual(Engine_ModMorphs.Id("other.glow"));
        AssertThat(Engine_ModMorphs.Resolve("glow", null)).IsNull();
        AssertThat(Engine_ModMorphs.StoreKey(4096)).IsEqual("morph.4096");
    }

    [TestCase]
    public void TheNativeIdIsTheSameRule()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        string body = Regex.Match(api, @"extern ""C"" uint32_t refii_mod_morph_id\(const char\* scoped\) \{.*?\n\}", RegexOptions.Singleline).Value;
        AssertThat(body.Length > 0).IsTrue();
        AssertThat(body.Contains("hash = (hash * 0x01000193u) ^ (byte >= 'A' && byte <= 'Z' ? byte + 32u : byte);", StringComparison.Ordinal)).IsTrue();
        AssertThat(body.Contains("const uint32_t id = hash & 0x7FFFFFFFu;", StringComparison.Ordinal)).IsTrue();
        AssertThat(body.Contains("return id < 0x100u ? id + 0x100u : id;", StringComparison.Ordinal)).IsTrue();
        // The store key both sides read and write.
        AssertThat(api.Contains("std::snprintf(out, size, \"morph.%u\", id);", StringComparison.Ordinal)).IsTrue();
        // The registry the hook reads is the one Publish writes.
        foreach (string key in new[] { "refii.morphs.generation", "refii.morphs.count", "refii.morphs.%d.%s" })
        {
            AssertThat(api.Contains(key, StringComparison.Ordinal)).OverrideFailureMessage(key).IsTrue();
        }
    }

    [TestCase]
    public void AManifestIsReadAndRefusedAsItsReaderSays()
    {
        var issues = new List<ModMorphIssue>();
        List<ModMorph> morphs = Engine_ModMorphs.Parse("hollow",
            "[{\"name\":\"hollow\",\"label\":\"Hollow\",\"default\":0,\"min\":0,\"max\":1,\"step\":0.05}," +
            "{\"name\":\"bulk\",\"max\":2,\"colour\":1}," +
            "{\"name\":\"bad name\"},{\"name\":\"bulk\"},{\"name\":\"flat\",\"min\":1,\"max\":1}," +
            "{\"name\":\"far\",\"default\":3},{\"name\":\"stepped\",\"step\":0}]", issues);
        AssertThat(morphs.Select(m => m.Name).ToArray()).IsEqual(new[] { "hollow", "bulk" });
        ModMorph bulk = morphs[1];
        AssertThat(bulk.Label).IsEqual("bulk");
        AssertThat(bulk.Minimum).IsEqual(0f);
        AssertThat(bulk.Maximum).IsEqual(2f);
        AssertThat(bulk.Default).IsEqual(0f);
        AssertThat(bulk.Step).IsEqual(0.05f);
        AssertThat(bulk.Id).IsEqual(Engine_ModMorphs.Id("hollow.bulk"));
        AssertThat(issues.Count(i => i.Error)).IsEqual(5);
        AssertThat(issues.Any(i => !i.Error && i.Pointer == "[1].colour")).IsTrue();
        AssertThat(bulk.Clamp(5)).IsEqual(2f);
        AssertThat(bulk.Clamp(float.NaN)).IsEqual(0f);
        AssertThat(morphs[0].Steps).IsEqual(21);
        AssertThat(morphs[0].Value(morphs[0].Index(0.42f))).IsEqual(0.4f);

        var notArray = new List<ModMorphIssue>();
        AssertThat(Engine_ModMorphs.Parse("m", "{\"name\":\"x\"}", notArray).Count).IsEqual(0);
        AssertThat(notArray.Single().Error).IsTrue();
    }

    [TestCase]
    public void TheRegistryRefusesATakenIdAndPublishesWhatTheHookReadsBack()
    {
        var problems = new List<string>();
        List<ModMorph> morphs = Engine_ModMorphs.Compose(new[]
        {
            ("hollow", "[{\"name\":\"hollow\"}]"),
            ("hollow", "[{\"name\":\"hollow\",\"max\":3}]"),
            ("glow", "[{\"name\":\"glow\",\"min\":-1,\"max\":1,\"default\":0.5}]"),
        }, problems);
        AssertThat(morphs.Select(m => m.Scoped).ToArray()).IsEqual(new[] { "hollow.hollow", "glow.glow" });
        AssertThat(problems.Single().Contains("declared twice", StringComparison.Ordinal)).IsTrue();

        var published = new List<(string Key, float Value)>();
        Engine_ModMorphs.Publish(morphs, (key, value) => published.Add((key, value)), 7);
        AssertThat(published[0]).IsEqual(("refii.morphs.count", 2f));
        AssertThat(published[^1]).IsEqual(("refii.morphs.generation", 7f));
        var values = published.ToDictionary(p => p.Key, p => p.Value);
        for (int i = 0; i < morphs.Count; i++)
        {
            uint id = ((uint)values[$"refii.morphs.{i}.hi"] << 16) | (uint)values[$"refii.morphs.{i}.lo"];
            AssertThat(id).IsEqual(morphs[i].Id);
            AssertThat(values[$"refii.morphs.{i}.default"]).IsEqual(morphs[i].Default);
            AssertThat(values[$"refii.morphs.{i}.min"]).IsEqual(morphs[i].Minimum);
            AssertThat(values[$"refii.morphs.{i}.max"]).IsEqual(morphs[i].Maximum);
            AssertThat(values[$"refii.morphs.{i}.hi"] <= 0x7FFF).IsTrue();
        }

        AssertThat(Engine_ModMorphs.Weight(morphs[1], null)).IsEqual(0.5f);
        AssertThat(Engine_ModMorphs.Weight(morphs[1], -4)).IsEqual(-1f);
    }

    [TestCase]
    public void MorphAndModelRowsCompileToTheTypeAndTheHash()
    {
        GdbMulti baseMulti = GdbMulti.Build(new[] { GdbWriter.Serialize(GdbContainerRoundTripTests.SyntheticDatabase()) });
        const string rows =
            "entry,property,type,value\n" +
            "ToyGunFirearm,MorphType,int,@morph:hollow\n" +
            "ToyGunFirearm,OtherMorph,enum,@morph:glow.glow\n" +
            "ToyGunFirearm,TargetModel,string,@model:Data/Art/MyMod/Target.mdl\n";
        GdbCsvResult result = GdbCsvOverlay.Parse(rows, baseMulti, "hollow");
        AssertThat(string.Join("\n", result.Errors)).IsEqual(string.Empty);
        GdbPatchEntry entry = result.Database.Entries.Single(e => e.Id == Engine_GameHash.Fnv1("ToyGunFirearm"));
        uint Value(string name) => entry.Properties.Single(p => p.NameHash == Engine_GameHash.Fnv1(name)).Value;
        AssertThat(Value("MorphType")).IsEqual(Engine_ModMorphs.Id("hollow.hollow"));
        AssertThat(Value("OtherMorph")).IsEqual(Engine_ModMorphs.Id("glow.glow"));
        AssertThat(Value("TargetModel")).IsEqual(Refii.Level.Engine_PropModelIndex.PathHash("art\\mymod\\target.mdl"));
        AssertThat(GdbCsvOverlay.ModelHash("art/mymod/target.mdl")).IsEqual(Fnv1Lower("art\\mymod\\target.mdl"));

        // A bare name needs its mod; a morph is no float; a model hash is no int.
        GdbCsvResult refused = GdbCsvOverlay.Parse(
            "ToyGunFirearm,MorphType,int,@morph:hollow\nToyGunFirearm,A,float,@morph:hollow.x\nToyGunFirearm,B,int,@model:a.mdl\n", baseMulti);
        AssertThat(refused.Errors.Count).IsEqual(3);
        AssertThat(refused.Errors[0].Contains("@morph:<mod>.hollow", StringComparison.Ordinal)).IsTrue();

        // The form check (no database) takes a bare name.
        AssertThat(GdbCsvOverlay.Parse("X,MorphType,int,@morph:hollow\n", null).Errors.Count).IsEqual(0);
    }

    [TestCase]
    public void ANewMorphTargetIsHeldToItsTemplatesTopology()
    {
        var errors = new List<string>();
        Dictionary<string, string> targets = ModMeshOverlays.ParseTargets(
            "[{\"model\":\"Art/MyMod/Bulk.mdl\",\"template\":\"art/characters/body.mdl\"}," +
            "{\"model\":\"art/mymod/bulk.mdl\",\"template\":\"art/x.mdl\"},{\"model\":\"a.mdl\",\"template\":\"a.mdl\"},{\"model\":\"b.glb\",\"template\":\"c.mdl\"}]", errors);
        AssertThat(targets.Count).IsEqual(1);
        AssertThat(targets["art/mymod/bulk.mdl"]).IsEqual("art/characters/body.mdl");
        AssertThat(errors.Count).IsEqual(3);

        byte[] template = MdlConverterTests.SyntheticModel(new[] { false, true });
        AssertThat(ModMeshOverlays.SameTopology(MdlConverterTests.SyntheticModel(new[] { false, true }), template, out string same))
            .OverrideFailureMessage(same.Length == 0 ? "same topology refused" : same).IsTrue();
        AssertThat(ModMeshOverlays.SameTopology(MdlConverterTests.SyntheticModel(new[] { false }), template, out string fewer)).IsFalse();
        AssertThat(fewer.Contains("mesh buffer", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModMeshOverlays.SameTopology(MdlConverterTests.SyntheticModel(new[] { true, true }), template, out string kind)).IsFalse();
        AssertThat(kind.Contains("skinned", StringComparison.Ordinal)).OverrideFailureMessage(kind).IsTrue();
        AssertThat(ModMeshOverlays.SameTopology(MdlConverterTests.SyntheticModel(new[] { false, true }, quad: 0), template, out string count)).IsFalse();
        AssertThat(count.Contains("vertices", StringComparison.Ordinal)).OverrideFailureMessage(count).IsTrue();
    }

    [TestCase]
    public void TheMirrorsModRowsAreLiveAndCancelPutsThemBack()
    {
        var hollow = new ModMorph("hollow", "hollow", "Hollow", 0, 0, 1, 0.05f);
        var written = new List<(uint Id, float Weight)>();
        var mirror = new Engine_MorphMirrorSettings(_ => "", null, new[] { (hollow, 0.2f) }, (m, w) => written.Add((m.Id, w)));
        AssertThat(mirror.Automatic).IsTrue();
        AssertThat(mirror.LookRows).IsEqual(1);
        AssertThat(mirror.Count).IsEqual(2);
        AssertThat(mirror.Title(1)).IsEqual("Hollow");
        AssertThat(mirror.Display(1)).IsEqual("20%");
        AssertThat(mirror.Adjust(1, 2)).IsTrue();
        AssertThat(written.Single()).IsEqual((hollow.Id, 0.3f));
        AssertThat(mirror.MorphWeight(1)).IsEqual(0.3f);

        // Manual adds the stat rows before it; the mod row follows them.
        AssertThat(mirror.Adjust(0, -1)).IsTrue();
        AssertThat(mirror.Count).IsEqual(Engine_CharacterMorphs.Sliders.Length + 2);
        AssertThat(mirror.Title(mirror.Count - 1)).IsEqual("Hollow");
        AssertThat(mirror.Adjust(mirror.Count - 1, 100)).IsTrue();
        AssertThat(written[^1]).IsEqual((hollow.Id, 1f));
        AssertThat(mirror.Adjust(mirror.Count - 1, 1)).IsFalse();

        mirror.RestoreMorphs();
        AssertThat(written[^1]).IsEqual((hollow.Id, 0.2f));
        AssertThat(mirror.MorphWeight(mirror.Count - 1)).IsEqual(0.2f);
        int before = written.Count;
        mirror.RestoreMorphs();
        AssertThat(written.Count).IsEqual(before);
    }

    [TestCase]
    public void ThePreviewWeighsAModTypeAtItsDefault()
    {
        try
        {
            var stats = new HeroMorphStats { Age = 30 };
            uint id = Engine_ModMorphs.Id("glow.glow");
            Engine_ModMorphs.Set(null);
            AssertThat(Engine_HeroMorphState.Weight(stats, id)).IsEqual(0f);
            Engine_ModMorphs.Set(new[] { new ModMorph("glow", "glow", "Glow", 0.75f, 0, 1, 0.05f) });
            AssertThat(Engine_HeroMorphState.Weight(stats, id)).IsEqual(0.75f);
            AssertThat(Engine_HeroMorphState.Weight(new HeroMorphStats { Age = 12 }, id)).IsEqual(0f);
            AssertThat(Engine_HeroMorphState.Weight(stats, 19)).IsEqual(0f);
        }
        finally
        {
            Engine_ModMorphs.Set(null);
        }
    }

    [TestCase]
    public void TheHookAnswersBothPlayersInBothModesAndRedraws()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string hook = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "character_morph_hooks.cpp"));
        string weight = Regex.Match(hook, @"extern ""C"" REX_FUNC\(rex_CECMorph_GetWeight\) \{.*?\n\}", RegexOptions.Singleline).Value;
        // A mod type answers before the manual look, for whichever player's hero owns the component.
        AssertThat(weight.IndexOf("refii_mod_morph_weight(player, type, &weight)", StringComparison.Ordinal) > 0).IsTrue();
        AssertThat(weight.IndexOf("refii_mod_morph_weight", StringComparison.Ordinal) < weight.IndexOf("LookOf(", StringComparison.Ordinal)).IsTrue();
        AssertThat(weight.Contains("adult.r4.u64 = 1;", StringComparison.Ordinal)).IsTrue();
        AssertThat(hook.Contains("const uint32_t entity = refii::mods::LoadU32(base, component + 4);", StringComparison.Ordinal)).IsTrue();
        // Every player's hero is tracked, manual or not; a moved weight redraws it and raises the event.
        AssertThat(hook.Contains("const uint32_t any = refii_mod_player_hero(base, player);", StringComparison.Ordinal)).IsTrue();
        AssertThat(hook.Contains("refii::mods::MorphChangedEvent e{};", StringComparison.Ordinal)).IsTrue();
        AssertThat(hook.Contains("QueueRefresh(hero);", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheEventStandsOnEverySurface()
    {
        ModApiEvent changed = ModApi.Find("morph_changed")!;
        AssertThat(changed).IsNotNull();
        AssertThat(changed.Kind).IsEqual(115);
        AssertThat(changed.Cancellable).IsFalse();
        AssertThat(changed.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "player", "morph", "weight", "hero" });
        AssertThat(RefiiHost.SignalNames.Contains("morph_changed")).IsTrue();
        AssertThat(ModApi.VerdictReads.Contains(("morph", "morph_get"))).IsTrue();

        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string events = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        AssertThat(events.Contains("REFII_EVENT_MORPH_CHANGED = 115,", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("\"player=%d,morph='0x%08x',weight=%.9g,hero=%u\"", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string name in new[] { "morph_get", "morph_set", "morph_id" })
        {
            AssertThat(natives.Contains($"{{\"{name}\", &Native", StringComparison.Ordinal)).OverrideFailureMessage(name).IsTrue();
        }

        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("what == \"morph\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("op == \"morph_set\"", StringComparison.Ordinal)).IsTrue();
        string reference = File.ReadAllText(Path.Combine(root, "Godot", "docs", "modding", "reference-events.md"));
        AssertThat(reference.Contains("| 115 | MorphChanged / morph_changed |", StringComparison.Ordinal)).IsTrue();
    }
}
