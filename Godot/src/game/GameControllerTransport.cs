using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;

namespace Refii.Game;

/// <summary>An immutable XInput sample after production menu masks have been applied.</summary>
public sealed record GameControllerPacket(int Pad, bool Connected, uint Buttons,
    uint LeftTrigger, uint RightTrigger, int LeftX, int LeftY, int RightX, int RightY)
{
    /// <summary>Android-only masked physical provenance; absent on the unchanged desktop transport.</summary>
    internal GameControllerPhysicalPublication? Physical { get; init; }
}

/// <summary>Separates already-masked physical buttons from current keyboard/scripted buttons and cancellation context.</summary>
/// <param name="Epoch">Changes on actual focus, device identity, route or ownership mask changes.</param>
/// <param name="Enabled">Whether physical input currently owns focus.</param>
/// <param name="Buttons">Physical buttons after every production ownership and held-unmask mask.</param>
/// <param name="OtherButtons">Already-filtered current nonphysical buttons; these are never queued.</param>
internal sealed record GameControllerPhysicalPublication(long Epoch, bool Enabled, uint Buttons, uint OtherButtons);

/// <summary>Routes each production input sample to exactly one native owner or the desktop bridge.</summary>
public static class GameControllerTransport
{
    /// <summary>Combines application and window focus while allowing the production sampler to keep clearing edges.</summary>
    /// <param name="applicationEnabled">The Android host's device-focus gate; true by default on desktop.</param>
    /// <param name="windowFocused">Whether the sampler's window owns physical input.</param>
    /// <returns>True only while both sources allow device input.</returns>
    public static bool DevicesHaveFocus(bool applicationEnabled, bool windowFocused) =>
        applicationEnabled && windowFocused;

    /// <summary>Uses the optional owner sink exclusively; desktop behavior remains the fallback.</summary>
    /// <param name="packet">The complete masked XInput sample.</param>
    /// <param name="ownerSink">An optional mailbox publisher for an embedded guest owner.</param>
    /// <param name="nativeFallback">The existing direct bridge publisher.</param>
    public static void Publish(GameControllerPacket packet, Action<GameControllerPacket>? ownerSink,
        Action<GameControllerPacket> nativeFallback) => (ownerSink ?? nativeFallback)(packet);
}

/// <summary>Single-player mailbox retaining bounded Android physical edges until an actual native state-copy receipt.</summary>
/// <remarks>Other pad slots are explicitly ignored by this single-player Android host. No deadzone or gameplay
/// transformation is introduced here; the production sampler and menu masks own those decisions.</remarks>
public sealed class SinglePlayerControllerMailbox
{
    /// <summary>One coherent publication with a monotonic timestamp and sequence.</summary>
    public sealed record Publication(long Sequence, long Timestamp, GameControllerPacket Packet);

    /// <summary>The exact publication selected by the native owner before one service step.</summary>
    public sealed record Observation(long Step, Publication Publication, ulong ReceiptToken = 0,
        uint RetainedPhysicalButtons = 0);

    /// <summary>The maximum press/release snapshots waiting for native reads; overflow retires the stale backlog.</summary>
    internal const int MaxQueuedPhysicalPhases = 16;
    /// <summary>Released physical work expires rather than turning a stalled guest into a later menu action.</summary>
    internal const double MaxReleasedPhaseAgeMilliseconds = 2000;

    /// <summary>One immutable masked physical snapshot, retained until its exact state-copy receipt or cancellation.</summary>
    private sealed record PhysicalPhase(uint Buttons, long Timestamp, ulong Token = 0);
    private readonly object _gate = new();
    private readonly Queue<PhysicalPhase> _phases = new();
    private PhysicalPhase? _active;
    private long? _epoch;
    private uint _publishedPhysical, _settledPhysical;
    private ulong _nextToken, _currentToken, _lastReadReceipt, _lastAcknowledgedToken;
    private bool _suppressUntilRelease;
    private long _overflows, _expired;

    private long _sequence;
    private Publication _latest = new(0, Stopwatch.GetTimestamp(),
        new GameControllerPacket(0, false, 0, 0, 0, 0, 0, 0, 0));
    private Observation? _owner;

    /// <summary>The latest complete packet, without reading mutable fields from another thread.</summary>
    public Publication Latest => Volatile.Read(ref _latest);

    /// <summary>The last exact sample selected by the native thread, or null before its first step.</summary>
    public Observation? Owner => Volatile.Read(ref _owner);

    /// <summary>Publishes pad zero; reports false for deliberately unsupported multiplayer slots.</summary>
    /// <param name="packet">A production packet that has already passed all menu masks.</param>
    /// <returns>True only if the single-player mailbox accepted this sample.</returns>
    public bool Publish(GameControllerPacket packet) => PublishAt(packet, Stopwatch.GetTimestamp());

