using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GdUnit4;
using Godot;
using Refii.Entities;
using Refii.Host;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The walk's per-row shortcuts against the longer answers they stand for: the skip index's slots
/// against a verdict table by address, the view gate's origin test against the sphere it lies in,
/// and the written-out constructed look against the record's equality.
/// </summary>
[TestSuite]
public sealed class EntitySkipIndexSlotTests
{
	/// <summary>The index as it stood before its slots: every question one lookup by address.</summary>
	private sealed class ByAddress
	{
		private struct Entry
		{
			public ulong UniqueId;
			public uint Record;
			public string? Key;
			public int Walk;
			public Engine_EntitySkipIndex.Kind Kind;
			public bool Hit;
			public bool Stale;
		}

		private readonly Dictionary<uint, Entry> _entries = new();
		private readonly List<uint> _absent = new();
		private int _walk;
		private int _present;
		private bool _drawnAdded;

		public int Count => _entries.Count;
		public int Hits { get; private set; }
		public int OwnedHits { get; private set; }

		public void BeginWalk()
		{
			++_walk;
			_present = 0;
			Hits = 0;
			OwnedHits = 0;
			_drawnAdded = false;
		}

		public bool TryHit(uint address, ulong uniqueId, out Engine_EntitySkipIndex.Kind kind)
		{
			ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, address);
			if (Unsafe.IsNullRef(ref entry) || entry.Stale || entry.UniqueId != uniqueId)
			{
				kind = default;
				return false;
			}

			kind = entry.Kind;
			++Hits;
			if (entry.Walk != _walk)
			{
				entry.Walk = _walk;
				entry.Hit = true;
				++_present;
				if (kind != Engine_EntitySkipIndex.Kind.Nothing)
				{
					++OwnedHits;
				}
			}

			return true;
		}

		public void Park(uint address, ulong uniqueId, Engine_EntitySkipIndex.Kind kind, uint record, string? key)
		{
			ref Entry entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, address, out bool exists);
			if (!exists || entry.Walk != _walk)
			{
				++_present;
			}
			else if (entry.Hit && entry.Kind != Engine_EntitySkipIndex.Kind.Nothing)
			{
				--OwnedHits;
			}

