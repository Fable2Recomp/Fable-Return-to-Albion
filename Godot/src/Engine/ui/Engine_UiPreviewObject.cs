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
	/// <param name="Rotation">`GUIScreenRotationOffset`, degrees.</param>
	/// <param name="Scale">`GUIScreenScaleMultiplier`; 0 means the artist stated none.</param>
	/// <param name="RotationAxis">`GUIScreenRotationAxis`, 1..6 — which euler swizzle the
	/// original applies. Carried, not yet applied.</param>
	public readonly record struct Placement(
		Vector3 Translation, Vector3 Rotation, float Scale, int RotationAxis);

	/// <summary>
	/// Shows the model the guest named, or clears the panel when it names none. True while an
	/// object is standing; a hash that resolves to no indexed model leaves the panel empty rather
	/// than substituting art.
	/// </summary>
	/// <summary>The preview mesh's node name — how the paint probe recognises it.</summary>
	public const string MeshName = "PreviewModel";

	public bool Show(uint modelHash, Placement placement)
	{
		if (modelHash == ModelHash)
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

		_mesh = new MeshInstance3D { Name = MeshName, Mesh = mesh };
		int textured = Present(_mesh, mesh);
		AddChild(_mesh);
		LastReport = $"{path} ({textured}/{mesh.GetSurfaceCount()} surface(s) textured)";
		Apply(placement);
		Visible = true;
		return true;
	}

	/// <summary>
	/// <summary>
	/// Shows a mesh the caller already holds — a LIVE ENTITY's composed body, not a model the guest
	/// named by hash. The co-op screen's mannequin is the case: `sub_82438818` spawns a real
	/// `CoopScreenTempHenchmanToDisplay` creature and the entity feed composes its dressed figure
	/// from component 17, so there is no hash to resolve and no second copy to load. Presented
	/// exactly as <see cref="Show"/> presents a model, keyed by <paramref name="key"/> (the entity
	/// address) so a frame that shows the same body again only re-applies the placement.
	///
	/// The guest scales this node by `(2.1 / body) * 350` (`sub_82438438`, 0x824387E0; body is
	/// 1.5 for a child, 2.1 adult, 2.4 with hair flag 10). What that means in this panel's units
	/// is not established, so the placement's scale is left for the panel fit in <c>Apply</c>.
	/// </summary>
	/// <param name="skeleton">
	/// The feed's Skeleton3D driving that body, or null. A skinned mesh drawn without its
	/// skeleton renders in BIND pose whatever the guest animates, so the copy here is bound to
	/// the same skeleton the feed poses — the pose then comes through as the feed writes it.
	/// </param>
	public bool ShowLive(Mesh? mesh, uint key, Placement placement, Skeleton3D? skeleton = null)
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
			Apply(placement);
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
		_mesh = new MeshInstance3D { Name = MeshName, Mesh = mesh };
		int textured = Present(_mesh, mesh, inPage: true);
		AddChild(_mesh);
		LastReport = $"live entity 0x{key:X8} ({textured}/{mesh.GetSurfaceCount()} surface(s) textured)";
		Apply(placement);
		Visible = true;
		return true;
	}

	/// <summary>The entity a live body was last shown for; 0 when the hash path owns the mesh.</summary>
	private uint _liveKey;

	/// Gives each surface an unlit material carrying the model's own albedo.
	///
	/// The model arrives with `Engine_MdlMaterial`'s lit world shader, which reads as a pink
	/// smear under the UI viewport's canvas camera. A host presentation choice, not a claim about
	/// the original: take the albedo and show it unlit. A surface stating no albedo gets neutral
	/// grey, so an untextured item reads as missing art rather than a rendering fault.
	/// </summary>
	/// <param name="inPage">
	/// Draw at the anchor's depth with the depth test on: the co-op mannequin sits in front of
	/// the parchment and behind the page's cut-out mask and the save-card fan, as the original
	/// composes it. The named-model path keeps the depth-free, top-priority draw.
	/// </param>
	private static int Present(MeshInstance3D instance, Mesh mesh, bool inPage = false)
	{
		int textured = 0;
		for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)
		{
			Texture2D? albedo = mesh.SurfaceGetMaterial(surface) is ShaderMaterial source
				? source.GetShaderParameter("albedo_tex").As<Texture2D>()
				: null;

			var flat = new StandardMaterial3D
			{
				// Unlit, as stated above: the UI viewport has no environment, and a lone key
				// light inside the clone's mirrored basis read every item as a flat grey ball.
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				// OVER THE PAGE. The guest drew its preview as a real entity in its own pass,
				// so it always sat on top of the book; here the anchor lies 350 canvas units
				// BEHIND the parchment (`model_proxy.model` z 1900, `frames_page` z 2249) and
				// the depth test buried the model under the page. Depth is off and the
				// priority high, with back faces culled so the mesh still reads as a solid.
				NoDepthTest = !inPage,
				RenderPriority = inPage ? 0 : 100,
				// NO CULLING: the anchor's basis is mirrored (see the light note above), so a
				// back-face cull removes the faces the player sees.
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				AlbedoTexture = albedo,
				AlbedoColor = albedo is null ? new Color(0.62f, 0.60f, 0.56f) : Colors.White,
			};

			if (albedo is not null)
			{
				++textured;
			}

			instance.SetSurfaceOverrideMaterial(surface, flat);
		}

		return textured;
	}

	/// <summary>The player's turn of the model about the panel's vertical, in degrees.</summary>
	public void Spin(float yawDegrees)
	{
		if (_mesh is null)
		{
			return;
		}

		// About the panel's vertical, as the guest's spinner writes the anchor's `rotation.y`
		// (`sub_829B5678` → `sub_824254C8`), over the record's own orientation.
		Basis turned = new Basis(Vector3.Up, Mathf.DegToRad(yawDegrees)) *
			Basis.FromEuler(_placedRotation * (Mathf.Pi / 180f));
		_mesh.Basis = turned.Scaled(_mesh.Scale);
		Centre();
	}

	private Vector3 _placedRotation;

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

		_mesh.Position = _placedOffset - _mesh.Basis * _meshCentre;
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
		_placedOffset = new Vector3(placement.Translation.X, placement.Translation.Z, 0f);
		_meshCentre = _mesh.Mesh?.GetAabb().GetCenter() ?? Vector3.Zero;
		_mesh.Scale = Vector3.One * (fitted / inherited);
		// Radians in the record — the bridge passes the floats raw. `GUIScreenRotationOffset` is
		// (0, 0, -1.482) for the tomb seal, i.e. -85 degrees.
		_placedRotation = new Vector3(
			Mathf.RadToDeg(placement.Rotation.X), Mathf.RadToDeg(placement.Rotation.Y),
			Mathf.RadToDeg(placement.Rotation.Z));
		_mesh.RotationDegrees = _placedRotation;
		Centre();

		// The guest lights the object with the presentation's own key light. The UI viewport owns
		// its world, so a light here reaches nothing else.
		if (GetNodeOrNull<DirectionalLight3D>("KeyLight") is null)
		{
			var key = new DirectionalLight3D { Name = "KeyLight", LightEnergy = 1.2f };
			key.RotationDegrees = new Vector3(-40f, 35f, 0f);
			AddChild(key);
		}

		// Carried on the report the caller prints once per model: what the guest stated, the
		// model's size, and what the fit had to do about it.
		Measurement = $"model {longest:F2}u, anchor scale {inherited:F3}, guest scale {placement.Scale:F3}" +
			$" (axis {placement.RotationAxis}), panel {PanelSize:F0}u, drawn at x{fitted:F1}" +
			(fitted < scale ? $" — FITTED, x{scale:F1} would have painted {longest * scale:F0}u" : string.Empty);
	}

	/// <summary>What the last <see cref="Apply"/> measured. Appended to the caller's report.</summary>
	public string Measurement { get; private set; } = string.Empty;
}
