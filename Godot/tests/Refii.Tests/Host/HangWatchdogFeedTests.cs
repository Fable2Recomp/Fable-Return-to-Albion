namespace Refii.Tests.Host;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// The hang watchdog's feed (Engine_HangWatchdogFeed): a phase name crosses the bridge once and
/// is stamped by index after that; a refused name is cached as refused; Install hooks the frame
/// phases. The stall schedule itself is native (hang_watchdog.cpp).
/// </summary>
[TestSuite]
public class HangWatchdogFeedTests
{
    [TestCase]
    public void TestPhaseNameRegistersOnceAndStampsEveryEntry()
    {
        var registered = new List<string>();
        var stamped = new List<uint>();
        Engine_HangWatchdogFeed.Install(
            name => { registered.Add(name); return (uint)registered.Count; },
            stamped.Add, _ => { });

        Engine_HangWatchdogFeed.OnPhaseEnter("entities");
        Engine_HangWatchdogFeed.OnPhaseEnter("entities");
        Engine_HangWatchdogFeed.OnPhaseEnter("ui");
        Engine_HangWatchdogFeed.OnPhaseEnter("entities");

        AssertArray(registered).ContainsExactly("entities", "ui");
        AssertArray(stamped).ContainsExactly(1u, 1u, 2u, 1u);
        AssertInt(Engine_HangWatchdogFeed.RegisteredCount).IsEqual(2);
    }

    [TestCase]
    public void TestRefusedNameIsCachedAndNeverStamped()
    {
        int calls = 0;
        var stamped = new List<uint>();
        Engine_HangWatchdogFeed.Install(_ => { calls++; return 0u; }, stamped.Add, _ => { });

        Engine_HangWatchdogFeed.OnPhaseEnter("overflow");
        Engine_HangWatchdogFeed.OnPhaseEnter("overflow");

        AssertInt(calls).IsEqual(1);
        AssertArray(stamped).IsEmpty();
        AssertInt((int)Engine_HangWatchdogFeed.IndexOf("overflow")).IsEqual(0);
    }

    [TestCase]
    public void TestInstallHooksTheFramePhasesAndResetsTheCache()
    {
        var alive = new List<long>();
        Engine_HangWatchdogFeed.Install(_ => 7u, _ => { }, alive.Add);
        Engine_HangWatchdogFeed.OnPhaseEnter("draw");
        AssertInt(Engine_HangWatchdogFeed.RegisteredCount).IsEqual(1);

        AssertObject(Engine_FramePhases.PhaseEnter).IsNotNull();
        AssertObject(Engine_FramePhases.FrameAlive).IsNotNull();
        Engine_FramePhases.FrameAlive!(42L);
        AssertArray(alive).ContainsExactly(42L);

        Engine_HangWatchdogFeed.Install(_ => 1u, _ => { }, _ => { });
        AssertInt(Engine_HangWatchdogFeed.RegisteredCount).IsEqual(0);
    }
}