			entry = new Entry { UniqueId = uniqueId, Record = record, Key = key, Walk = _walk, Kind = kind };
		}

		public void Unpark(uint address)
		{
			if (!_entries.Remove(address, out Entry old) || old.Walk != _walk)
			{
				return;
			}

			--_present;
			if (old.Hit && old.Kind != Engine_EntitySkipIndex.Kind.Nothing)
			{
				--OwnedHits;
			}
		}

		public void Invalidate(uint address)
		{
			ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, address);
			if (!Unsafe.IsNullRef(ref entry))
			{
				entry.Stale = true;
			}
		}

		public bool Owns(uint address)
		{
			ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, address);
			return !Unsafe.IsNullRef(ref entry) && entry.Walk == _walk && entry.Hit && entry.Kind != Engine_EntitySkipIndex.Kind.Nothing;
		}

		public void AddDrawn(HashSet<uint> records, List<(uint Address, string ModelKey)> keys)
		{
			if (_drawnAdded)
			{
				return;
			}

			_drawnAdded = true;
			foreach ((uint address, Entry entry) in _entries)
			{
				if (entry.Walk != _walk || !entry.Hit)
				{
					continue;
				}

				if (entry.Record != 0u)
				{
					records.Add(entry.Record);
				}

				if (entry.Key is { } key)
				{
					keys.Add((address, key));
				}
			}
		}

		public void EndWalk()
		{
			if (_present == _entries.Count)
			{
				return;
			}

			_absent.Clear();
			foreach ((uint address, Entry entry) in _entries)
			{
				if (entry.Walk != _walk)
				{
					_absent.Add(address);
				}
			}

			foreach (uint address in _absent)
			{
				_entries.Remove(address);
			}
		}

		public void Clear()
		{
			_entries.Clear();
			_present = 0;
			Hits = 0;
			OwnedHits = 0;
			_drawnAdded = false;
		}
	}

	/// <summary>Random walks over a churning list: every answer the feed reads, compared on every step.</summary>
	[TestCase]
	public void SlotsAnswerAsTheTableDid()
	{
		var random = new Random(20261105);
		var slots = new Engine_EntitySkipIndex();
		var table = new ByAddress();
		var list = new List<(uint Address, ulong Uid)>();
		uint next = 0x10000u;
		for (int i = 0; i < 300; ++i)
		{
			list.Add((next += 0x40u, (ulong)random.Next()));
		}

		string[] keys = { "door", "crate", "sign" };
		for (int walk = 0; walk < 3000; ++walk)
		{
			switch (random.Next(10))
			{
				case 0: list.Insert(random.Next(list.Count), (next += 0x40u, (ulong)random.Next())); break;
				case 1: if (list.Count > 50) list.RemoveAt(random.Next(list.Count)); break;
				case 2:
				{
					// A recycled address: same place, another entity.
					int at = random.Next(list.Count);
					list[at] = (list[at].Address, (ulong)random.Next());
					break;
				}
				case 3:
				{
					int a = random.Next(list.Count);
					int b = random.Next(list.Count);
					(list[a], list[b]) = (list[b], list[a]);
					break;
				}
			}

			if (random.Next(500) == 0)
			{
				slots.Clear();
				table.Clear();
			}

			slots.BeginWalk();
			table.BeginWalk();
			for (int row = 0; row < list.Count; ++row)
			{
				(uint address, ulong uid) = list[row];
				if (random.Next(5) == 0)
				{
					AssertThat(slots.Owns(address)).IsEqual(table.Owns(address));
				}

				switch (random.Next(30))
				{
					case 0:
						slots.Unpark(address);
						table.Unpark(address);
						continue;
					case 1:
						slots.Invalidate(address);
						table.Invalidate(address);
						break;
				}

				// A row sometimes reported again at the end of the walk, by another row's number.
				bool hit = slots.TryHit(address, uid, row, out Engine_EntitySkipIndex.Kind kind);
				AssertThat(hit).IsEqual(table.TryHit(address, uid, out Engine_EntitySkipIndex.Kind expected));
				AssertThat(kind).IsEqual(expected);
				if (!hit || random.Next(40) == 0)
				{
					var verdict = (Engine_EntitySkipIndex.Kind)random.Next(4);
					uint record = random.Next(2) == 0 ? 0u : (uint)random.Next(1, 100);
					string? key = random.Next(2) == 0 ? null : keys[random.Next(keys.Length)];
					slots.Park(address, uid, verdict, record, key);
					table.Park(address, uid, verdict, record, key);
				}

				AssertThat(slots.Hits).IsEqual(table.Hits);
				AssertThat(slots.OwnedHits).IsEqual(table.OwnedHits);
			}

			if (random.Next(10) == 0)
			{
				var slotRecords = new HashSet<uint>();
				var tableRecords = new HashSet<uint>();
				var slotKeys = new List<(uint Address, string ModelKey)>();
				var tableKeys = new List<(uint Address, string ModelKey)>();
				slots.AddDrawn(slotRecords, slotKeys);
				table.AddDrawn(tableRecords, tableKeys);
				AssertThat(slotRecords.SetEquals(tableRecords)).IsTrue();
				AssertThat(slotKeys.ToHashSet().SetEquals(tableKeys)).IsTrue();
				AssertThat(slotKeys.Count).IsEqual(tableKeys.Count);
			}

			slots.EndWalk();
			table.EndWalk();
			AssertThat(slots.Count).IsEqual(table.Count);
		}
	}

	/// <summary>An origin the gate passes is a sphere the gate passes, for any sphere holding that origin.</summary>
	[TestCase]
	public void AnOriginInViewIsASphereInView()
	{
		var random = new Random(20261105);
		float Next(float range) => ((float)random.NextDouble() - 0.5f) * 2f * range;
		var projection = Projection.CreatePerspective(70f, 16f / 9f, 0.1f, 500f, false);
		Plane[] planes = new[]
		{
			Projection.Planes.Near, Projection.Planes.Far, Projection.Planes.Left,
			Projection.Planes.Top, Projection.Planes.Right, Projection.Planes.Bottom,
		}.Select(which => projection.GetProjectionPlane(which)).ToArray();

		int passed = 0;
		for (int i = 0; i < 200000; ++i)
		{
			var rig = new Transform3D(
				new Basis(Vector3.Up, Next(3f)).Scaled(Vector3.One * (0.5f + (float)random.NextDouble())),
				new Vector3(Next(300f), Next(50f), Next(300f)));
			if (!Engine_ViewSphere.InsideAll(planes, rig.Origin, 0f, 2.5f))
			{
				continue;
			}

			// Any sphere in model space with the model origin inside: the native side's promise.
			var centre = new Vector3(Next(5f), Next(5f), Next(5f));
			float radius = centre.Length() + (float)random.NextDouble();
			(Vector3 worldCentre, float worldRadius) = Engine_ViewSphere.ToWorld(rig, centre, radius);
			AssertThat(Engine_ViewSphere.InsideAll(planes, worldCentre, worldRadius, 2.5f)).IsTrue();
			++passed;
		}

		AssertThat(passed).IsGreater(1000);
	}

	/// <summary>The written-out test agrees with the record's equality, one lane off at a time.</summary>
	[TestCase]
	public void TheConstructedLookIsTheConstructor()
	{
		HostBridge.EntityAppearance constructed = HostBridge.EntityAppearance.Constructed;
		AssertThat(constructed.IsConstructed).IsTrue();
		float[] offs = { 0f, 0.5f, float.NaN, float.PositiveInfinity, -1f, 1.0000001f };
		foreach (float off in offs)
		{
			HostBridge.EntityAppearance[] looks =
			{
				constructed with { Alpha = off },
				constructed with { CameraAlpha = off },
				constructed with { Scale = off },
				constructed with { TintLanes = new Vector4(off, 1f, 1f, 1f) },
				constructed with { TintLanes = new Vector4(1f, off, 1f, 1f) },
				constructed with { TintLanes = new Vector4(1f, 1f, off, 1f) },
				constructed with { TintLanes = new Vector4(1f, 1f, 1f, off) },
			};
			foreach (HostBridge.EntityAppearance look in looks)
			{
				AssertThat(look.IsConstructed).IsEqual(look == constructed);
			}
		}

		AssertThat((constructed with { Flags = 0u }).IsConstructed).IsFalse();
		AssertThat((constructed with { Flags = 3u }).IsConstructed).IsFalse();
	}
}
