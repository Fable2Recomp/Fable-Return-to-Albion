using System;
using System.Diagnostics;
using System.Threading;
using GdUnit4;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Android physical input ownership, slow-consumer and native-receipt lifecycles without native GUI objects.</summary>
[TestSuite]
public sealed class ControllerReadReceiptTests
{
    private const uint A = 0x1000, B = 0x2000;

    /// <summary>A complete short tap published between native steps retains both down and up until exact reads.</summary>
    [TestCase]
    public void SlowOwnerAndSlowGuestCannotLosePressOrRelease()
    {
        var mailbox = new SinglePlayerControllerMailbox();
        mailbox.Publish(Packet(A)); mailbox.Publish(Packet(0));
        AssertThat(mailbox.SelectAcknowledged(1, 0, out ulong press).Buttons).IsEqual(A);
        AssertThat(press > 0).IsTrue();
        for (int step = 2; step <= 8; ++step)
        {
            AssertThat(mailbox.SelectAcknowledged(step, 0, out ulong same).Buttons).IsEqual(A);
            AssertThat(same).IsEqual(press);
        }
        AssertThat(mailbox.SelectAcknowledged(9, press, out ulong release).Buttons).IsEqual(0u);
        AssertThat(release > press).IsTrue();
        AssertThat(mailbox.SelectAcknowledged(10, press, out ulong pending).Buttons).IsEqual(0u);
        AssertThat(pending).IsEqual(release);
        AssertThat(mailbox.SelectAcknowledged(11, release, out ulong settled).Buttons).IsEqual(0u);
        AssertThat(settled).IsEqual(release);
        AssertThat(mailbox.Owner!.ReceiptToken).IsEqual(release);
        AssertThat(mailbox.Owner.Publication.Sequence).IsEqual(2L);
    }

    /// <summary>Distinct clicks and simultaneous chords preserve their intervening release and original order.</summary>
    [TestCase]
    public void DistinctClicksAndChordsAreOrdered()
    {
        var mailbox = new SinglePlayerControllerMailbox();
        uint[] sequence = { A, 0, B, 0, A | B, 0 };
        foreach (uint buttons in sequence) mailbox.Publish(Packet(buttons));
        ulong receipt = 0;
        for (int i = 0; i < sequence.Length; ++i)
        {
            AssertThat(mailbox.SelectAcknowledged(i + 1, receipt, out ulong next).Buttons).IsEqual(sequence[i]);
            AssertThat(next > receipt).IsTrue(); receipt = next;
        }
        AssertThat(mailbox.SelectAcknowledged(7, receipt, out _).Buttons).IsEqual(0u);
    }

    /// <summary>Physical phases retain identity while current sticks, triggers and nonphysical sources continue updating.</summary>
    [TestCase]
    public void AnalogAndOtherButtonsRemainCurrentWithoutMovingTheTag()
    {
        var mailbox = new SinglePlayerControllerMailbox(); mailbox.Publish(Packet(A));
        mailbox.SelectAcknowledged(1, 0, out ulong tag);
        mailbox.Publish(Packet(0, other: B) with { LeftX = 32700, RightY = -16000, LeftTrigger = 255 });
        GameControllerPacket selected = mailbox.SelectAcknowledged(2, tag + 100, out ulong same);
        AssertThat(same).IsEqual(tag); AssertThat(selected.Buttons).IsEqual(A | B);
        AssertThat(selected.LeftX).IsEqual(32700); AssertThat(selected.RightY).IsEqual(-16000);
        AssertThat(selected.LeftTrigger).IsEqual(255u);
        selected = mailbox.SelectAcknowledged(3, tag, out _);
        AssertThat(selected.Buttons).IsEqual(B);
    }

    /// <summary>A genuine physical hold stays held before and after acknowledgment without synthetic repeat releases.</summary>
    [TestCase]
    public void ContinuousHoldNeverManufacturesRepeatEdges()
    {
        var mailbox = new SinglePlayerControllerMailbox(); long start = Stopwatch.GetTimestamp();
        mailbox.PublishAt(Packet(A), start);
        AssertThat(mailbox.SelectAcknowledged(1, 0, out ulong tag, start).Buttons).IsEqual(A);
        for (int i = 0; i < 8; ++i)
        {
            mailbox.PublishAt(Packet(A), start + Ticks(10000 + i));
            AssertThat(mailbox.SelectAcknowledged(2 + i, 0, out ulong same, start + Ticks(10000 + i)).Buttons).IsEqual(A);
            AssertThat(same).IsEqual(tag);
        }
        AssertThat(mailbox.SelectAcknowledged(10, tag, out ulong settled).Buttons).IsEqual(A);
        AssertThat(settled).IsEqual(tag);
        mailbox.Publish(Packet(0));
        AssertThat(mailbox.SelectAcknowledged(11, tag, out ulong release).Buttons).IsEqual(0u);
        AssertThat(release > tag).IsTrue();
    }

