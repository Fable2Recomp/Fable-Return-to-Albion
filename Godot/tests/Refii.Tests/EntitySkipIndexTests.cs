using System.Collections.Generic;
using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The entity walk's skip verdicts: what a hit owns and draws, and what undoes one.</summary>
[TestSuite]
public sealed class EntitySkipIndexTests
{
	private static bool Hit(Engine_EntitySkipIndex index, uint address, ulong uid, out Engine_EntitySkipIndex.Kind kind) =>
		index.TryHit(address, uid, out kind);

	/// <summary>A verdict parked on one walk answers the next; an empty one is counted, never owned.</summary>
	[TestCase]
	public void AParkedVerdictAnswersTheNextWalk()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Still, 0xAAu, "door");
		index.Park(2u, 20ul, Engine_EntitySkipIndex.Kind.Nothing, 0u, null);
		AssertThat(index.Owns(1u)).IsFalse(); // the walk's own set holds it this walk
		AssertThat(index.OwnedHits).IsEqual(0);
		index.EndWalk();

		index.BeginWalk();
		AssertThat(Hit(index, 1u, 10ul, out var still)).IsTrue();
		AssertThat(still).IsEqual(Engine_EntitySkipIndex.Kind.Still);
		AssertThat(Hit(index, 2u, 20ul, out var nothing)).IsTrue();
		AssertThat(nothing).IsEqual(Engine_EntitySkipIndex.Kind.Nothing);
		AssertThat(index.Hits).IsEqual(2);
		AssertThat(index.OwnedHits).IsEqual(1);
		AssertThat(index.Owns(1u)).IsTrue();
		AssertThat(index.Owns(2u)).IsFalse();
		AssertThat(index.Owns(3u)).IsFalse();
		index.EndWalk();
		AssertThat(index.Count).IsEqual(2);
	}

	/// <summary>An address that recycled to another entity is not answered for it.</summary>
	[TestCase]
	public void ARecycledAddressMisses()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Adopted, 0u, null);
		index.EndWalk();
		index.BeginWalk();
		AssertThat(Hit(index, 1u, 11ul, out _)).IsFalse();
		AssertThat(index.Hits).IsEqual(0);
	}

	/// <summary>A walk that does not report an entity drops its verdict.</summary>
	[TestCase]
	public void AnAbsentEntityLosesItsVerdict()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Batched, 5u, "crate");
		index.Park(2u, 20ul, Engine_EntitySkipIndex.Kind.Batched, 6u, "barrel");
		index.EndWalk();

		index.BeginWalk();
		AssertThat(Hit(index, 2u, 20ul, out _)).IsTrue();
		index.EndWalk();
		AssertThat(index.Count).IsEqual(1);

		index.BeginWalk();
		AssertThat(Hit(index, 1u, 10ul, out _)).IsFalse();
		AssertThat(Hit(index, 2u, 20ul, out _)).IsTrue();
	}

	/// <summary>
	/// The path going past every skip drops the verdict at once; a change from outside the path
	/// keeps this walk's answer (it was decided before) and refuses the next until the path
	/// writes a new one.
	/// </summary>
	[TestCase]
	public void UnparkAndInvalidate()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Still, 0u, null);
		index.Park(2u, 20ul, Engine_EntitySkipIndex.Kind.Still, 0u, null);
		index.EndWalk();

		index.BeginWalk();
		index.Unpark(1u);
		AssertThat(Hit(index, 1u, 10ul, out _)).IsFalse();
		AssertThat(Hit(index, 2u, 20ul, out _)).IsTrue();
		index.Invalidate(2u);
		AssertThat(index.Owns(2u)).IsTrue();
		AssertThat(index.OwnedHits).IsEqual(1);
		index.EndWalk();

		index.BeginWalk();
		AssertThat(Hit(index, 2u, 20ul, out _)).IsFalse();
		index.Park(2u, 20ul, Engine_EntitySkipIndex.Kind.Still, 0u, null);
		index.EndWalk();
		index.BeginWalk();
		AssertThat(Hit(index, 2u, 20ul, out _)).IsTrue();
	}

	/// <summary>The hits' records and model keys join the walk's drawn sets once a walk, the parked ones' never.</summary>
	[TestCase]
	public void HitsAreDrawnOncePerWalk()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Batched, 0xAu, "crate");
		index.Park(2u, 20ul, Engine_EntitySkipIndex.Kind.Adopted, 0u, null);
		index.Park(3u, 30ul, Engine_EntitySkipIndex.Kind.Still, 0xCu, "sign");
		var records = new HashSet<uint>();
		var keys = new List<(uint Address, string ModelKey)>();
		index.AddDrawn(records, keys);
		AssertThat(records.Count).IsEqual(0); // parked by the path: the walk drew them itself
		index.EndWalk();

		index.BeginWalk();
		AssertThat(Hit(index, 1u, 10ul, out _)).IsTrue();
		AssertThat(Hit(index, 2u, 20ul, out _)).IsTrue();
		index.Park(3u, 30ul, Engine_EntitySkipIndex.Kind.Still, 0xCu, "sign");
		index.AddDrawn(records, keys);
		index.AddDrawn(records, keys);
		AssertThat(records.Count).IsEqual(1);
		AssertThat(records.Contains(0xAu)).IsTrue();
		AssertThat(keys.Count).IsEqual(1);
		AssertThat(keys[0]).IsEqual((1u, "crate"));
	}

	/// <summary>The same address twice in one walk is owned once.</summary>
	[TestCase]
	public void ADuplicateIsOwnedOnce()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Still, 0u, null);
		index.EndWalk();

		index.BeginWalk();
		AssertThat(Hit(index, 1u, 10ul, out _)).IsTrue();
		AssertThat(Hit(index, 1u, 10ul, out _)).IsTrue();
		AssertThat(index.Hits).IsEqual(2);
		AssertThat(index.OwnedHits).IsEqual(1);

		// The second report went down the path instead: the walk's own set holds it now.
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Still, 0u, null);
		AssertThat(index.OwnedHits).IsEqual(0);
		AssertThat(index.Owns(1u)).IsFalse();
		index.EndWalk();
		AssertThat(index.Count).IsEqual(1);
	}

	/// <summary>Clearing forgets every verdict.</summary>
	[TestCase]
	public void ClearForgetsEverything()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Still, 0u, null);
		index.EndWalk();
		index.Clear();
		index.BeginWalk();
		AssertThat(Hit(index, 1u, 10ul, out _)).IsFalse();
		AssertThat(index.Count).IsEqual(0);
	}

	/// <summary>A found verdict is not a hit until taken: the walk's own path may still decide.</summary>
	[TestCase]
	public void AFoundVerdictCountsOnlyOnceTaken()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.RigProp, 0xAAu, "door", null, 1);
		index.EndWalk();

		index.BeginWalk();
		AssertThat(index.TryFind(1u, 10ul, 0, out var kind, out int tag)).IsTrue();
		AssertThat(kind).IsEqual(Engine_EntitySkipIndex.Kind.RigProp);
		AssertThat(tag).IsEqual(1);
		AssertThat(index.Hits).IsEqual(0);
		AssertThat(index.Owns(1u)).IsFalse();

		// Not taken: the path ran and dropped it; the walk ends without it.
		index.Unpark(1u);
		index.EndWalk();
		AssertThat(index.Count).IsEqual(0);

		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.RigProp, 0xAAu, "door", null, 1);
		index.EndWalk();
		index.BeginWalk();
		AssertThat(index.TryFind(1u, 10ul, 0, out _, out _)).IsTrue();
		index.Take();
		index.Take(); // a second take of the same find is nothing
		AssertThat(index.Hits).IsEqual(1);
		AssertThat(index.OwnedHits).IsEqual(1);
		AssertThat(index.Owns(1u)).IsTrue();
		index.EndWalk();
		AssertThat(index.Count).IsEqual(1);
	}

	/// <summary>A held rigged prop and a building's parts are drawn by the hits, every key of them.</summary>
	[TestCase]
	public void AHitDrawsEveryKeyItsPathDrew()
	{
		var index = new Engine_EntitySkipIndex();
		index.BeginWalk();
		index.Park(1u, 10ul, Engine_EntitySkipIndex.Kind.Still, 0xB0u, null, new[] { "roof", "wall" }, 0x201);
		index.Park(2u, 20ul, Engine_EntitySkipIndex.Kind.RigProp, 0xC0u, "sign");
		index.EndWalk();

		index.BeginWalk();
		AssertThat(index.TryFind(1u, 10ul, 0, out _, out int tag)).IsTrue();
		AssertThat(tag).IsEqual(0x201);
		index.Take();
		AssertThat(Hit(index, 2u, 20ul, out _)).IsTrue();
		var records = new HashSet<uint>();
		var keys = new List<(uint Address, string ModelKey)>();
		index.AddDrawn(records, keys);
		AssertThat(records.Contains(0xB0u)).IsTrue();
		AssertThat(records.Contains(0xC0u)).IsTrue();
		AssertThat(keys.Contains((1u, "roof"))).IsTrue();
		AssertThat(keys.Contains((1u, "wall"))).IsTrue();
		AssertThat(keys.Contains((2u, "sign"))).IsTrue();
		AssertThat(keys.Count).IsEqual(3);
	}

	/// <summary>
	/// Each kind is re-asked on its own path's cadence: the empty path's eight reconcile periods
	/// (whatever the still scale, which is at most eight), a rigged prop's settled-prop check, the
	/// still paths' reconcile times the scale.
	/// </summary>
	[TestCase]
	public void EachKindIsReaskedOnItsOwnPathsCadence()
	{
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Nothing, 30, 1)).IsEqual(240);
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Nothing, 30, 8)).IsEqual(240);
		// Scales that don't divide 8: the old empty path re-read only on walks due on both cadences.
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Nothing, 30, 3)).IsEqual(720);
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Nothing, 30, 5)).IsEqual(1200);
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Nothing, 30, 7)).IsEqual(1680);
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Still, 30, 1)).IsEqual(30);
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Batched, 30, 4)).IsEqual(120);
		AssertThat(Engine_EntitySkipIndex.RecheckTicks(Engine_EntitySkipIndex.Kind.Adopted, 30, 2)).IsEqual(60);

		// A held verdict's cadence divides into the one whose walk it must not skip: the reconcile
		// (30) divides the empty path's (240), and the settled-prop check (6) divides the reconcile,
		// so a rigged prop is never held across its reconcile walk.
		AssertThat(240 % 30).IsEqual(0);
		AssertThat(30 % Engine_EntitySkipIndex.RigPropRecheckTicks).IsEqual(0);
	}
}
