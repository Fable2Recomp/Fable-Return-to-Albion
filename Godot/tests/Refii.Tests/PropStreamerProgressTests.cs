namespace Refii.Tests;

using GdUnit4;
using Godot;
using Refii.Level;
using static GdUnit4.Assertions;

/// <summary>
/// The load's hold asks the streamers beneath the mounted region every frame. It gets the answer a walk
/// of the region's tree gives: every streamer beneath it, however deep, and none outside it.
/// </summary>
[TestSuite]
public class PropStreamerProgressTests
{
    private static Engine_PropStreamer Streamer(string name, int props)
    {
        var streamer = new Engine_PropStreamer { Name = name, Enabled = false };
        for (int i = 0; i < props; i++)
        {
            var prop = new MeshInstance3D { Name = $"Prop{i}", Visible = false };
            prop.SetMeta(Engine_PropStreamer.ModelPathMeta, $"no/such/model{i}.mdl");
            streamer.AddChild(prop);
        }
        return streamer;
    }

    [TestCase]
    [RequireGodotRuntime]
    public void TestOnlyTheStreamersBeneathTheRootCount()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var region = new Node3D { Name = "Region" };
        var nest = new Node3D { Name = "Nest" };
        var other = new Node3D { Name = "Other" };
        Engine_PropStreamer top = Streamer("Top", 2);
        Engine_PropStreamer deep = Streamer("Deep", 3);
        Engine_PropStreamer outside = Streamer("Outside", 5);
        region.AddChild(top);
        region.AddChild(nest);
        nest.AddChild(deep);
        other.AddChild(outside);
        tree.Root.AddChild(region);
        tree.Root.AddChild(other);
        try
        {
            int done = 0, total = 0, settling = 0;
            Engine_PropStreamer.SumProgress(region, ref done, ref total, ref settling);
            AssertThat(total).IsEqual(5);
            AssertThat(done).IsEqual(0);
            AssertThat(settling).IsEqual(0);

            Engine_PropStreamer.SetBudget(region, 100.0);
            AssertThat(top.MillisecondsPerFrame).IsEqual(100.0);
            AssertThat(deep.MillisecondsPerFrame).IsEqual(100.0);
            AssertThat(outside.MillisecondsPerFrame).IsEqual(Engine_PropStreamer.DefaultMsPerFrame);

            // Gone from the tree, gone from the count.
            tree.Root.RemoveChild(region);
            total = 0;
            Engine_PropStreamer.SumProgress(region, ref done, ref total, ref settling);
            AssertThat(total).IsEqual(0);
        }
        finally
        {
            Engine_PropStreamer.StopAll();
            region.Free();
            other.Free();
        }
    }
}
