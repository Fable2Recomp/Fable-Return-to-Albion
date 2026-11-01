using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// The HUD's other widgets as pieces (<see cref="Engine_HudWidgets"/>), and the HUD layout derived
/// again from the files as they stand.
/// </summary>
/// <remarks>
/// Each widget layer hangs under a MOUNT of its own: the mount carries what the mods state — hidden
/// (the mount is down, whatever the layer's own drive says), the move (a shift in the layer's seat)
/// — and the layer keeps its seat transform and its own drive. A mod's scale is the widget's own
/// <c>PieceScale</c>, so each mark grows about its point. A mod's copy of a widget is an
/// <see cref="Engine_HudWidgets.Copy"/> beside the layer in the same mount.
/// </remarks>
public partial class GameUi
{
	/// <summary>One drawn widget: its reader's name, whether it is the copy seated for player two, its layer and mount.</summary>
	private sealed class HudWidgetSlot
	{
		public required string Name { get; init; }
		public required bool SeatTwo { get; init; }
		public required Node3D Layer { get; init; }
		public required Node3D Mount { get; init; }
		public List<(Engine_HudWidgets.Copy Copy, Engine_HudModPieces.Clone Clone)> Copies { get; } = new();
	}

	private readonly List<HudWidgetSlot> _hudWidgets = new();

	/// <summary>The layout on <see cref="_hud"/> was derived this build; the view's first build keeps it.</summary>
	private bool _hudLayoutFresh;

	/// <summary>Puts a widget layer under its own mount on the presentation, so mods can address it.</summary>
	private void MountHudWidget(string name, bool seatTwo, Node3D layer)
	{
		var mount = new Node3D { Name = layer.Name + "Piece" };
		mount.AddChild(layer);
		_presentation!.AddChild(mount);
		_hudWidgets.Add(new HudWidgetSlot { Name = name, SeatTwo = seatTwo, Layer = layer, Mount = mount });
	}

	/// <summary>
	/// The HUD's layout derived from the document AS THE MOUNT STANDS NOW (<see cref="Engine_HudView.Derive"/>):
	/// the bindings of `hud.bgf` — a mod's own when it replaces the file — then the mods' `layout`
	/// blocks, the snapshot beneath the player's file, then the player's file. A new document
	/// rebinds the element resolve and the world icons to it.
	/// </summary>
	private (Engine_UiScreen Screen, Engine_HudLayout Layout)? DeriveHudLayout()
	{
		if (_hudLayoutFresh)
		{
			_hudLayoutFresh = false;
			return null;
		}

		if (_presentation is null || _presentation.DocumentFor(HudPresentation) is not { } screen)
		{
			return null;
		}

		if (!ReferenceEquals(screen, _hudScreen))
		{
			// The element pipeline joins the guest's elements to the document by ordinal; a new
			// document starts a new join. The guest's own copy of the file is the guest's: until it
			// states as many elements as this document has, the count guard holds the feed back.
			_hudScreen = screen;
			_hudResolve = null;
			foreach (Engine_HudWorldIcons? icons in new[] { _worldIcons, _worldIconsTwo })
			{
				if (icons is not null && GodotObject.IsInstanceValid(icons))
				{
					icons.Bind(screen);
				}
			}

			GD.Print($"[hud] '{HudPresentation}' changed under the mount — the layout is derived again from the new document " +
				$"({screen.Nodes.Length} element(s))");
		}

		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
		Engine_HudLayout layout = Engine_HudLayout.FromBindings(
			screen, bindings, HostAppliesWidescreen ? Engine_UiWidescreen.Read(screen.Document) : null);
		DirAccess.MakeDirRecursiveAbsolute("user://hud");
		int modArranged = layout.ApplyModLayout();
		layout.ApplyPlatformDefaults();
		layout.Save(HudLayoutSnapshotPath, Engine_HudLayout.SnapshotLayer);
		int arranged = layout.ApplyOverrides(HudLayoutPath);

		var ids = new List<string>();
		foreach (Engine_HudLayout.Piece piece in layout.Pieces)
		{
			ids.Add(piece.Id);
		}

		int replaced = _hud?.Art.LoadOverrides("user://hud", ids) ?? 0;
		GD.Print($"[hud] layout derived again: {bindings.Describe()}; {replaced} piece(s) replaced from user://hud" +
			(modArranged == 0 ? string.Empty : $"; {modArranged} piece(s) moved by mods") +
			(arranged == 0 ? string.Empty : $"; {arranged} piece(s) moved by {HudLayoutPath}"));
		return (screen, layout);
	}