    /// <summary>Actual route/mask/device epochs, focus loss and disconnection retire unsatisfied physical work immediately.</summary>
    [TestCase]
    public void ContextFocusAndDisconnectCancelWithoutMaskLeaks()
    {
        var mailbox = new SinglePlayerControllerMailbox(); mailbox.Publish(Packet(A)); mailbox.Publish(Packet(0));
        mailbox.SelectAcknowledged(1, 0, out ulong old);
        mailbox.Publish(Packet(0, epoch: 2, other: B));
        AssertThat(mailbox.SelectAcknowledged(2, old, out ulong cleared).Buttons).IsEqual(B);
        AssertThat(cleared).IsEqual(0UL);
        mailbox.Publish(Packet(A, epoch: 2)); mailbox.Publish(Packet(0, epoch: 2, enabled: false, other: B));
        AssertThat(mailbox.SelectAcknowledged(3, 0, out _).Buttons).IsEqual(B);
        mailbox.Publish(Packet(A, epoch: 3));
        mailbox.Publish(Packet(0, epoch: 3) with { Connected = false });
        AssertThat(mailbox.SelectAcknowledged(4, 0, out _).Connected).IsFalse();
        mailbox.Publish(Packet(0, epoch: 4));
        AssertThat(mailbox.SelectAcknowledged(5, 0, out _).Buttons).IsEqual(0u);
        mailbox.Publish(Packet(A, epoch: 4));
        AssertThat(mailbox.SelectAcknowledged(6, old, out ulong fresh).Buttons).IsEqual(A);
        AssertThat(fresh > old).IsTrue();
    }

    /// <summary>Capacity overflow retires stale commands, and a fresh release permits a later real click.</summary>
    [TestCase]
    public void BacklogCapacityFailsClosedAndRecoversOnRelease()
    {
        var mailbox = new SinglePlayerControllerMailbox();
        for (int i = 0; i < SinglePlayerControllerMailbox.MaxQueuedPhysicalPhases + 1; ++i)
            mailbox.Publish(Packet(i % 2 == 0 ? A : 0));
        mailbox.Publish(Packet(0));
        AssertThat(mailbox.SelectAcknowledged(1, 0, out _).Buttons).IsEqual(0u);
        mailbox.Publish(Packet(B)); mailbox.Publish(Packet(0));
        AssertThat(mailbox.SelectAcknowledged(2, 0, out ulong tag).Buttons).IsEqual(B);
        AssertThat(mailbox.SelectAcknowledged(3, tag, out _).Buttons).IsEqual(0u);
    }

    /// <summary>Released work expires on a monotonic bound, including an old click matching a newer held button.</summary>
    [TestCase]
    public void ExpiredReleasedWorkCannotReplayIntoALaterState()
    {
        var mailbox = new SinglePlayerControllerMailbox(); long start = Stopwatch.GetTimestamp();
        mailbox.PublishAt(Packet(A), start); mailbox.PublishAt(Packet(0), start + Ticks(100));
        AssertThat(mailbox.SelectAcknowledged(1, 0, out ulong cleared, start + Ticks(2100)).Buttons).IsEqual(0u);
        AssertThat(cleared).IsEqual(0UL);
        mailbox.PublishAt(Packet(A), start + Ticks(2200)); mailbox.PublishAt(Packet(0), start + Ticks(2300));
        mailbox.PublishAt(Packet(A), start + Ticks(4500));
        AssertThat(mailbox.SelectAcknowledged(2, 0, out _, start + Ticks(4500)).Buttons).IsEqual(A);
        // The old release is gone; acknowledging a prior tag cannot manufacture a release of the current hold.
        AssertThat(mailbox.SelectAcknowledged(3, 1, out _, start + Ticks(4501)).Buttons).IsEqual(A);
    }

    /// <summary>Absent physical provenance preserves original desktop/current-state routing and ignores other pads.</summary>
    [TestCase]
    public void LegacyPacketsStayUnchangedAndPlayerTwoCannotReplacePlayerOne()
    {
        var mailbox = new SinglePlayerControllerMailbox();
        var packet = new GameControllerPacket(0, true, A, 1, 2, 3, 4, 5, 6);
        mailbox.Publish(packet); AssertThat(mailbox.Publish(packet with { Pad = 1, Buttons = B })).IsFalse();
        AssertThat(mailbox.SelectAcknowledged(1, 999, out ulong tag)).IsEqual(packet);
        AssertThat(tag).IsEqual(0UL);
    }

    /// <summary>Concurrent producer publications remain coherent while the sole native owner acknowledges complete phases.</summary>
    [TestCase]
    public void ConcurrentPublicationAndSelectionRemainCoherent()
    {
        var mailbox = new SinglePlayerControllerMailbox(); int broken = 0;
        using var start = new ManualResetEventSlim(false);
        var producer = new Thread(() =>
        {
            start.Wait();
            for (int i = 1; i <= 200; ++i)
                mailbox.Publish(Packet(i % 2 == 0 ? 0 : A) with { LeftX = i, LeftY = -i, RightX = i * 2 });
        });
        producer.Start(); start.Set(); ulong receipt = 0;
        for (int i = 1; i <= 250; ++i)
        {
            GameControllerPacket selected = mailbox.SelectAcknowledged(i, receipt, out receipt);
            if (selected.LeftX != -selected.LeftY || selected.RightX != selected.LeftX * 2)
                Interlocked.Increment(ref broken);
        }
        producer.Join(); AssertThat(broken).IsEqual(0);
    }

    private static GameControllerPacket Packet(uint physical, long epoch = 1, bool enabled = true, uint other = 0) =>
        new(0, true, physical | other, 0, 0, 0, 0, 0, 0)
        { Physical = new GameControllerPhysicalPublication(epoch, enabled, physical, other) };
    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);
}
