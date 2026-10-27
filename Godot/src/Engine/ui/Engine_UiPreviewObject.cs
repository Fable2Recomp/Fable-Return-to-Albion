using Godot;

namespace Refii.Ui;

/// <summary>
/// The 3D object a menu panel shows — the spinning weapon, item or gift inside the panel's
/// ring. Godot-owned: the guest names the model and states the spin; we load and draw it.
/// </summary>
/// <remarks>
/// There is no mesh element type — the ring's interior is a plain transform anchor
/// (`…model_proxy.model`, `Foreground.SpinningCube.Cube`). The original resolves the item's GDB
/// `InventoryItemComponent.GUIScreenMesh` to a model resource hash, spawns a real scene entity
/// and places it at the anchor; no render-to-texture is involved.
///
/// From the guest: the model hash (in the currency
/// <see cref="Refii.Level.Engine_PropModelIndex"/> indexes), the per-item offsets
/// (`GUIScreenTranslationOffset` / `GUIScreenRotationOffset` / `GUIScreenScaleMultiplier` /
/// `GUIScreenRotationAxis`), and the spin, written as `rotation.x/y/z` on the anchor element
/// every frame.
///
/// DRAWN ON THE GUI STAGE (<see cref="Engine_UiFigureStage"/>), the original's own lit
/// perspective world, as the co-op figures are: this node keeps the placement — anchor, offsets,
/// axis swizzle, spin, fit — on a hidden mesh, and the stage draws its twin with the model's own
/// materials (Engine_UiStageMaterial), glass see-through and cutouts cut.
/// </remarks>
public partial class Engine_UiPreviewObject : Node3D
{
	/// <summary>
	/// UI canvas units per world unit — one, because `GUIScreenScaleMultiplier` is itself the
	/// whole conversion, authored per item (790.0 for a 0.17-unit card, 168.3 for a 1.17-unit
	/// sword: 134 and 197 canvas units inside a 442-unit ring). Do not also apply the guest's
	/// 0.01 UI-to-world step from `sub_828C3C78`.
	/// </summary>
	public const float CanvasUnitsPerWorldUnit = 1f;

	/// <summary>
	/// The authored panel this object shows through, in canvas units, or 0 for unbounded. A
	/// bound, not a layout: the model is fitted into it, and <see cref="LastReport"/> carries the
	/// factor that had to be applied.
	/// </summary>
	public float PanelSize { get; set; }

