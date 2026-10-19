namespace Refii.Tests.Modding;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// The spells kind: a type name resolves to the binary's index, a type the binary does not have
/// is refused, and the Lua generated for a spell compiles with the game's own compiler.
/// </summary>
[TestSuite]
public class ModSpellsTests
{
    private const string Sample =
        "{\n" +
        "  \"replaces\": \"Chaos\",\n" +
        "  \"name\": \"Frost Nova\",\n" +
        "  \"description\": \"A ring of frost.\",\n" +
        "  \"icon\": \"art/gui/spells/frost.tex.png\",\n" +
        "  \"damage\": 1.3,\n" +
        "  \"look\": { \"base\": [\"fxspl_chaos_orb\"], \"tint\": [0.6, 0.8, 1.4], \"seconds\": 6 },\n" +
        "  \"on_cast\": \"local who = e.caster\\nif Refii.log then Refii.log('nova ' .. tostring(who)) end\",\n" +
        "  \"on_hit\": \"if e.victim_player == 0 then RefiiModSave.frozen = (RefiiModSave.frozen or 0) + 1 end\"\n" +
        "}\n";

    [TestCase]
    public void TypeNamesResolveToTheBinarysIndex()
    {
        AssertThat(ModSpells.TypeNames.Length).IsEqual(8);
        for (int i = 0; i < ModSpells.TypeNames.Length; i++)
        {
            AssertThat(ModSpells.TryResolveType(ModSpells.TypeNames[i], out int byName)).IsTrue();
            AssertThat(byName).IsEqual(i);
            AssertThat(ModSpells.TryResolveType(i.ToString(), out int byIndex)).IsTrue();
            AssertThat(byIndex).IsEqual(i);
        }

        AssertThat(ModSpells.TryResolveType("force push", out int forcePush)).IsTrue();
        AssertThat(forcePush).IsEqual(6);
        AssertThat(ModSpells.TryResolveType("slow_time", out int slowTime)).IsTrue();
        AssertThat(slowTime).IsEqual(2);
        AssertThat(ModSpells.TryResolveType("Swords", out int swords)).IsTrue();
        AssertThat(swords).IsEqual(3);
        AssertThat(ModSpells.TryResolveType("DeadRising", out int raiseDead)).IsTrue();
        AssertThat(raiseDead).IsEqual(7);
    }

    [TestCase]
    public void ATypeTheBinaryDoesNotHaveIsRejected()
    {
        AssertThat(ModSpells.TryResolveType("Meteor", out _)).IsFalse();
        AssertThat(ModSpells.TryResolveType("8", out _)).IsFalse();
        AssertThat(ModSpells.TryResolveType("-1", out _)).IsFalse();

        AssertThat(ModSpells.TryParse("demo", "meteor", "{ \"replaces\": \"Meteor\" }", out ModSpell? spell, out string error)).IsFalse();
        AssertThat(spell).IsNull();
        AssertThat(error).Contains("Meteor");

        AssertThat(ModSpells.TryParse("demo", "none", "{ \"name\": \"No type\" }", out _, out string missing)).IsFalse();
        AssertThat(missing).Contains("replaces");

        AssertThat(ModSpells.TryParse("demo", "bad", "not json", out _, out string notJson)).IsFalse();
        AssertThat(notJson).Contains("JSON");
    }

    [TestCase]
    public void TheSampleParsesToItsFields()
    {
        AssertThat(ModSpells.TryParse("demo", "frost_nova", Sample, out ModSpell? spell, out string error)).IsTrue();
        AssertThat(error).IsEmpty();
        AssertThat(spell).IsNotNull();
        AssertThat(spell!.Type).IsEqual(5);
        AssertThat(spell.Name).IsEqual("Frost Nova");
        AssertThat(spell.Damage).IsEqual(1.3f);
        AssertThat(spell.LookSeconds).IsEqual(6f);
        AssertThat(spell.LookKey).IsEqual("demo.spells.frost_nova.look");
        AssertThat(spell.Label).IsEqual("demo/spells/frost_nova");

        // The Chaos tags the rows go over, and the shipped texture paths the overlays replace.
        AssertThat(ModSpells.NameTags(5)).Contains("GUI_SCREEN_ABILITIES_WILL_CHAOS_NAME_LEVEL_06");
        AssertThat(ModSpells.NameTags(5)).Contains("GUI_SCREEN_ABILITIES_WILL_CHAOS_NAME_LEVEL_01");
        AssertThat(ModSpells.DescriptionTags(5)).Contains("GUI_SCREEN_ABILITIES_WILL_CHAOS_DESC_LEVEL_05");
        AssertThat(ModSpells.WheelIconPath(5)).IsEqual("art/gui/wheelicons/abilities/will_chaos.tex");
        AssertThat(ModSpells.CardPath(5)).IsEqual("art/gui/abilitytarot/chaos_spell.tex");
        AssertThat(ModSpells.WheelIconPath(2)).IsEqual("art/gui/wheelicons/abilities/icon_will_time_control.tex");

        string recipes = ModSpells.LookRecipesJson(spell);
        AssertThat(recipes).Contains("\"id\":\"demo.spells.frost_nova\"");
        AssertThat(recipes).Contains("\"when\":[\"mod:demo.spells.frost_nova.look\"]");
        AssertThat(recipes).Contains("\"equals\":1");
        AssertThat(recipes).Contains("fxspl_chaos_orb");
        AssertThat(recipes.Contains("\"seconds\"")).IsFalse();
    }

    [TestCase]
    public void TheGeneratedLuaCompiles()
    {
        AssertThat(ModSpells.TryParse("demo", "frost_nova", Sample, out ModSpell? spell, out _)).IsTrue();
        string lua = ModSpells.GenerateLua(spell!);

        AssertThat(lua).Contains("Refii.on('spell_launch'");
        AssertThat(lua).Contains("Refii.on('spell_launched'");
        AssertThat(lua).Contains("Refii.on('hit'");
        AssertThat(lua).Contains("Refii.on('hit_landed'");
        AssertThat(lua).Contains("Refii.on('tick'");
        AssertThat(lua).Contains("e.damage = e.damage * DAMAGE");
        AssertThat(lua).Contains("\"demo.spells.frost_nova.look\"");

        // Wrapped as the init chunk wraps it, with the game's own compiler when the library is here.
        string wrapped = ModLua.Wrap(spell!.Label, lua);
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(wrapped, "@" + spell.Label, out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;  // no refii_luadec beside the test host
        }

        AssertThat(ok).OverrideFailureMessage($"generated Lua does not compile: {error}\n{wrapped}").IsTrue();
    }

    [TestCase]
    public void ASpellWithOnlyATypeGeneratesTheMinimum()
    {
        AssertThat(ModSpells.TryParse("demo", "plain", "{ \"replaces\": 0 }", out ModSpell? spell, out _)).IsTrue();
        string lua = ModSpells.GenerateLua(spell!);
        AssertThat(lua).Contains("Refii.on('spell_launched'");
        AssertThat(lua.Contains("Refii.on('hit'")).IsFalse();
        AssertThat(lua.Contains("Refii.on('hit_landed'")).IsFalse();
        AssertThat(lua.Contains("Refii.on('tick'")).IsFalse();
        AssertThat(spell!.Look).IsEmpty();

        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(ModLua.Wrap(spell.Label, lua), "@plain", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;
        }

        AssertThat(ok).OverrideFailureMessage($"generated Lua does not compile: {error}").IsTrue();
    }
}
