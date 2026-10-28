using GdUnit4;
using Refii.AndroidRuntime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Verifies reader scheduling and pending-result lifecycle independently of Godot/native guest bindings.</summary>
[TestSuite]
public sealed class AndroidEntityFeedTests
{
    /// <summary>The new publication, rather than the previous frame's flag, decides the read.</summary>
    [TestCase]
    public void SamplesBeforeEvaluatingChangeAndKicking()
    {
        var events = new List<string>();
        bool changed = false;
        AndroidEntityFeed.SampleAndKick(false, () => { changed=true; events.Add("sample"); },
            () => { events.Add("changed"); return changed; }, () => events.Add("kick"));
        AssertThat(events.ToArray()).IsEqual(new[] { "sample", "changed", "kick" });
        events.Clear();
        AndroidEntityFeed.SampleAndKick(false, () => { changed=false; events.Add("sample"); },
            () => { events.Add("changed"); return changed; }, () => events.Add("kick"));
        AssertThat(events.ToArray()).IsEqual(new[] { "sample", "changed" });
    }

    /// <summary>Unchanged, forced, disabled and held-world frames preserve one pending read until consumption.</summary>
    [TestCase]
    public void SchedulingAcrossFeedAndConsumerTransitions()
    {
        bool pending=false, enabled=true, changed=false;
        int reads=0, takes=0, samples=0;
        Action sample=() => ++samples;
        Action kick=() => { if(enabled && !pending) { pending=true; ++reads; } };
        void Step(bool force, bool consume)
        {
            AndroidEntityFeed.SampleAndKick(force,sample,() => changed,kick);
            if(consume && pending) { pending=false; ++takes; }
        }
        Step(false,true); // unchanged: no scan
        changed=true;
        Step(false,false); Step(false,false); // held world: no duplicate pending scan
        Step(false,true); // world ready: takes the original result
        changed=false;
        Step(true,true); // force diagnostic also feeds an unchanged frame
        enabled=false; changed=true;
        Step(false,true); // synchronous fallback owns the read outside this scheduler
        enabled=true;
        Step(false,true); // re-enabled worker consumes a new result
        AssertThat(samples).IsEqual(7);
        AssertThat(reads).IsEqual(3);
        AssertThat(takes).IsEqual(3);
        AssertThat(pending).IsFalse();
    }

    /// <summary>The production host invokes the tested scheduler only inside its initialized guest guard.</summary>
    [TestCase]
    public void ProductionOwnerSchedulesBeforeUiAndWorld()
    {
        string source=HostSource();
        int guard=source.IndexOf("if (_failure.Length == 0 && _guest?.IsInitialized == true)",StringComparison.Ordinal);
        int schedule=source.IndexOf("AndroidEntityFeed.SampleAndKick",StringComparison.Ordinal);
        int ui=source.IndexOf("AttachFrontEnd();",schedule,StringComparison.Ordinal);
        AssertThat(guard>=0 && schedule>guard && ui>schedule).IsTrue();
        // The frame counter is sampled once per frame by Engine_FrameStartNode; the host only fills it before the first.
        AssertThat(source.Contains("if (Engine_GuestFrame.Frame == 0u) HostBridge.SampleGuestFrame();")).IsTrue();
        AssertThat(source.Contains("static () => Engine_GuestFrame.Changed, HostEntityWalk.Kick")).IsTrue();
    }

    /// <summary>Locates production source relative to this test without relying on a runner working directory.</summary>
    private static string HostSource([CallerFilePath] string source="") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,"../../src/android/AndroidGameHost.cs")));
}