	/// <summary>
	/// Player one's HUD was built (at boot, on a remount, a new `hud.bgf`, a change of local
	/// player): player two's view takes the same document and layout and is built again, and the
	/// widgets take the mods' statements.
	/// </summary>
	private void OnHudRebuilt(Engine_HudView view)
	{
		if (view != _hud)
		{
			return;
		}

		if (_hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
		{
			_hudTwo.Screen = view.Screen;
			_hudTwo.Layout = view.Layout;
			_hudTwo.Build();
		}

		ApplyHudWidgetCopies();
	}

	/// <summary>
	/// The mods' copies of each widget (<c>clones</c> whose <c>of</c> names a widget's id as this
	/// machine draws it), built again: one <see cref="Engine_HudWidgets.Copy"/> per clone, in the
	/// original's mount.
	/// </summary>
	private void ApplyHudWidgetCopies()
	{
		Engine_HudModPieces.ModLayout? mods = _hud is not null && GodotObject.IsInstanceValid(_hud) ? _hud.ModLayout : null;
		bool joiner = CoopSession.IsClient && !CoopSession.LocalCoop;
		foreach (HudWidgetSlot slot in _hudWidgets)
		{
			foreach ((Engine_HudWidgets.Copy copy, _) in slot.Copies)
			{
				if (GodotObject.IsInstanceValid(copy))
				{
					copy.QueueFree();
				}
			}

			slot.Copies.Clear();
			if (mods is null || !GodotObject.IsInstanceValid(slot.Mount))
			{
				continue;
			}

			string id = Engine_HudWidgets.DeclaredId(slot.Name, slot.SeatTwo, joiner);
			foreach (Engine_HudModPieces.Clone clone in mods.Clones)
			{
				if (clone.Of != id)
				{
					continue;
				}

				var copy = new Engine_HudWidgets.Copy { Name = clone.Id.Replace('.', '_'), Source = slot.Layer };
				slot.Mount.AddChild(copy);
				slot.Copies.Add((copy, clone));
				GD.Print($"[hud-mods] '{clone.Id}' ({clone.Mod}) is a copy of the widget '{id}'");
			}
		}
	}

	/// <summary>
	/// Every frame: each widget's mount takes what the mods and the layout state for the id this
	/// machine draws it as — hidden, its move from home in its seat, its scale — and its copies
	/// their own offsets. A widget nobody addresses sits at home: an identity mount.
	/// </summary>
	private void ApplyHudWidgets(Vector2 window)
	{
		if (_hud is null || !GodotObject.IsInstanceValid(_hud) || _hud.ModLayout is not { } mods ||
			window.X <= 0f || window.Y <= 0f)
		{
			return;
		}

		bool split = Engine_Views.Split;
		bool joiner = CoopSession.IsClient && !CoopSession.LocalCoop;
		foreach (HudWidgetSlot slot in _hudWidgets)
		{
			if (!GodotObject.IsInstanceValid(slot.Layer) || !GodotObject.IsInstanceValid(slot.Mount))
			{
				continue;
			}

			string id = Engine_HudWidgets.DeclaredId(slot.Name, slot.SeatTwo, joiner);
			bool toast = slot.Name == Engine_HudWidgets.AchievementToast;
			Engine_UiSeats.Frame frame = Engine_UiSeats.Of(window,
				toast || !split ? Engine_UiSeats.Seat.Whole
				: slot.SeatTwo ? Engine_UiSeats.Seat.PlayerTwo : Engine_UiSeats.Seat.PlayerOne);
			Vector2 extent = frame.Extent;
			Engine_HudWidgets.Widget? widget = Engine_HudWidgets.Find(id);
			Engine_HudLayout.Piece piece = _hud.Layout.Find(id) ??
				new Engine_HudLayout.Piece(id, slot.Name, widget?.Home ?? Engine_HudLayout.Anchor.Centre, Vector2.Zero);
			Engine_HudWidgets.State state = Engine_HudWidgets.StateOf(
				id, mods.Hidden, mods.Scales, piece.Anchor, piece.Offset, extent);

			if (slot.Mount.Visible == state.Hidden)
			{
				slot.Mount.Visible = !state.Hidden;
			}

			Basis seat = frame.Transform(0f).Basis;
			float units = Engine_HudLayout.Scale(extent);
			Vector3 shift = seat * new Vector3(state.Shift.X * units, -state.Shift.Y * units, 0f);
			if (slot.Mount.Position != shift)
			{
				slot.Mount.Position = shift;
			}

			SetPieceScale(slot.Layer, state.Scale);

			if (slot.Copies.Count == 0)
			{
				continue;
			}

			// The home point in the layer's own space, for a copy's scale.
			Engine_HudLayout.Anchor home = widget?.Home ?? Engine_HudLayout.Anchor.Centre;
			Vector2 homeCanvas = Engine_HudLayout.Place(new Engine_HudLayout.Piece(id, string.Empty, home, Vector2.Zero), extent);
			Vector3 homeMount = frame.Transform(0f) * new Vector3(homeCanvas.X, homeCanvas.Y, 0f);
			Vector3 pivot = slot.Layer.Transform.AffineInverse() * homeMount;
			foreach ((Engine_HudWidgets.Copy copy, Engine_HudModPieces.Clone clone) in slot.Copies)
			{
				if (!GodotObject.IsInstanceValid(copy))
				{
					continue;
				}

				Engine_HudWidgets.State own = Engine_HudWidgets.CopyState(state, clone.Offset, mods.Scales, clone.Id);
				Vector3 offset = seat * new Vector3(clone.Offset.X * units, -clone.Offset.Y * units, 0f);
				copy.Shift = new Vector2(offset.X, offset.Y);
				copy.ScaleFactor = state.Scale > 0f ? own.Scale / state.Scale : 1f;
				copy.Pivot = new Vector2(pivot.X, pivot.Y);
			}
		}
	}

	/// <summary>A mod's scale onto the widget that owns its marks.</summary>
	private void SetPieceScale(Node3D layer, float scale)
	{
		switch (layer)
		{
			case Engine_HudReticule reticule:
				reticule.PieceScale = scale;
				break;
			case Engine_HudSlowTimeDial dial:
				dial.PieceScale = scale;
				break;
			case Engine_HudWorldIcons icons:
				icons.PieceScale = scale;
				break;
			case Engine_HudWorldSprites sprites:
				sprites.PieceScale = scale;
				break;
			default:
				if (layer == _toastMount && _toast is not null && GodotObject.IsInstanceValid(_toast))
				{
					_toast.PieceScale = scale;
				}

				break;
		}
	}
}
