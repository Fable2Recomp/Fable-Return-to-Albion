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
	public bool Show(uint modelHash, Placement placement)
	{
		if (modelHash == ModelHash)
		{
			Apply(placement);
			return _mesh is not null;
		}

		ModelHash = modelHash;
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

		_mesh = new MeshInstance3D { Name = "PreviewModel", Mesh = mesh };
		int textured = Present(_mesh, mesh);
		AddChild(_mesh);
		LastReport = $"{path} ({textured}/{mesh.GetSurfaceCount()} surface(s) textured)";
		Apply(placement);
		Visible = true;
		return true;
	}

	/// <summary>
	/// Gives each surface an unlit material carrying the model's own albedo.
	///
	/// The model arrives with `Engine_MdlMaterial`'s lit world shader, which reads as a pink
	/// smear under the UI viewport's canvas camera. A host presentation choice, not a claim about
	/// the original: take the albedo and show it unlit. A surface stating no albedo gets neutral
	/// grey, so an untextured item reads as missing art rather than a rendering fault.
	/// </summary>
	private static int Present(MeshInstance3D instance, Mesh mesh)
	{
		int textured = 0;
		for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)
		{
			Texture2D? albedo = mesh.SurfaceGetMaterial(surface) is ShaderMaterial source
				? source.GetShaderParameter("albedo_tex").As<Texture2D>()
				: null;

			var flat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
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

	/// <summary>The spin, taken from the anchor element the guest writes it on.</summary>
	public void Spin(Vector3 rotationDegrees)
	{
		if (_mesh is not null)
		{
			_mesh.RotationDegrees = _placedRotation + rotationDegrees;
		}
	}

	private Vector3 _placedRotation;

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

		_mesh.Position = placement.Translation;
		_mesh.Scale = Vector3.One * (fitted / inherited);
		// Radians in the record — the bridge passes the floats raw. `GUIScreenRotationOffset` is
		// (0, 0, -1.482) for the tomb seal, i.e. -85 degrees.
		_placedRotation = new Vector3(
			Mathf.RadToDeg(placement.Rotation.X), Mathf.RadToDeg(placement.Rotation.Y),
			Mathf.RadToDeg(placement.Rotation.Z));
		_mesh.RotationDegrees = _placedRotation;

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
