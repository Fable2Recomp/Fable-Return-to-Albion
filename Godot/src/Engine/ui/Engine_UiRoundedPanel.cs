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

	private readonly QuadMesh _quad = new();

	public Engine_UiRoundedPanel()
	{
		Mesh = _quad;
		MaterialOverride = _material;
		Visible = false;
	}

	/// <summary>Fits the panel to a rectangle in the HUD's canvas space at depth <paramref name="z"/>.</summary>
	public void Fit(Rect2 rect, float z, float opacity = 1f)
	{
		Vector2 size = rect.Size + new Vector2(PaddingX * 2f, PaddingY * 2f);
		if (_quad.Size != size)
		{
			_quad.Size = size;
			_material.SetShaderParameter("size", size);
		}

		_material.SetShaderParameter("opacity", opacity);
		Position = new Vector3(rect.Position.X + rect.Size.X * 0.5f, rect.Position.Y + rect.Size.Y * 0.5f, z);
		Visible = true;
	}
}