	/// <summary>The model currently shown, so a re-state with the same item is free.</summary>
	public uint ModelHash { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private MeshInstance3D? _mesh;

	/// <summary>
	/// The guest's stated placement for one item, as read off its preview object.
	/// </summary>
	/// <param name="Translation">`GUIScreenTranslationOffset`, in the guest's UI space.</param>
	/// <param name="Rotation">`GUIScreenRotationOffset`, radians.</param>
	/// <param name="Scale">`GUIScreenScaleMultiplier`; 0 means the artist stated none.</param>
	/// <param name="RotationAxis">`GUIScreenRotationAxis`, 1..6 — the euler swizzle and the
	/// composition order `sub_828C3C78` applies; see <see cref="Stated"/>.</param>
	/// <param name="Hung">
	/// True to hang the model by its ORIGIN, as `sub_828C3C78` does — the artist's translation
	/// offset is tuned against the origin. False centres the mesh's bounds on the anchor, for
	/// the placements that are ours: a live body, and the clothing book's item-alone view,
	/// whose stated offset is a mannequin's and is dropped.
	/// </param>
	public readonly record struct Placement(
		Vector3 Translation, Vector3 Rotation, float Scale, int RotationAxis, bool Hung = false);

	/// <summary>
	/// Shows the model the guest named, or clears the panel when it names none. True while an
	/// object is standing; a hash that resolves to no indexed model leaves the panel empty rather
	/// than substituting art.
	/// </summary>
	/// <summary>The preview mesh's node name — how the paint probe recognises it.</summary>
	public const string MeshName = "PreviewModel";

	public bool Show(
		uint modelHash, Placement placement,
		System.Collections.Generic.IReadOnlyList<Refii.Host.HostBridge.GuestTextureComposite>? dye = null)
	{
		ulong dyeSignature = DyeSignature(dye);
		if (modelHash == ModelHash && dyeSignature == _dyeSignature)
		{
			// AND RE-ASSERT VISIBILITY. This path restored the placement but never the
			// visibility, while every failure below and both null-frame sites in the caller
			// set it false. The guest's model hash is STICKY, so one frame that answered
			// nothing — the screen id read a tick early, the spawned byte cleared as the entity
			// retired, a sample gap — latched the preview off for the rest of that card AND for
			// the next pickup of the same item, because the hash matched and this path took
			// over. That is the empty oval that never comes back.
			Apply(placement);
			Visible = _mesh is not null;
			return _mesh is not null;
		}

		ModelHash = modelHash;
		_dyeSignature = dyeSignature;
		_liveKey = 0;
		_mesh?.QueueFree();
		_mesh = null;

		if (modelHash == 0)
		{
			LastReport = "the guest names no model";
			Visible = false;
			return false;
		}

		string? path = Refii.Level.Engine_PropModelIndex.Resolve(modelHash, System.Array.Empty<string>());
		if (path is null)
		{
			LastReport = $"0x{modelHash:X8} is not in the model index";
			Visible = false;
			return false;
		}

		Mesh? mesh;
		try
		{
			mesh = ResourceLoader.Load<Mesh>(Refii.Models.Engine_BnkMeshLoader.PathPrefix + path);
		}
		catch (System.Exception exception)
		{
			LastReport = $"{path}: {exception.Message}";
			Visible = false;
			return false;
		}

		if (mesh is null)
		{
			LastReport = $"{path} loaded no mesh";
			Visible = false;
			return false;
		}

		// A DYED GARMENT SHOWS DYED. The guest's own dye builder states the item's layers
		// (RefiiHostGetItemDyeLayers) and the hero's compositor lays them on a PRIVATE copy of
		// the model — the shared mesh keeps its materials for every other holder.
		string dyed = string.Empty;
		if (dye is { Count: > 0 } && mesh is ArrayMesh shared)
		{
			var own = (ArrayMesh)shared.Duplicate();
			Refii.Hero.Engine_HeroTextureMorph.ApplyTo(own, dye);
			mesh = own;
			dyed = $", dyed ({dye.Count} composite(s))";
		}

		_mesh = new MeshInstance3D { Name = MeshName, Mesh = mesh, Visible = false };
		AddChild(_mesh);
		Apply(placement);
		LastReport = $"{path}{dyed}: {Draw() ?? "on the stage"}";
		Visible = true;
		return true;
	}

	/// <summary>
	/// <summary>
	/// Shows a mesh the caller already holds — a LIVE ENTITY's composed body, not a model the guest
	/// named by hash: the clothing book's mannequin, whose figure the entity feed composes from
	/// component 17. Presented exactly as <see cref="Show"/> presents a model, keyed by
	/// <paramref name="key"/> (the entity address) so a frame that shows the same body again only
	/// re-applies the placement. The co-op screen's figures are not drawn here — they stand on
	/// <see cref="Engine_UiFigureStage"/>, the original's own perspective stage.
	/// </summary>
	/// <param name="skeleton">
	/// The feed's Skeleton3D driving that body, or null. A skinned mesh drawn without its
	/// skeleton renders in BIND pose whatever the guest animates, so the copy here is bound to
	/// the same skeleton the feed poses — the pose then comes through as the feed writes it.
	/// </param>
	/// <param name="skin">
	/// The feed node's own <see cref="Skin"/> for that mesh. Without it Godot binds the copy by
	/// skeleton bone ORDER, and a redressed body is a new mesh over a rebuilt bone table — the
	/// vertices landed on the wrong bones and the figure exploded into spikes.
	/// </param>
	public bool ShowLive(
		Mesh? mesh, uint key, Placement placement, Skeleton3D? skeleton = null, Skin? skin = null)
	{
		if (skeleton is not null && _mesh is not null && GodotObject.IsInstanceValid(skeleton) &&
			skeleton.IsInsideTree() && _mesh.IsInsideTree())
		{
			NodePath path = _mesh.GetPathTo(skeleton);
			if (_mesh.Skeleton != path)
			{
				_mesh.Skeleton = path;
			}
		}

		// THE LAST BODY STANDS when the feed has no node this frame. The co-op screen's temp
		// henchman drops out of the entity walk between samples (the walk is truncated, not the
		// entity gone), and the feed retires its node each time; the mesh resource captured
		// here outlives that node, so the panel keeps showing it for the same entity.
		if (key != 0 && key == _liveKey && _mesh is not null)
		{
			// THE SAME BODY, REDRESSED. The feed composes a body by its part list and builds a
			// new mesh when the parts change — the clothing mannequin trying an outfit on — so
			// the copy follows the feed's current mesh; only a frame with NO mesh keeps the last.
			// The rig can be rebuilt a frame after the mesh, so the skin is followed on its own.
			if (skin is not null && !ReferenceEquals(_mesh.Skin, skin))
			{
				_mesh.Skin = skin;
			}

			if (mesh is not null && !ReferenceEquals(_mesh.Mesh, mesh))
			{
				_mesh.Mesh = mesh;
			}

			Apply(placement);
			if (Draw() is { } redressed)
			{
				LastReport = $"live entity 0x{key:X8} redressed: {redressed}";
			}

			Visible = true;
			return true;
		}

		if (mesh is null || key == 0)
		{
			LastReport = "no live body to show";
			Visible = false;
			return false;
		}

		ModelHash = 0;
		_liveKey = key;
		_mesh?.QueueFree();
		_mesh = new MeshInstance3D { Name = MeshName, Mesh = mesh, Skin = skin, Visible = false };
		AddChild(_mesh);
		if (skeleton is not null && GodotObject.IsInstanceValid(skeleton) && skeleton.IsInsideTree() &&
			_mesh.IsInsideTree())
		{
			_mesh.Skeleton = _mesh.GetPathTo(skeleton);
		}

		Apply(placement);
		LastReport = $"live entity 0x{key:X8}: {Draw() ?? "on the stage"}";
		Visible = true;
		return true;
	}

	/// <summary>The entity a live body was last shown for; 0 when the hash path owns the mesh.</summary>
	private uint _liveKey;

	/// <summary>The dye layers the shown model was composited with, as a hash; 0 for none.</summary>
	private ulong _dyeSignature;

	private static ulong DyeSignature(
		System.Collections.Generic.IReadOnlyList<Refii.Host.HostBridge.GuestTextureComposite>? dye)
	{
		if (dye is null || dye.Count == 0)
		{
			return 0UL;
		}

		ulong hash = 14695981039346656037UL;
		foreach (Refii.Host.HostBridge.GuestTextureComposite composite in dye)
		{
			hash = (hash ^ composite.Original ^ (composite.Overlay ? 1UL : 0UL)) * 1099511628211UL;
			foreach (Refii.Host.HostBridge.GuestTextureLayer layer in composite.Layers)
			{
				hash = (hash ^ layer.Texture) * 1099511628211UL;
				hash = (hash ^ (uint)layer.Mode) * 1099511628211UL;
				hash = (hash ^ System.BitConverter.SingleToUInt32Bits(layer.Weight)) * 1099511628211UL;
				hash = (hash ^ (layer.HasTint ? layer.Tint : 0u)) * 1099511628211UL;
				hash = (hash ^ System.BitConverter.SingleToUInt32Bits(layer.Brightness)) * 1099511628211UL;
				hash = (hash ^ layer.Bump) * 1099511628211UL;
			}
		}

		return hash == 0UL ? 1UL : hash;
	}

	/// <summary>The GUI stage this preview's model is drawn on, created with the first model.</summary>
	private Engine_UiFigureStage? _stage;

	/// <summary>
	/// OVER THE PAGE BY DEPTH. The stage's quad stands this far in front of the anchor in canvas
	/// units: the item books' anchor lies 350 behind the parchment (`model_proxy.model` z 1900,
	/// `frames_page` z 2249).
	/// </summary>
	private const float PageClearance = 400f;

	/// <summary>
	/// Draws the hidden placement mesh's twin on the stage. The stage is seated on the canvas at
	/// the view's centre, <see cref="PageClearance"/> in front of the anchor, and covers the
	/// visible canvas; the model stands at its canvas point measured from the anchor's depth, so
	/// the anchor's plane is the stage's 40 m plane and the model's size there is its canvas
	/// size. Returns the stage's dressing report when the model changed.
	/// </summary>
	private string? Draw()
	{
		if (_mesh is null || !IsInsideTree() || GetViewport() is not { } viewport)
		{
			_stage?.HideAll();
			return null;
		}

		Transform3D own = Engine_UiTheme.CanvasTransform(this);
		if (Mathf.Abs(own.Basis.Determinant()) < 1e-9f)
		{
			// Scaled away by its anchor: nothing stands.
			_stage?.HideAll();
			return null;
		}

		if (_stage is null)
		{
			_stage = new Engine_UiFigureStage { Name = "PreviewStage" };
			AddChild(_stage);
		}

		_stage.Transform = own.AffineInverse() *
			new Transform3D(Basis.Identity, new Vector3(0f, 0f, own.Origin.Z + PageClearance));
		_stage.Frame(
			Engine_UiSceneBuilder.VisibleHalfWidth,
			Engine_UiSceneBuilder.CanvasHeight / Engine_UiSceneBuilder.PixelsPerUnit,
			(Vector2I)viewport.GetVisibleRect().Size);

		Transform3D placed = Engine_UiTheme.CanvasTransform(_mesh);
		placed.Origin = new Vector3(placed.Origin.X, placed.Origin.Y, placed.Origin.Z - own.Origin.Z);
		return _stage.Mirror(0, _mesh, placed);
	}

	public override void _Process(double delta)
	{
		if (_mesh is not null && IsVisibleInTree())
		{
			Draw();
		}
		else if (_stage is not null && _stage.Visible)
		{
			_stage.HideAll();
		}
	}

	/// <summary>
	/// The model's extent on the canvas, from its placement mesh, or null when nothing stands —
	/// what a press must land near to grab it.
	/// </summary>
	public Rect2? CanvasBounds()
	{
		if (_mesh?.Mesh is null || !IsVisibleInTree())
		{
			return null;
		}

		Aabb box = _mesh.GlobalTransform * _mesh.GetAabb();
		return new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
	}

	/// <summary>The player's turn of the model about the panel's vertical, in degrees.</summary>
	public void Spin(float yawDegrees)
	{
		if (_mesh is null)
		{
			return;
		}

		_yaw = Mathf.DegToRad(yawDegrees);
		Orient();
		Draw();
	}

	private void Orient()
	{
		if (_mesh is null)
		{
			return;
		}

		// A live body's pose is the host's: a plain turn about the vertical over its euler.
		Basis turned = _liveKey != 0
			? new Basis(Vector3.Up, _yaw) * Basis.FromEuler(_placed.Rotation)
			: Stated(_yaw, _placed.Rotation, _placed.RotationAxis);
		_mesh.Basis = turned.Scaled(Vector3.One * _drawnScale);
		Centre();
	}

	/// <summary>
	/// The orientation `sub_828C3C78` gives the preview entity, in Godot's axes.
	///
	/// The guest reads the anchor's `rotation.x/y/z` as R and swizzles it by the axis enum
	/// (jump table 0x828C3D3C): 1/4 E = (R.x, R.y, R.z), 2/5 E = (R.z, R.x, R.y),
	/// 3/6 E = (R.y, R.x, R.z), anything else zero. `sub_82A23A60` builds E as heading, pitch,
	/// roll in its Z-up world — S = Rz(−E0) · Rx(E1) · Ry(E2) — and `sub_824B2350` builds the
	/// record's `GUIScreenRotationOffset` O as Rx(−O.z) · Rz(−O.x) · Ry(−O.y). `sub_82233200`
	/// composes them offset · spin for axes 1..3 and spin · offset for 4..6.
	///
	/// Its world is X right, Y away from the camera, Z up; the model loader already maps that
	/// to Godot as (x, z, −y), so a turn about its Z is a turn about our Y, about its X our X,
	/// and about its Y our −Z. The spinner animates only `rotation.y`, the player's turn here,
	/// negated so that on the turntable axes (3/6, heading = −R.y) it is a plain yaw.
	/// </summary>
	private static Basis Stated(float yaw, Vector3 offset, int axis)
	{
		var r = new Vector3(0f, -yaw, 0f);
		Vector3 e = axis switch
		{
			1 or 4 => r,
			2 or 5 => new Vector3(r.Z, r.X, r.Y),
			3 or 6 => new Vector3(r.Y, r.X, r.Z),
			_ => Vector3.Zero,
		};

		Basis spin = new Basis(Vector3.Up, -e.X) * new Basis(Vector3.Right, e.Y) *
			new Basis(Vector3.Back, -e.Z);
		Basis tilt = new Basis(Vector3.Right, -offset.Z) * new Basis(Vector3.Up, -offset.X) *
			new Basis(Vector3.Back, offset.Y);
		return axis is >= 1 and <= 3 ? tilt * spin : spin * tilt;
	}

	private Placement _placed;

	private float _yaw;

	/// <summary>The uniform scale <see cref="Apply"/> settled on, anchor chain divided out.</summary>
	private float _drawnScale = 1f;

	/// <summary>The stated offset from the anchor, in canvas units (x, screen-vertical).</summary>
	private Vector3 _placedOffset;

	/// <summary>The mesh's own bounds centre, in model units.</summary>
	private Vector3 _meshCentre;

	/// <summary>
	/// THE ITEM'S CENTRE SITS AT THE ANCHOR, NOT ITS ORIGIN. Worn items are modelled in body
	/// space — the soldier helmet's geometry is at head height beside the origin, so hung by
	/// its origin it landed 400 canvas units below the ring (the paint probe: node at
	/// (230, 30), box at (−149..−19, −495..−364)). The bounds centre is carried through the
	/// current basis (rotation, spin and scale) and subtracted, so the geometry's middle is
	/// what sits at the anchor plus the stated offset, whichever way it is turned.
	/// </summary>
	private void Centre()
	{
		if (_mesh is null)
		{
			return;
		}

		_mesh.Position = _placed.Hung
			? _placedOffset
			: _placedOffset - _mesh.Basis * _meshCentre;
	}

	private void Apply(Placement placement)
	{
		if (_mesh is null)
		{
			return;
		}

		// A stated scale of 0 means the artist stated none, not "shrink it away".
		float stated = placement.Scale > 0f ? placement.Scale : 1f;
		float scale = stated * CanvasUnitsPerWorldUnit;

		// Fit to the artist's panel: the model is meant to sit inside it.
		Vector3 extent = _mesh.Mesh?.GetAabb().Size ?? Vector3.Zero;
		float longest = Mathf.Max(extent.X, Mathf.Max(extent.Y, extent.Z));
		float fitted = scale;
		if (PanelSize > 0f && longest > 0f && longest * scale > PanelSize)
		{
			fitted = PanelSize / longest;
		}

		// The guest places its preview at the anchor's accumulated POSITION and never inherits
		// the element's scale. Parenting to the anchor (which makes the spin free) means Godot
		// multiplies in every scale above, so divide it back out.
		float inherited = 1f;
		if (GetParent() is Node3D anchor)
		{
			Vector3 chain = Engine_UiTheme.CanvasTransform(anchor).Basis.Scale;
			float mean = (chain.X + chain.Y + chain.Z) / 3f;
			if (mean > 0.0001f)
			{
				inherited = mean;
			}
		}

		// THE OFFSET IS CANVAS-SPACE AND SWIZZLED. `sub_828C3C78` sums the anchor chain's
		// `position.x/y/z` into (x, z, y) lanes, adds `GUIScreenTranslationOffset` lane-wise
		// (0x828c3fb4), and `sub_822A89C0` maps lane 2 to world Z, which is UP — so the
		// offset's third component is screen-vertical and its second is depth. The Rusty
		// Longsword's (0, 0, -54.5) hangs it 54.5 canvas units below its anchor.
		// Canvas units, and the mesh sits in the anchor's space: the chain's scale comes out of
		// the offset exactly as it comes out of the model's.
		_placedOffset = new Vector3(placement.Translation.X, placement.Translation.Z, 0f) / inherited;
		_meshCentre = _mesh.Mesh?.GetAabb().GetCenter() ?? Vector3.Zero;
		_drawnScale = fitted / inherited;
		_placed = placement;
		Orient();

		// Carried on the report the caller prints once per model: what the guest stated, the
		// model's size, and what the fit had to do about it.
		Measurement = $"model {longest:F2}u, anchor scale {inherited:F3}, guest scale {placement.Scale:F3}" +
			$" (axis {placement.RotationAxis}), panel {PanelSize:F0}u, drawn at x{fitted:F1}" +
			(fitted < scale ? $" — FITTED, x{scale:F1} would have painted {longest * scale:F0}u" : string.Empty);
	}

	/// <summary>What the last <see cref="Apply"/> measured. Appended to the caller's report.</summary>
	public string Measurement { get; private set; } = string.Empty;
}
