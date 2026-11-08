namespace Refii.Tests;

using System.Collections;
using System.Reflection;
using GdUnit4;
using Godot;
using Refii.Entities;
using Refii.Level;
using static GdUnit4.Assertions;

/// <summary>
/// The twins' model index reads a batch's model once. Every instance is still indexed under the batch's
/// key and in order, a single under its own, and a node that states no model under none.
/// </summary>
[TestSuite]
public class StaticTwinsIndexTests
{
    private static object Field(Engine_StaticTwins twins, string name) =>
        typeof(Engine_StaticTwins).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(twins)!;

    private static object Member(object value, string name)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        System.Type type = value.GetType();
        return type.GetProperty(name, Any)?.GetValue(value) ?? type.GetField(name, Any)!.GetValue(value)!;
    }

    [TestCase]
    [RequireGodotRuntime]
    public void TestEveryInstanceIsIndexedUnderItsBatchsModel()
    {
        var region = new Node3D { Name = "Region" };
        var props = new Node3D { Name = Engine_LevelProps.NodeName };
        region.AddChild(props);
        var batch = new MultiMeshInstance3D
        {
            Name = "Batch",
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = 3 },
        };
        batch.SetMeta(Engine_PropStreamer.ModelPathMeta, "Art\\Foliage\\RS_Pine_01\\RS_Pine_01.mdl");
        batch.SetMeta(Engine_PropStreamer.RecordsMeta, new long[] { 11, 0, 13 });
        props.AddChild(batch);
        var single = new MeshInstance3D { Name = "Jug" };
        single.SetMeta(Engine_PropStreamer.ModelPathMeta, "Art/Props/BS_Jug.mdl");
        single.SetMeta(Engine_PropStreamer.RecordMeta, 21L);
        props.AddChild(single);
        var bare = new MeshInstance3D { Name = "Bare" };
        bare.SetMeta(Engine_PropStreamer.RecordMeta, 22L);
        props.AddChild(bare);
        try
        {
            Engine_StaticTwins twins = Engine_StaticTwins.Build(region);
            AssertThat(twins.Instances).IsEqual(4);

            var byModel = new System.Collections.Generic.Dictionary<string, string>();
            foreach (object pair in (IEnumerable)Field(twins, "_byModel"))
            {
                var held = new System.Collections.Generic.List<string>();
                foreach (object twin in (IEnumerable)Member(pair, "Value"))
                {
                    held.Add($"{((Node3D)Member(twin, "Node")).Name}/{Member(twin, "Instance")}");
                }
                byModel[(string)Member(pair, "Key")] = string.Join(' ', held);
            }

            AssertThat(byModel.Count).IsEqual(2);
            AssertThat(byModel["rs_pine_01"]).IsEqual("Batch/0 Batch/1 Batch/2");
            AssertThat(byModel["bs_jug"]).IsEqual("Jug/-1");
            AssertThat(((ICollection)Field(twins, "_modelOf")).Count).IsEqual(4);
        }
        finally { region.Free(); }
    }
}
