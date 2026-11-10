#if TOOLS
using Godot;

namespace Refii.EditorTools;

/// <summary>
/// The player's layout tool: the HUD placement editor (<see cref="UiLayoutEditor"/>) over the
/// pieces the game last snapshotted at their place beneath the player's file
/// (<c>user://hud/layout.snapshot.json</c>: shipped, then the mods' layout), writing the player's
/// <c>user://hud/layout.json</c> (read at boot, over the shipped and the mods' arrangement), and the
/// menu editor dock over <c>user://ui/templates.json</c>. The same two editors the Mod Studio's UI
/// tab uses, pointed at the player's files.
/// </summary>
[Tool]
public partial class HudEditorPlugin : EditorPlugin
{
	private UiLayoutEditor? _dock;
	private MenuEditorDock? _menus;

	public override void _EnterTree()
	{
		if (DisplayServer.GetName() == "headless")
		{
			return;
		}

		_dock = new UiLayoutEditor { Name = "HUD Layout", Saves = true, TargetName = PlayerHudLayoutTarget.Name };
		_dock.SaveRequested += Save;
		_dock.ReloadRequested += Reload;

#pragma warning disable CS0618 // AddControlToDock is deprecated in Godot 4.4+ in favor of AddDock(EditorDock)
		AddControlToDock(DockSlot.RightUl, _dock);

		_menus = new MenuEditorDock();
		AddControlToDock(DockSlot.RightBl, _menus);
#pragma warning restore CS0618

		Reload();
	}

	public override void _ExitTree()
	{
#pragma warning disable CS0618 // RemoveControlFromDocks is deprecated in Godot 4.4+ in favor of RemoveDock(EditorDock)
		if (_dock is not null)
		{
			RemoveControlFromDocks(_dock);
			_dock.QueueFree();
			_dock = null;
		}

		if (_menus is not null)
		{
			RemoveControlFromDocks(_menus);
			_menus.QueueFree();
			_menus = null;
		}
#pragma warning restore CS0618
	}

	/// <summary>The snapshot's pieces with the player's own entries over them.</summary>
	private void Reload()
	{
		if (_dock is null)
		{
			return;
		}

		if (PlayerHudLayoutTarget.Load() is not { } loaded)
		{
			_dock.Load(System.Array.Empty<HudLayoutPiece>(), new System.Collections.Generic.Dictionary<string, Refii.Ui.Engine_HudModLayout.Move>());
			_dock.CallDeferred(UiLayoutEditor.MethodName.SetStatus,
				$"No snapshot at {Refii.Ui.Engine_HudLayout.ActiveSnapshotPath} yet. Run the game once — it writes the derived layout there on boot.");
			return;
		}

		_dock.Load(loaded.Pieces, loaded.Stated);
		if (!PlayerHudLayoutTarget.SnapshotIsBeneath())
		{
			_dock.CallDeferred(UiLayoutEditor.MethodName.SetStatus,
				$"{Refii.Ui.Engine_HudLayout.ActiveSnapshotPath} was written by an older build over your own layout, so " +
				"\"back to the place beneath\" returns a piece to where your file put it. Run the game once to refresh it.");
		}
	}

	/// <summary>Writes the pieces moved this session; the file's other entries stay.</summary>
	private void Save()
	{
		if (_dock is null)
		{
			return;
		}

		Refii.Ui.Engine_HudLayoutEdit.Changes changes = _dock.Changes;
		string error = PlayerHudLayoutTarget.Save(changes);
		if (error.Length != 0)
		{
			_dock.SetStatus("⚠ " + error);
			return;
		}

		Reload();
		_dock.SetStatus(changes.Empty
			? "Nothing to save."
			: $"Saved {changes.Set.Count} moved and {changes.Removed.Count} reverted piece(s) to " +
				$"{Refii.Ui.Engine_HudLayout.ActiveLayoutPath}. The game applies it on boot.");
	}
}
#endif
