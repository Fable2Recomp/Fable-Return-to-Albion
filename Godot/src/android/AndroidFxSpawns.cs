using System;
using System.Collections.Generic;

namespace Refii.Game;

/// <summary>
/// <c>android_fx_spawns_per_frame</c>: how many live effect nodes (<see cref="Refii.Fx.FableFx"/>)
/// <see cref="GameFx"/> builds in one walk of the guest's effect list, the rest waiting their turn.
/// </summary>
/// <remarks>
/// WHY. A node is built on the frame it is added (FableFx._Ready builds its system, surfaces and
/// lights), and entering a town lists every torch, chimney and lamp effect at once: on the S10e
/// one frame built 112 of them, 18.6 ms of the 473 ms hitch, before their first materials and
/// textures. Spread over the next walks, the same effects start a few guest frames apart.
///
/// HOW. A walk happens on each guest frame. The effects that waited longest go first: each walk
/// sets aside slots for the oldest waiting ones (<see cref="AndroidFxSpawnQueue"/>), and the
/// slots left go to effects seen for the first time, in the walk's order. An effect the guest
/// stops listing leaves the queue after <see cref="AndroidFxSpawnQueue.MissedWalksBeforeDrop"/>
/// walks, as a built node is retired after GameFx's own absences, so nothing the guest has
/// finished with is started late; while it is listed, it is built.
///
/// THE VALUE. A count from 1; 0 builds every node at once, as the desktop does. Phone only, read
/// when the effect feed starts (each level).
/// </remarks>
public static class AndroidFxSpawns
{
    /// <summary>The refii.toml key.</summary>
    public const string Key = "android_fx_spawns_per_frame";

    /// <summary>The phone's default limit.</summary>
    public const int DefaultLimit = 16;

    /// <summary>The limit a setting stands for on this platform: 0 (none) off a phone. Pure.</summary>
    /// <param name="config">refii.toml's values.</param>
    /// <param name="phone">Whether this is a phone.</param>
    public static int Resolve(IReadOnlyDictionary<string, string> config, bool phone) =>
        phone ? Math.Max(0, Refii.Core.Engine_RefiiConfig.Int(config, Key, DefaultLimit)) : 0;
}

/// <summary>
/// The per-walk budget of new effect nodes and the effects waiting for one, oldest first. Pure:
/// it holds keys only, and GameFx builds what it admits.
/// </summary>
public sealed class AndroidFxSpawnQueue
{
    /// <summary>Walks a waiting effect may go unasked (unlisted, unplaced) before it leaves the queue.</summary>
    public const int MissedWalksBeforeDrop = 3;

    /// <summary>Waiting effects in the order they were first refused; with the walks each has gone unasked.</summary>
    private readonly List<uint> _order = new();
    private readonly Dictionary<uint, int> _missed = new();

    /// <summary>Waiting effects this walk keeps a slot for.</summary>
    private readonly HashSet<uint> _reserved = new();

    /// <summary>Keys asked for this walk, and those of them admitted.</summary>
    private readonly HashSet<uint> _asked = new();
    private readonly HashSet<uint> _admitted = new();

    /// <summary>This walk's slots for effects outside <see cref="_reserved"/>, and how many are used.</summary>
    private int _open;
    private int _openUsed;

    /// <summary>New nodes a walk may build; 0 for no limit.</summary>
    public int Limit { get; set; }

    /// <summary>Effects waiting for a node.</summary>
    public int Waiting => _order.Count;

    /// <summary>Requests refused so far (an effect refused in two walks counts twice). Run total.</summary>
    public long Deferred { get; private set; }

    /// <summary>Whether <paramref name="key"/> is waiting for a node.</summary>
    public bool IsWaiting(uint key) => _missed.ContainsKey(key);

    /// <summary>Starts a walk: the oldest waiting effects, up to the limit, get this walk's slots first.</summary>
    public void BeginWalk()
    {
        _asked.Clear();
        _admitted.Clear();
        _reserved.Clear();
        _openUsed = 0;
        if (Limit <= 0)
        {
            _open = int.MaxValue;
            return;
        }

        int reserve = Math.Min(Limit, _order.Count);
        for (int i = 0; i < reserve; i++)
        {
            _reserved.Add(_order[i]);
        }

        _open = Limit - reserve;
    }

    /// <summary>
    /// Whether the node for <paramref name="key"/> may be built now. A refused effect waits in
    /// the queue; asking again in the same walk gives the same answer and takes no second slot.
    /// </summary>
    public bool TryAdmit(uint key)
    {
        if (Limit <= 0)
        {
            if (_missed.Count != 0)
            {
                Clear();
            }

            return true;
        }

        if (!_asked.Add(key) && _admitted.Contains(key))
        {
            return true;
        }

        if (_reserved.Remove(key))
        {
            Forget(key);
            _admitted.Add(key);
            return true;
        }

        if (!_missed.ContainsKey(key) && _openUsed < _open)
        {
            _openUsed++;
            _admitted.Add(key);
            return true;
        }

        if (_missed.TryAdd(key, 0))
        {
            _order.Add(key);
        }
        else
        {
            _missed[key] = 0;
        }

        Deferred++;
        return false;
    }

    /// <summary>Ends a walk: an effect not asked for in <see cref="MissedWalksBeforeDrop"/> walks running leaves the queue.</summary>
    public void EndWalk()
    {
        if (_order.Count == 0)
        {
            return;
        }

        _order.RemoveAll(key =>
        {
            if (_asked.Contains(key))
            {
                return false;
            }

            int missed = _missed[key] + 1;
            if (missed < MissedWalksBeforeDrop)
            {
                _missed[key] = missed;
                return false;
            }

            _missed.Remove(key);
            return true;
        });
    }

    /// <summary>Drops every waiting effect.</summary>
    public void Clear()
    {
        _order.Clear();
        _missed.Clear();
        _reserved.Clear();
        _asked.Clear();
        _admitted.Clear();
    }

    private void Forget(uint key)
    {
        if (_missed.Remove(key))
        {
            _order.Remove(key);
        }
    }
}
