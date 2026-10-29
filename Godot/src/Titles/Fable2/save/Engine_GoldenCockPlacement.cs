using Godot;

namespace Refii.Titles.Fable2;

using Refii.Save;

[Tool]
public partial class Engine_GoldenCockPlacement : Resource
{
    [Export] public string World { get; set; } = "albion";
    [Export] public string LevelPath { get; set; } = string.Empty;
    [Export] public string LevelName { get; set; } = string.Empty;
    [Export] public string Scenario { get; set; } = string.Empty;
    [Export] public Transform3D Pose { get; set; } = Transform3D.Identity;
    [Export] public bool Enabled { get; set; }
}
