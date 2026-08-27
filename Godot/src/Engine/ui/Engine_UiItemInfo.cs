using Godot;

namespace Refii.Ui;

/// <summary>
/// The item info panel — `scripts/gameface/iteminfo.lua`, ported. Four objects, three setters,
/// no logic: a screen manager pushes everything at it.
///
/// Takes RESOLVED display strings, not tags — `iteminfo.dis main.1/main.2 pc7..12` do
/// `setAttribute(_, 'textstring', GUI:GetText(id))`, the opposite of
/// <see cref="Engine_UiInfoWindow"/>, which writes a `TextID` tag. The icon is one atlas quad
/// selected by UV ORIGIN (`positionu`/`positionv`, `main.3 pc6..15`); nothing resizes it.
/// `PlaceholderMesh` is fetched by the script and used by nothing.
/// </summary>
public sealed class Engine_UiItemInfo
{
	private MeshInstance3D? _name;
	private MeshInstance3D? _description;
	private Node3D? _placeholder;
	private Node3D? _icon;

	/// <summary>The theme the text is re-meshed through.</summary>
	public Engine_UiTheme? Theme { get; set; }

	public bool IsBound => _name is not null || _description is not null;

	/// <summary>`AK.fetchObjects(g_ItemInfo, "NameText", "DescText", "PlaceholderMesh", "IconGraphic")`.</summary>
	public bool Bind(Node3D panel)
	{
		_name = Engine_UiTheme.FindByName(panel, "NameText") as MeshInstance3D;
		_description = Engine_UiTheme.FindByName(panel, "DescText") as MeshInstance3D;
		_placeholder = Engine_UiTheme.FindByName(panel, "PlaceholderMesh") as Node3D;
		_icon = Engine_UiTheme.FindByName(panel, "IconGraphic") as Node3D;
		return IsBound;
	}

	/// <summary>`SetName` — a RESOLVED string; the Lua runs it through `GUI:GetText` first.</summary>
	public void SetName(string display) => SetText(_name, display);

	/// <summary>`SetDescription` — a resolved string again.</summary>
	public void SetDescription(string display) => SetText(_description, display);

	/// <summary>
	/// `SetGraphic(u, v)` — moves the icon quad's UV origin to an atlas cell (`positionu`,
	/// `positionv`, `iteminfo.dis main.3 pc6..15`). The uniform is `uv1_offset`; `ui_quad.gdshader`
	/// declares no `uv_offset` and Godot stores an unknown parameter silently. The span is left
	/// alone — the Lua writes none, and `uv1_scale` keeps the authored rect's size.
	/// </summary>
	public void SetGraphic(float u, float v)
	{
		if (_icon is not MeshInstance3D mesh ||
			!GodotObject.IsInstanceValid(mesh) ||
			mesh.MaterialOverride is not ShaderMaterial material)
		{
			return;
		}

		var offset = new Vector2(u, v);

		// `Engine_UiSceneBuilder.BuildQuad` writes `uv1_offset = UMin + half`, so the half-texel
		// inset is folded into the value being replaced; re-derive it for the new origin or the
		// bilinear tap picks up the neighbouring cell. Signed off `uv1_scale`, which carries the
		// rect's direction.
		if (material.GetShaderParameter("diffuse_tex").As<Texture2D>() is { } texture)
		{
			Vector2 span = material.GetShaderParameter("uv1_scale").AsVector2();
			float du = 0.5f / Mathf.Max(texture.GetWidth(), 1);
			float dv = 0.5f / Mathf.Max(texture.GetHeight(), 1);

			offset.X += span.X < 0f ? -du : du;
			offset.Y += span.Y < 0f ? -dv : dv;
		}

		material.SetShaderParameter("uv1_offset", offset);
	}

	private void SetText(MeshInstance3D? node, string display)
	{
		// Each setter guards its own element — `if g_ItemInfo.NameText then`.
		if (node is null || !GodotObject.IsInstanceValid(node) || Theme is null)
		{
			return;
		}

		Theme.SetMeshText(node, display);
	}

	public string Describe() =>
		$"item info {(IsBound ? "bound" : "NOT bound")}" +
		(_name is null ? ", no NameText" : string.Empty) +
		(_description is null ? ", no DescText" : string.Empty) +
		(_icon is null ? ", no IconGraphic" : string.Empty) +
		(_placeholder is null ? string.Empty : ", placeholder present (never driven — as shipped)");
}
