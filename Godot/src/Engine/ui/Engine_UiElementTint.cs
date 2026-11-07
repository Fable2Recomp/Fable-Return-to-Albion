using Godot;

namespace Refii.Ui;

/// <summary>
/// AN ELEMENT'S STATED COLOUR ON ITS BUILT NODE — the one mechanism both titles tint by. The
/// guest's colour setter writes the element renderable's diffuse at +0x40 (Fable 2 `sub_82425E58`
/// on a text element and `sub_82425C58` on a quad's four vertices, `rgba / 255`; Fable 3
/// `sub_828190E8`, 0x82819110..0x8281919C). A TEXT element's diffuse is its glyphs' colour: the
/// mesher reads `text_colour`, so the caller re-meshes the field. A QUAD's is its material's
/// `tint_rgb`, the alpha its `tint_amount`, on the node and its direct children.
/// </summary>
public static class Engine_UiElementTint
{
	public static readonly StringName TextColour = "text_colour";

	/// <summary>The text colour the node was built with, kept the first time a statement replaces it.</summary>
	public static readonly StringName AuthoredTextColour = "text_colour_authored";

	private static readonly StringName TintRgb = "tint_rgb";

	private static readonly StringName TintAmount = "tint_amount";

	/// <summary>
	/// Writes <paramref name="colour"/> onto the node. True when the node is a text whose colour
	/// is now <paramref name="colour"/> — the caller re-meshes its shown string.
	/// </summary>
	/// <param name="own">
	/// Give each tinted quad its own material first: a cloned piece shares its source's (Godot's
	/// `Duplicate()` keeps resources), and a tint written there would reach every bearer.
	/// </param>
	public static bool Tint(Node node, Color colour, bool own = false)
	{
		if (node is MeshInstance3D text && text.HasMeta(TextColour))
		{
			if (!text.HasMeta(AuthoredTextColour))
			{
				text.SetMeta(AuthoredTextColour, text.GetMeta(TextColour));
			}

			text.SetMeta(TextColour, colour);
			return true;
		}

		Paint(node, colour, own);
		foreach (Node child in node.GetChildren())
		{
			Paint(child, colour, own);
		}

		return false;
	}

	/// <summary>
	/// Puts the file's colour back: a text's built colour (true — the caller re-meshes), a quad's
	/// authored tint from <paramref name="file"/>.
	/// </summary>
	public static bool Restore(Node node, UiNode? file, bool own = false)
	{
		if (node is MeshInstance3D text && text.HasMeta(AuthoredTextColour))
		{
			return Tint(text, (Color)text.GetMeta(AuthoredTextColour), own);
		}

		if (file?.Quad is { } quad)
		{
			Tint(node, new Color(quad.R, quad.G, quad.B, quad.TintAmount), own);
		}

		return false;
	}

	private static void Paint(Node target, Color colour, bool own)
	{
		if (target is not MeshInstance3D mesh)
		{
			return;
		}

		if (own && !mesh.HasMeta(OwnedMeta) && Engine_UiSceneBuilder.OwnQuadMaterial(mesh))
		{
			mesh.SetMeta(OwnedMeta, true);
		}

		if (mesh.MaterialOverride is ShaderMaterial material)
		{
			material.SetShaderParameter(TintRgb, new Vector3(colour.R, colour.G, colour.B));
			material.SetShaderParameter(TintAmount, colour.A);
		}
	}

	private static readonly StringName OwnedMeta = "tint_material_owned";
}
