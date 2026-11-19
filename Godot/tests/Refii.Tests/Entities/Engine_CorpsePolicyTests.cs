using System;
using GdUnit4;
using Godot;
using Refii.Entities;
using Refii.Host;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class Engine_CorpsePolicyTests
{
    [TestCase]
    public void ParsesLifetimeWithoutUpperCap()
    {
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds(null)).IsEqual(Engine_CorpsePolicy.DefaultLifetimeSeconds);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("")).IsEqual(Engine_CorpsePolicy.DefaultLifetimeSeconds);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("invalid")).IsEqual(Engine_CorpsePolicy.DefaultLifetimeSeconds);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("0")).IsEqual(0f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("-12")).IsEqual(0f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("\"45\"")).IsEqual(45f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("300")).IsEqual(300f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("7200")).IsEqual(7200f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("86400")).IsEqual(86400f);
    }

    [TestCase]
    public void ParsesAndResolvesMaxCorpseCaps()
    {
        AssertThat(Engine_CorpsePolicy.ParseConfiguredMaxCount(null)).IsEqual(0);
        AssertThat(Engine_CorpsePolicy.ParseConfiguredMaxCount("-1")).IsEqual(-1);
        AssertThat(Engine_CorpsePolicy.ParseConfiguredMaxCount("128")).IsEqual(128);

        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(0f, 0, compactTier: false)).IsEqual(0);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(10f, 0, compactTier: false)).IsEqual(16);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, 0, compactTier: false)).IsEqual(32);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(120f, 0, compactTier: false)).IsEqual(64);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(300f, 0, compactTier: false)).IsEqual(96);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(3600f, 0, compactTier: false)).IsEqual(96);

        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(10f, 0, compactTier: true)).IsEqual(8);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(20f, 0, compactTier: true)).IsEqual(12);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, 0, compactTier: true)).IsEqual(20);

        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, 64, compactTier: true)).IsEqual(64);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, -1, compactTier: false)).IsEqual(int.MaxValue);
    }

    [TestCase]
    public void DetectsEtherealAppearancesAndExcludedTypes()
    {
        var solid = new HostBridge.EntityAppearance(1f, 1f, 1f, Vector4.One, HostBridge.EntityAppearance.DrawableFlag);
        var fadingOrGhost = solid with { Alpha = 0.75f };
        var glowingSpirit = solid with { TintLanes = new Vector4(0.8f, 2.1f, 3.0f, 1f) };

        AssertThat(Engine_CorpsePolicy.IsEtherealAppearance(solid)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEtherealAppearance(fadingOrGhost)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsEtherealAppearance(glowingSpirit)).IsTrue();

        // Exploding bugs, ghosts, banshees, Banshee children, trolls, and Spire shards are excluded.
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x88CCE036u, null)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x8E3A9AF8u, null)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x19020080u, null)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x5AAACE13u, null)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0xFDF0DC5Bu, null)).IsTrue();
        AssertThat(Engine_CorpsePolicy.HasExcludedName("creatures/beetle_bomb.mdl")).IsTrue();
        AssertThat(Engine_CorpsePolicy.HasExcludedName("creatures/Banshee_Elder.mdl")).IsTrue();
        AssertThat(Engine_CorpsePolicy.HasExcludedName("creatures/shadow_wisp.mdl")).IsTrue();

        foreach (uint fieldHash in new[] { 0x2B1E4BC7u, 0x6122274Cu, 0xFD04EFA9u, 0x107B894Cu, 0xEA238706u, 0xC5FFF38Bu })
        {
            AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(fieldHash)).IsTrue();
        }
        AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(0xC3B90D4Fu)).IsFalse(); // CreatureComponent
        AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(0xC09AEA19u)).IsFalse(); // AmbientCreatureComponent

        // Standard physical corpses (skeletons, humans, hobbes, balverines) are allowed.
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x12345678u, "creatures/hollow_man_armoured.mdl")).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x12345678u, "characters/bandit_thug.mdl")).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x12345678u, "creatures/balverine_white.mdl")).IsFalse();
    }

    [TestCase]
    public void FiltersIneligibleCorpsesStrictly()
    {
        var valid = new Engine_CorpsePolicy.CorpseCandidate(
            IsDeadCreature: true,
            HasSkeleton: true,
            SawVisibleWhileDead: true,
            SawEtherealWhileAlive: false,
            IsPrincipal: false,
            IsCompanion: false,
            RecordHash: 0x12345678u,
            ModelPath: "creatures/hollow_man.mdl",
            RecordExcluded: false);

        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { IsDeadCreature = false })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { HasSkeleton = false })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { SawVisibleWhileDead = false })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { SawEtherealWhileAlive = true })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { IsPrincipal = true })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { IsCompanion = true })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { RecordExcluded = true })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(valid with { ModelPath = "creatures/ghost_guard.mdl" })).IsFalse();
    }

    [TestCase]
    public void EvictsOldestDistantCorpseBeforeTouchingNearbyOnes()
    {
        Vector3 cameraPos = Vector3.Zero;
        ReadOnlySpan<Engine_CorpsePolicy.CorpseSlotSummary> slots = new Engine_CorpsePolicy.CorpseSlotSummary[]
        {
            new(new Vector3(4f, 0f, 0f), Age: 40f, FadingOut: false),   // 0: nearby oldest
            new(new Vector3(32f, 0f, 0f), Age: 15f, FadingOut: false),  // 1: distant younger
            new(new Vector3(40f, 0f, 0f), Age: 25f, FadingOut: false),  // 2: distant oldest
            new(new Vector3(50f, 0f, 0f), Age: 99f, FadingOut: true),   // 3: already fading out
        };

        AssertThat(Engine_CorpsePolicy.SelectEvictionIndex(slots, cameraPos)).IsEqual(2);

        ReadOnlySpan<Engine_CorpsePolicy.CorpseSlotSummary> allNearby = new Engine_CorpsePolicy.CorpseSlotSummary[]
        {
            new(new Vector3(3f, 0f, 0f), Age: 12f, FadingOut: false),
            new(new Vector3(8f, 0f, 0f), Age: 28f, FadingOut: false),
        };

        AssertThat(Engine_CorpsePolicy.SelectEvictionIndex(allNearby, cameraPos)).IsEqual(1);
    }
}