    /// <summary>Accepts a coherent sample at an explicit monotonic timestamp for isolated age/lifecycle proofs.</summary>
    /// <param name="packet">The production-masked sample.</param>
    /// <param name="now">A monotonic timestamp, in Stopwatch ticks.</param>
    /// <returns>True only for the single-player slot.</returns>
    internal bool PublishAt(GameControllerPacket packet, long now)
    {
        if (packet.Pad != 0) return false;
        lock (_gate)
        {
            Volatile.Write(ref _latest, new Publication(++_sequence, now, packet));
            GameControllerPhysicalPublication? physical = packet.Physical;
            if (physical == null || !packet.Connected || !physical.Enabled)
            {
                ResetPhysical();
                _epoch = physical?.Epoch;
                return true;
            }
            if (_epoch != physical.Epoch)
            {
                ResetPhysical();
                _epoch = physical.Epoch;
            }
            if (_suppressUntilRelease)
            {
                _publishedPhysical = _settledPhysical = physical.Buttons;
                if (physical.Buttons == 0) _suppressUntilRelease = false;
                return true;
            }
            if (_publishedPhysical == physical.Buttons) return true;
            _publishedPhysical = physical.Buttons;
            if (_phases.Count + (_active == null ? 0 : 1) >= MaxQueuedPhysicalPhases)
            {
                ResetPhysical();
                _publishedPhysical = _settledPhysical = physical.Buttons;
                _suppressUntilRelease = physical.Buttons != 0;
                ++_overflows;
            }
            else _phases.Enqueue(new PhysicalPhase(physical.Buttons, now));
        }
        return true;
    }

    /// <summary>Selects and records one immutable packet; only the native owner calls this method.</summary>
    /// <param name="step">The service step about to consume the packet.</param>
    /// <returns>The exact packet to publish to native XInput.</returns>
    public GameControllerPacket Select(long step)
    {
        Publication publication = Latest;
        Volatile.Write(ref _owner, new Observation(step, publication));
        return publication.Packet;
    }

    /// <summary>Selects one physical phase until its exact native successful non-null state-copy receipt arrives.</summary>
    /// <param name="step">The native owner service step.</param>
    /// <param name="readReceipt">The native read token, independent of analog packet-number changes.</param>
    /// <param name="token">The stable tag to publish with this selected native state.</param>
    /// <param name="timestamp">Optional monotonic clock value for isolated timeout proofs.</param>
    /// <returns>The retained physical phase combined with current analog and nonphysical input.</returns>
    internal GameControllerPacket SelectAcknowledged(long step, ulong readReceipt, out ulong token,
        long? timestamp = null)
    {
        lock (_gate)
        {
            Publication latest = Latest;
            _lastReadReceipt = readReceipt;
            if (_active != null && readReceipt == _active.Token)
            {
                _settledPhysical = _active.Buttons;
                _lastAcknowledgedToken = _active.Token;
                _active = null;
            }
            GameControllerPhysicalPublication? physical = latest.Packet.Physical;
            bool enabled = physical != null && physical.Enabled && latest.Packet.Connected;
            long now = timestamp ?? Stopwatch.GetTimestamp();
            PhysicalPhase? oldest = _active ?? (_phases.Count != 0 ? _phases.Peek() : null);
            bool continuousHold = enabled && oldest != null && oldest.Buttons != 0 &&
                oldest.Buttons == physical!.Buttons && _phases.Count + (_active == null ? 0 : 1) == 1;
            if (enabled && oldest != null &&
                !continuousHold &&
                (now - oldest.Timestamp) * 1000.0 / Stopwatch.Frequency > MaxReleasedPhaseAgeMilliseconds)
            {
                ResetPhysical();
                _publishedPhysical = _settledPhysical = physical!.Buttons;
                ++_expired;
            }
            if (enabled && _active == null && _phases.Count != 0)
            {
                _active = _phases.Dequeue() with { Token = ++_nextToken };
                _currentToken = _active.Token;
            }
            uint retained = enabled ? _active?.Buttons ?? _settledPhysical : 0;
            GameControllerPacket packet = enabled
                ? latest.Packet with { Buttons = physical!.OtherButtons | retained }
                : latest.Packet;
            token = enabled ? _currentToken : 0;
            var selected = new Publication(latest.Sequence, latest.Timestamp, packet);
            Volatile.Write(ref _owner, new Observation(step, selected, token, retained));
            return packet;
        }
    }

    /// <summary>Returns bounded transport evidence; a selection remains distinct from native consumption.</summary>
    /// <returns>Current phase, actual receipt, backlog and cancellation/expiry counters.</returns>
    internal object DescribeReceipts()
    {
        lock (_gate) return new
        {
            phase_token = _currentToken, native_read_receipt = _lastReadReceipt,
            acknowledged_token = _lastAcknowledgedToken, pending_phases = _phases.Count,
            phase_waiting = _active != null, physical_context_epoch = _epoch,
            retained_physical_buttons = _active?.Buttons ?? _settledPhysical,
            overflows = _overflows, expired = _expired
        };
    }

    private void ResetPhysical()
    {
        _phases.Clear(); _active = null;
        _publishedPhysical = _settledPhysical = 0;
        _currentToken = 0; _suppressUntilRelease = false;
    }

    /// <summary>Returns a sample's age in monotonic milliseconds without wall-clock adjustments.</summary>
    /// <param name="publication">The publication whose age is measured.</param>
    /// <returns>Nonnegative time since publication.</returns>
    public static double AgeMilliseconds(Publication publication) =>
        Math.Max(0, (Stopwatch.GetTimestamp() - publication.Timestamp) * 1000.0 / Stopwatch.Frequency);
}
