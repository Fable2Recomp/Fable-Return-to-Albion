using System;
using System.Collections.Generic;
using System.Text.Json;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Tests admission, narrow tree selection and exact restore across repeated arms/world replacement without native nodes.</summary>
[TestSuite]
public sealed class TreeDistanceDiagnosticTests
{
    /// <summary>Applies a parsed request through the production policy with detached observations.</summary>
    private static void Apply(Engine_TreeDistanceDiagnostic owner, string json,
        Func<IEnumerable<Engine_TreeDistanceDiagnostic.Entry>> capture, Action<ulong, bool> sink)
    {
        using var document = JsonDocument.Parse(json);
        owner.ApplyRequest(document.RootElement, capture, sink);
    }

    /// <summary>Nearby trees, non-tree models, main models, hidden nodes and invalid distances retain their visibility.</summary>
    [TestCase]
    public void NarrowSelectionAndBoundary()
    {
        var owner = new Engine_TreeDistanceDiagnostic();
        var changed = new Dictionary<ulong, bool>();
        var entries = new[] {
            new Engine_TreeDistanceDiagnostic.Entry(1,"a/BW_Tree_LOD1.mdl",true,101),
            new Engine_TreeDistanceDiagnostic.Entry(2,"a/BW_Tree_LOD1.mdl",true,100),
            new Engine_TreeDistanceDiagnostic.Entry(3,"a/BW_Tree.mdl",true,200),
            new Engine_TreeDistanceDiagnostic.Entry(4,"tree/House_LOD1.mdl",true,200),
            new Engine_TreeDistanceDiagnostic.Entry(5,"a/BW_Tree_LOD1.mdl",false,200),
            new Engine_TreeDistanceDiagnostic.Entry(6,"a/BW_Tree_LOD1.mdl",true,double.NaN) };
        Apply(owner,"{\"treeDiagnosticDistance\":100}",()=>entries,(id,v)=>changed[id]=v);
        AssertThat(owner.Hidden).IsEqual(1);
        AssertThat(changed[1]).IsFalse();
        AssertThat(changed.Count).IsEqual(1);
        Apply(owner,"{\"treeDiagnosticDistance\":0}",()=>entries,(id,v)=>changed[id]=v);
        AssertThat(changed[1]).IsTrue();
        AssertThat(owner.Hidden).IsEqual(0);
        AssertThat(owner.Metres).IsEqual(0);
    }

    /// <summary>Alternating arms restore before observing; new-world nodes are independent of missing old-world nodes.</summary>
    [TestCase]
    public void ReapplyRestoreAndWorldReplacement()
    {
        var owner = new Engine_TreeDistanceDiagnostic();
        var live = new Dictionary<ulong,bool>{{1,true}};
        IEnumerable<Engine_TreeDistanceDiagnostic.Entry> Capture() {
            foreach(var p in live) yield return new(p.Key,"BW_Tree_LOD1.mdl",p.Value,150);
        }
        void Publish(ulong id,bool visible) { if(live.ContainsKey(id)) live[id]=visible; }
        Apply(owner,"{\"treeDiagnosticDistance\":100}",Capture,Publish);
        AssertThat(live[1]).IsFalse();
        Apply(owner,"{\"treeDiagnosticDistance\":200}",Capture,Publish);
        AssertThat(live[1]).IsTrue();
        Apply(owner,"{\"treeDiagnosticDistance\":100}",Capture,Publish);
        live.Clear(); live[2]=true;
        Apply(owner,"{\"treeDiagnosticDistance\":100}",Capture,Publish);
        AssertThat(live[2]).IsFalse();
        Apply(owner,"{\"treeDiagnosticDistance\":0}",Capture,Publish);
        AssertThat(live[2]).IsTrue();
        AssertThat(new Engine_TreeDistanceDiagnostic().Metres).IsEqual(0);
    }

    /// <summary>Malformed values cannot mutate a running arm; unrelated reads leave it intact.</summary>
    [TestCase]
    public void AdmissionBeforeMutation()
    {
        var owner = new Engine_TreeDistanceDiagnostic(); int calls=0;
        IEnumerable<Engine_TreeDistanceDiagnostic.Entry> Capture() => Array.Empty<Engine_TreeDistanceDiagnostic.Entry>();
        foreach(string value in new[]{"null","true","\"100\"","99","1001","100.5"}) {
            bool rejected=false;
            try { Apply(owner,"{\"treeDiagnosticDistance\":"+value+"}",Capture,(_,_)=>calls++); }
            catch(JsonException) { rejected=true; }
            AssertThat(rejected).IsTrue();
        }
        Apply(owner,"{}",Capture,(_,_)=>calls++);
        AssertThat(calls).IsEqual(0);
        AssertThat(owner.Metres).IsEqual(0);
    }
}
