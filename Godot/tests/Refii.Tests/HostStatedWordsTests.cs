using System;
using System.Collections.Generic;
using GdUnit4;
using Refii.Host;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// <see cref="HostStatedWords"/> answers exactly what comparing every row against tables keyed by
/// address answers, whatever the guest's list does between walks.
/// </summary>
[TestSuite]
public sealed class HostStatedWordsTests
{
	/// <summary>The comparison the slots replace, by address alone, pruned on the same walks.</summary>
	private sealed class Tables
	{
		private readonly Dictionary<uint, uint[]> _last = new();
		private int _walks;

		public bool[] Walk(uint[] words, int count)
		{
			var changed = new bool[count];
			bool prune = ++_walks >= 600;
			var seen = new HashSet<uint>();
			for (int row = 0; row < count; ++row)
			{
				int at = row * HostStatedWords.Stride;
				uint address = words[at + 8];
				uint[] now = Compared(words, at);
				seen.Add(address);
				if (!_last.TryGetValue(address, out uint[]? last) || !last.AsSpan().SequenceEqual(now))
				{
					_last[address] = now;
					changed[row] = true;
				}
			}

			if (prune)
			{
				_walks = 0;
				foreach (uint address in new List<uint>(_last.Keys))
				{
					if (!seen.Contains(address))
					{
						_last.Remove(address);
					}
				}
			}

			return changed;
		}

		private static uint[] Compared(uint[] words, int at)
		{
			var kept = new uint[20];
			Array.Copy(words, at, kept, 0, 8);
			Array.Copy(words, at + 9, kept, 8, 3);
			Array.Copy(words, at + 20, kept, 11, 9);
			return kept;
		}
	}

	private sealed class Row
	{
		public uint Address;
		public readonly uint[] Words = new uint[HostStatedWords.Stride];
	}

	/// <summary>Random churn: moves, looks, unread words, spawns, removals, reorders, returns, long absences.</summary>
	[TestCase]
	public void MatchesTheTablesThroughChurn()
	{
		var random = new Random(20261105);
		var list = new List<Row>();
		var away = new List<Row>();
		uint nextAddress = 0x10000u;
		Row Spawn()
		{
			var row = new Row { Address = nextAddress };
			nextAddress += 0x40u;
			for (int w = 0; w < row.Words.Length; ++w)
			{
				row.Words[w] = (uint)random.Next();
			}

			row.Words[8] = row.Address;
			return row;
		}

		for (int i = 0; i < 400; ++i)
		{
			list.Add(Spawn());
		}

		var slots = new HostStatedWords(4096);
		var tables = new Tables();
		for (int walk = 0; walk < 2500; ++walk)
		{
			foreach (Row row in list)
			{
				switch (random.Next(60))
				{
					case 0: row.Words[random.Next(8)] = (uint)random.Next(); break;      // placement or model
					case 1: row.Words[20 + random.Next(9)] = (uint)random.Next(); break; // highlight or look
					case 2: row.Words[11] = (uint)random.Next(); break;                  // record
					case 3: row.Words[9 + random.Next(2)] = (uint)random.Next(); break;  // unique id: a recycled address
					case 4: row.Words[12 + random.Next(8)] = (uint)random.Next(); break; // the pair: not compared
				}
			}

			switch (random.Next(12))
			{
				case 0: list.Insert(random.Next(list.Count + 1), Spawn()); break;
				case 1:
				{
					int leaving = random.Next(list.Count);
					away.Add(list[leaving]);
					list.RemoveAt(leaving);
					break;
				}
				case 2 when away.Count > 0:
				{
					int back = random.Next(away.Count);
					list.Insert(random.Next(list.Count + 1), away[back]);
					away.RemoveAt(back);
					break;
				}
				case 3:
				{
					int first = random.Next(list.Count);
					int second = random.Next(list.Count);
					(list[first], list[second]) = (list[second], list[first]);
					break;
				}
				case 4:
				{
					// A long stretch leaves at once: past the look-ahead.
					int from = random.Next(list.Count);
					int many = Math.Min(20, list.Count - from);
					away.AddRange(list.GetRange(from, many));
					list.RemoveRange(from, many);
					break;
				}
				case 5:
					list.Add(list[random.Next(list.Count)]); // reported twice, same words
					break;
			}

			if (list.Count < 50)
			{
				list.Add(Spawn());
			}

			// One list reported twice stays once from here on, or it would pile up.
			var unique = new HashSet<Row>();
			uint[] words = slots.Words;
			for (int row = 0; row < list.Count; ++row)
			{
				Array.Copy(list[row].Words, 0, words, row * HostStatedWords.Stride, HostStatedWords.Stride);
				unique.Add(list[row]);
			}

			int count = list.Count;
			bool[] expected = tables.Walk(words, count);
			slots.BeginWalk(count);
			for (int row = 0; row < count; ++row)
			{
				AssertThat(slots.Changed(row)).IsEqual(expected[row]);
			}

			slots.EndWalk(count);
			if (unique.Count != count)
			{
				list = new List<Row>(unique);
			}
		}
	}

	/// <summary>A row that keeps its place and its words is unchanged, and the first walk changes everything.</summary>
	[TestCase]
	public void AStillListIsUnchanged()
	{
		var slots = new HostStatedWords(16);
		var row = new uint[HostStatedWords.Stride];
		row[8] = 0x20000u;
		row[0] = 7u;

		bool Walk()
		{
			row.CopyTo(slots.Words, 0);
			slots.BeginWalk(1);
			bool changed = slots.Changed(0);
			slots.EndWalk(1);
			return changed;
		}

		AssertThat(Walk()).IsTrue();
		AssertThat(Walk()).IsFalse();
		AssertThat(Walk()).IsFalse();
		row[25] = 9u;
		AssertThat(Walk()).IsTrue();
		AssertThat(Walk()).IsFalse();
	}

	/// <summary>
	/// A new entity at an address another one held is a change even with the same placement,
	/// model, record and look: the guest recycles addresses across a level change.
	/// </summary>
	[TestCase]
	public void ARecycledAddressIsAChange()
	{
		var slots = new HostStatedWords(16);
		var row = new uint[HostStatedWords.Stride];
		row[8] = 0x42C10DD0u;
		row[10] = 0x453D501Eu;

		bool Walk()
		{
			row.CopyTo(slots.Words, 0);
			slots.BeginWalk(1);
			bool changed = slots.Changed(0);
			slots.EndWalk(1);
			return changed;
		}

		AssertThat(Walk()).IsTrue();
		AssertThat(Walk()).IsFalse();
		row[10] = 0xA4897C14u;
		AssertThat(Walk()).IsTrue();
		AssertThat(Walk()).IsFalse();
	}
}
