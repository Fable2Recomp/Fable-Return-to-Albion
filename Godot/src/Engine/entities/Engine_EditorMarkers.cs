using Godot;

namespace Refii.Entities;

[Tool]
[GlobalClass]
public partial class Engine_EditorMarkers : Node3D
{
	public override void _Ready()
	{
		Visible = Engine.IsEditorHint();
	}
}
