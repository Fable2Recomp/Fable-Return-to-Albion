#if TOOLS
using Godot;

namespace Refii.EditorTools;

/// <summary>
/// The player's menu dock: the menu editor (<see cref="UiMenuEditor"/>) over the records the game
/// last snapshotted, writing the menus the player changed — whole, every field round-tripped — into
/// <c>user://ui/templates.json</c> and keeping every other record and section of that file. The
/// same editor the Mod Studio's UI tab uses, pointed at the player's file.
/// </summary>
[Tool]
public sealed partial class MenuEditorDock : VBoxContainer
{
	public override void _Ready()
	{
		Name = "UI Menus";
		var editor = new UiMenuEditor { Saves = true, SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild(editor);
		editor.Bind(new PlayerMenuTarget());
		if (editor.Names.Count == 0)
		{
			editor.SetStatus($"No menus at {Refii.Ui.Engine_UiTemplates.SnapshotPath}. Run the game once — it snapshots " +
				"every template on boot.");
		}
	}
}
#endif
