using Godot;

namespace Refii.Ui;

/// <summary>
/// A rounded translucent panel fitted behind a run of HUD text — the remaster's replacement for
/// the shipped black slabs (a black-tinted `hud` image at 50%, sized by the TopBoxX/TopBoxY
/// meters). Sized from the text it backs every frame, so it follows the guest's strings.
/// </summary>
public partial class Engine_UiRoundedPanel : MeshInstance3D
{
	private static readonly Shader PanelShader = GD.Load<Shader>("res://src/Engine/ui/ui_panel.gdshader");

	public const float PaddingX = 18f;
	public const float PaddingY = 9f;

	private readonly ShaderMaterial _material = new() { Shader = PanelShader };

	// Fitted every frame: one StringName per uniform, never a literal (a literal is a fresh
	// StringName per call, and a weak handle the next GC frees inside its pause).
	private static readonly StringName FillName = "fill";
	private static readonly StringName BorderName = "border";
	private static readonly StringName BorderWidthName = "border_width";
	private static readonly StringName RadiusName = "radius";
	private static readonly StringName SizeName = "size";
	private static readonly StringName OpacityName = "opacity";

	private float _opacity = float.NaN;

	private readonly QuadMesh _quad = new();

	public Engine_UiRoundedPanel()
	{
		Mesh = _quad;
		MaterialOverride = _material;
		Visible = false;
	}

	/// <summary>
	/// A look other than the HUD notice's default: the fill (its alpha is the panel's own
	/// translucency) and an optional border. For a panel that must read over ANY scene — the
	/// achievement toast stands over the world itself, not over a dimmed menu.
	/// </summary>
	public void Style(Color fill, Color border, float borderWidth)
	{
		_material.SetShaderParameter(FillName, fill);
		_material.SetShaderParameter(BorderName, border);
		_material.SetShaderParameter(BorderWidthName, borderWidth);
	}

	/// <summary>The corner radius the HUD's notices wear, in HUD canvas units.</summary>
	public const float HudCornerRadius = 12f;

	/// <summary>
	/// The corner radius in THIS panel's local units. The shader's default is the HUD's, which is
	/// only right for a panel that lives in HUD canvas units; one mounted in another space (the
	/// achievement toast works in display pixels) states its own so the corners match on screen.
	/// </summary>
	public void Corner(float radius) => _material.SetShaderParameter(RadiusName, radius);

	/// <summary>Fits the panel to a rectangle in the HUD's canvas space at depth <paramref name="z"/>.</summary>
	public void Fit(Rect2 rect, float z, float opacity = 1f)
	{
		Vector2 size = rect.Size + new Vector2(PaddingX * 2f, PaddingY * 2f);
		if (_quad.Size != size)
		{
			_quad.Size = size;
			_material.SetShaderParameter(SizeName, size);
		}

		if (_opacity != opacity)
		{
			_opacity = opacity;
			_material.SetShaderParameter(OpacityName, opacity);
		}

		Position = new Vector3(rect.Position.X + rect.Size.X * 0.5f, rect.Position.Y + rect.Size.Y * 0.5f, z);
		Visible = true;
	}
}
