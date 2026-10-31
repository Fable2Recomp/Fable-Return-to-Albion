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
}
