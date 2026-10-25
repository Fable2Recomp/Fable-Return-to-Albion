using Godot;
using Refii.Save;

namespace Refii.Hero;

[Tool]
public partial class Engine_MorphMirrorPlacement : Engine_GoldenCockPlacement
{
    [Export] public string SourceScene { get; set; } = string.Empty;
    [Export] public string InstancePath { get; set; } = string.Empty;
    [Export] public Godot.Collections.Array<string> Models { get; set; } = new();
    [Export] public Godot.Collections.Array<Transform3D> Poses { get; set; } = new();
    [Export] public Transform3D HeroPose { get; set; } = Transform3D.Identity;
    [Export] public Vector3 CameraPosition { get; set; }
    [Export] public float CameraFov { get; set; } = 60;
}
