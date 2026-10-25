using Godot;
using Refii.Entities;
using Refii.Game;

namespace Refii.Hero;

[Tool]
public partial class Engine_MorphMirror : Node3D
{
    [Export] public Engine_MorphMirrorPlacement? Placement { get; set; }
    private MeshInstance3D? _mirror;

    public override void _Ready()
    {
        if (Placement is not { Enabled: true } placement || placement.Models.Count != placement.Poses.Count) return;
        for (int i = 0; i < placement.Models.Count; i++)
        {
            string name = i == 0 ? "MorphMirror" : "Prop" + i;
            if (GetNodeOrNull<MeshInstance3D>(name) is { } existing)
            {
                if (i == 0) _mirror = existing;
                continue;
            }
            var mesh = ResourceLoader.Load<ArrayMesh>("game://" + placement.Models[i]);
            if (mesh is null) continue;
            var model = new MeshInstance3D { Name = name, Mesh = mesh, Transform = placement.Poses[i] };
            model.SetMeta(Refii.Level.LevelRecordMetadata.ModelPathMeta, placement.Models[i]);
            AddChild(model);
            model.Owner = this;
            if (i == 0) { _mirror = model; model.SetMeta(Engine_MorphMirrorPlacements.Marker, true); }
        }
        if (!Engine.IsEditorHint()) GameMorphMirror.Register(this);
    }

    public void Highlight(uint word)
    {
        if (_mirror is not null)
            Engine_EntityHighlight.Apply(_mirror, word != 0x000000FFu,
                Color.Color8((byte)(word >> 8), (byte)(word >> 16), (byte)(word >> 24)), word);
    }

    public override void _ExitTree()
    {
        if (!Engine.IsEditorHint()) GameMorphMirror.Unregister(this);
    }
}
