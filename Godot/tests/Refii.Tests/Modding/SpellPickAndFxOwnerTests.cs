namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Fx;
using Refii.Titles.Fable2;

/// <summary>
/// G.38, G.40, G.42: the game's own spell pick raised at the guest's slot writer
/// (Fable2/src/vfs/src/hooks/mod_abilities.cpp), the spell's own update stating which effects are its
/// (Fable2/src/vfs/src/hooks/spell_fx_owner.cpp) and the <c>with</c> rule binding exactly that cast
/// (<see cref="Engine_FxPlayer.Bind"/>). Pure .NET; no Godot object is constructed.
/// </summary>
[TestSuite]
public class SpellPickAndFxOwnerTests
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

	private const uint Hash = 0xA1u;
	private const uint OtherHash = 0xB2u;
	private const uint Vortex = 0x40001000u;
	private const uint OtherVortex = 0x40002000u;
	private const uint GroundFx = 0x40003000u;
	private const uint Hero = 0x40004000u;

	private static uint Bind(uint[] entities, bool claimed, params Engine_FxPlayer.PlayAt[] plays) =>
		Engine_FxPlayer.Bind(Hash, entities, claimed, plays, 10f);

	[TestCase]
	public void APlayBindsTheEffectsItsEntityOwns()
	{
		var mine = new Engine_FxPlayer.PlayAt(1, Hash, Vortex, 400f);
		var theirs = new Engine_FxPlayer.PlayAt(2, Hash, OtherVortex, 1f);

		// The vortex's ground effect: owner an FX entity its update places (the ride), so the
		// entities are (ride, attached 0, instance ride 0) — bound to its own vortex however far.
		AssertThat(Bind(new[] { Vortex, 0u, 0u }, true, mine, theirs)).IsEqual(1u);

		// The fireball shot's effect: attached to the shot.
		AssertThat(Bind(new[] { GroundFx, Vortex, 0u }, false, theirs, mine)).IsEqual(1u);

		// A Blades sword's spell-owned instance: no owner, its instance placed by the sword's update.
		AssertThat(Bind(new[] { 0u, 0u, Vortex }, true, theirs, mine)).IsEqual(1u);
	}

	[TestCase]
	public void AnEffectOfAnotherCastIsNeverBoundByDistance()
	{
		var near = new Engine_FxPlayer.PlayAt(2, Hash, Vortex, 1f);

		// Claimed by another vortex's update: not this play's, however near.
		AssertThat(Bind(new[] { OtherVortex, 0u, 0u }, true, near)).IsEqual(0u);

		// Owned by an entity another play (of any effect) rides: that cast's.
		var other = new Engine_FxPlayer.PlayAt(3, OtherHash, OtherVortex, float.NaN);
		AssertThat(Bind(new[] { OtherVortex, 0u, 0u }, false, near, other)).IsEqual(0u);
	}

	[TestCase]
	public void OnlyAnEffectStatedToNoCastBindsByDistance()
	{
		var far = new Engine_FxPlayer.PlayAt(1, Hash, 0u, 144f);
		var near = new Engine_FxPlayer.PlayAt(2, Hash, 0u, 4f);
		var otherEffect = new Engine_FxPlayer.PlayAt(3, OtherHash, 0u, 0f);
		var unplaced = new Engine_FxPlayer.PlayAt(4, Hash, 0u, float.NaN);

		// A blast where the shot was: owned by nothing a play rides, the nearest within the radius.
		AssertThat(Bind(new[] { Hero, 0u, 0u }, false, far, near, otherEffect, unplaced)).IsEqual(2u);
		AssertThat(Bind(new[] { 0u, 0u, 0u }, false, far)).IsEqual(0u);
		AssertThat(Bind(new[] { 0u, 0u, 0u }, false, unplaced)).IsEqual(0u);
	}

	[TestCase]
	public void TheRideRecordParses()
	{
		Engine_FxPlayer.Ride? ride = Engine_FxPlayer.ParseRide("op='ride',fx=1073754112,on=1073745920");
		AssertThat(ride).IsNotNull();
		AssertThat(ride!.Value.Fx).IsEqual(1073754112u);
		AssertThat(ride.Value.On).IsEqual(1073745920u);
		AssertThat(Engine_FxPlayer.ParseRide("op='ride',fx=0,on=5")).IsNull();
		AssertThat(Engine_FxPlayer.ParseRide("op='play',id=5,name='x'")).IsNull();

		// A ride is not a play request.
		AssertThat(Engine_FxPlayer.ParseRequest("op='ride',fx=1,on=2")).IsNull();
		AssertThat(Engine_FxPlayer.RideOf(0u)).IsEqual(0u);
	}

	[TestCase]
	public void ThePayloadStatesWhatGodotReads()
	{
		string? root = RepoRoot();
		if (root == null)
		{
			return;
		}

		string owner = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "spell_fx_owner.cpp"));
		Match format = Regex.Match(owner, @"""(op='ride',fx=%u,on=%u)""");
		AssertThat(format.Success).IsTrue();
		AssertThat(Engine_FxPlayer.ParseRide(format.Groups[1].Value.Replace("%u", "7"))).IsNotNull();
		foreach (string hook in new[] { "rex_VortexComponent_Update", "rex_DamoclesSword_Update", "rex_FxEntity_PlaceAt" })
		{
			AssertThat(owner.Contains($"REX_EXTERN({hook})")).IsTrue();
		}

		string abilities = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_abilities.cpp"));
		AssertThat(abilities.Contains("REX_EXTERN(rex_CECSpellManager_SetSpellSlot)")).IsTrue();
		AssertThat(abilities.Contains("e.pick = \"spell\";")).IsTrue();
		AssertThat(Engine_UiSpellPicks.Game.Name).IsEqual("spell");
		AssertThat(Engine_UiSpellPicks.Game.Mod).IsEqual(string.Empty);

		string externs = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "hooks", "ppc_externs.h"));
		string cmake = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "CMakeLists.txt"));
		AssertThat(cmake.Contains("src/hooks/spell_fx_owner.cpp")).IsTrue();

		// Every hook is named in both builds' configs and declared for its original.
		var both = new Dictionary<string, (string Tu1, string Disc)>
		{
			["rex_CECSpellManager_SetSpellSlot"] = ("0x82541F30", "0x8242C970"),
			["rex_VortexComponent_Update"] = ("0x827590E0", "0x82730AC0"),
			["rex_DamoclesSword_Update"] = ("0x825A63A8", "0x82557190"),
			["rex_FxEntity_PlaceAt"] = ("0x825F86F0", "0x824B8938"),
		};
		string tu1 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
		string disc = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "refii_config.toml"));
		foreach ((string name, (string t, string d)) in both)
		{
			AssertThat(tu1.Contains($"{t} = {{ name = \"{name}\" }}")).OverrideFailureMessage(name + " TU1").IsTrue();
			AssertThat(disc.Contains($"{d} = {{ name = \"{name}\" }}")).OverrideFailureMessage(name + " disc").IsTrue();
			AssertThat(externs.Contains($"PPC_EXTERN_FUNC(__imp__{name});")).OverrideFailureMessage(name + " extern").IsTrue();
		}
	}
}
