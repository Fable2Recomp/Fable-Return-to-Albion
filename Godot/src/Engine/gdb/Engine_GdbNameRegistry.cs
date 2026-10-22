using System;
using System.Collections.Generic;

namespace Refii.Gdb;

/// <summary>
/// THE GUEST'S NAME REGISTRY, as the name-table tail of a <c>.gdb</c> serialises it. The tail is
/// not a lookup structure of its own: it is the tool's registry dumped slot by slot, and the guest
/// re-registers it linearly (<c>GDBNames_RegisterTable</c> 0x82A01338 reads the first word as a
/// CAPACITY — <c>if (v10 &gt; *a1) sub_829FF8A0(a1, v10)</c> grows its registry to it — then the
/// pair bytes, then <c>nameCount</c> offsets one after another, each registered with
/// <c>sub_82A01288</c> unless <c>GDBNames_ResolveHash</c> already knows the hash).
///
/// The registry (<c>sub_82A01488</c> creates it with 0x10000 slots; <c>sub_82A01288</c> inserts;
/// <c>sub_829FF8A0</c> rehashes) is an open-addressed table: slot <c>hash &amp; (capacity - 1)</c>,
/// linear probe <c>(slot + 1) &amp; (capacity - 1)</c>, and when the count reaches
/// <c>3 * capacity / 4</c> (<c>0x82A012C0 cmplw cr6, r9, r7</c> with <c>r7 = (3 * cap) &gt;&gt; 2</c>)
/// it doubles (<c>0x82A012C8 slwi r4, r11, 1</c>) and re-inserts the old slots in slot order.
///
/// The shipped <c>globals.gdb</c> states capacity 0x10000 and its 19 595 offsets are exactly this
/// table's slot order for the pairs inserted in pair order (probed 2026-10-22: 0 mismatches).
/// </summary>
public static class GdbNameRegistry
{
    /// <summary>The capacity a fresh registry starts with (<c>sub_82A01488</c>: <c>v2[2] = 0x10000</c>).</summary>
    public const uint InitialCapacity = 0x10000u;

    /// <summary>
    /// Inserts <paramref name="hashes"/> in order into a fresh registry and returns its capacity
    /// and the pair indices in slot order — the name-table tail's first word and offsets array.
    /// A hash already registered is not inserted again (the guest's <c>ResolveHash</c> gate), so a
    /// repeated hash keeps its first slot.
    /// </summary>
    public static (uint Capacity, int[] SlotOrder) Layout(IReadOnlyList<uint> hashes)
    {
        if (hashes is null)
        {
            throw new ArgumentNullException(nameof(hashes));
        }

        uint capacity = InitialCapacity;
        int[] slots = new int[capacity];
        Array.Fill(slots, -1);
        int count = 0;

        for (int i = 0; i < hashes.Count; i++)
        {
            uint hash = hashes[i];
            if (Find(slots, hashes, capacity, hash) >= 0)
            {
                continue;
            }

            // sub_82A01180 counts the new name before sub_82A01288 tests the load.
            count++;
            if ((uint)count == (3u * capacity) >> 2)
            {
                uint grown = capacity << 1;
                int[] next = new int[grown];
                Array.Fill(next, -1);
                for (uint s = 0; s < capacity; s++)
                {
                    if (slots[s] >= 0)
                    {
                        Insert(next, hashes, grown, slots[s]);
                    }
                }

                slots = next;
                capacity = grown;
            }

            Insert(slots, hashes, capacity, i);
        }

        var order = new List<int>(count);
        for (uint s = 0; s < capacity; s++)
        {
            if (slots[s] >= 0)
            {
                order.Add(slots[s]);
            }
        }

        return (capacity, order.ToArray());
    }

    private static int Find(int[] slots, IReadOnlyList<uint> hashes, uint capacity, uint hash)
    {
        uint mask = capacity - 1;
        uint s = hash & mask;
        while (slots[s] >= 0)
        {
            if (hashes[slots[s]] == hash)
            {
                return slots[s];
            }

            s = (s + 1) & mask;
        }

        return -1;
    }

    private static void Insert(int[] slots, IReadOnlyList<uint> hashes, uint capacity, int index)
    {
        uint mask = capacity - 1;
        uint hash = hashes[index];
        uint s = hash & mask;
        while (slots[s] >= 0 && hashes[slots[s]] != hash)
        {
            s = (s + 1) & mask;
        }

        slots[s] = index;
    }
}
