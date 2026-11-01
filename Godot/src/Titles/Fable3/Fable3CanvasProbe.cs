namespace Refii.Titles.Fable3;

using System.Text;
using Godot;

/// <summary>
/// One-shot ground truth for a Fable 3 3D canvas (a SubViewportContainer hosting an own-world
/// SubViewport and an orthographic camera): what the container, the viewport and the camera are,
/// where the subject's meshes fall in the camera, and — read back from the viewport's texture —
/// whether anything was rendered at all.
/// </summary>
/// <remarks>
/// EXISTS BECAUSE THE LOG LIED BY OMISSION. r7 and r8 logged `view drew 3 … view shown` for the
/// Options list while no row reached the screen, and the world sprites logged `1 on screen` with
/// nothing visible: "drew" counts rows made visible, not pixels. The readback separates the three
/// failures — the viewport never rendered (no pixel), it rendered but the meshes are outside the
/// camera (pixels 0, corners out of frustum), or it rendered and the composite lost it (pixels
/// non-zero).
/// </remarks>
public static class Fable3CanvasProbe
{
	/// <summary>Reports a few frames from now, so the viewport has rendered what the caller just built.</summary>
	public static async void Report(string tag, SubViewportContainer container, SubViewport viewport, Camera3D camera, Node3D? subject)
	{
		for (int i = 0; i < 3; ++i)
		{
			if (!GodotObject.IsInstanceValid(container))
			{
				return;
			}

			await container.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		}

		if (!GodotObject.IsInstanceValid(container) || !GodotObject.IsInstanceValid(viewport) || !GodotObject.IsInstanceValid(camera))
		{
			return;
		}

		var line = new StringBuilder();
		line.Append($"[{tag}] canvas probe: container size {container.Size} at {container.GlobalPosition} ")
			.Append($"visible {container.IsVisibleInTree()} stretch {container.Stretch} in '{container.GetViewport()?.Name}'; ")
			.Append($"viewport size {viewport.Size} mode {viewport.RenderTargetUpdateMode} transparent {viewport.TransparentBg} ")
			.Append($"own-world {viewport.OwnWorld3D} disable3d {viewport.Disable3D} active camera '{viewport.GetCamera3D()?.Name}' ")
			.Append($"(ours {ReferenceEquals(viewport.GetCamera3D(), camera)}); ")
			.Append($"camera current {camera.Current} {camera.Projection} size {camera.Size:0.#} keep {camera.KeepAspect} ")
			.Append($"at {camera.GlobalPosition} near {camera.Near} far {camera.Far} cull {camera.CullMask:X}; ");

		if (subject is not null && GodotObject.IsInstanceValid(subject))
		{
			Describe(line, subject, camera, viewport);
		}

		line.Append(Readback(viewport));
		GD.Print(line.ToString());
	}

	private static void Describe(StringBuilder line, Node3D subject, Camera3D camera, SubViewport viewport)
	{
		int meshes = 0;
		int inFrustum = 0;
		Rect2? pixels = null;
		Aabb? world = null;
		float? alpha = null;
		uint layers = 0;
		foreach (MeshInstance3D mesh in Meshes(subject))
		{
			if (!mesh.IsVisibleInTree() || mesh.Mesh is null)
			{
				continue;
			}

			++meshes;
			layers |= mesh.Layers;
			Aabb box = mesh.GlobalTransform * mesh.Mesh.GetAabb();
			world = world is { } w ? w.Merge(box) : box;
			bool any = false;
			for (int c = 0; c < 8; ++c)
			{
				Vector3 corner = box.GetEndpoint(c);
				any |= camera.IsPositionInFrustum(corner);
				Vector2 at = camera.UnprojectPosition(corner);
				pixels = pixels is { } p ? p.Expand(at) : new Rect2(at, Vector2.Zero);
			}

			if (any)
			{
				++inFrustum;
			}

			if (alpha is null && mesh.MaterialOverride is ShaderMaterial material && material.GetShaderParameter("colour").VariantType == Variant.Type.Color)
			{
				alpha = material.GetShaderParameter("colour").AsColor().A;
			}
		}

		line.Append($"subject '{subject.Name}' at {subject.GlobalPosition} scale {subject.Scale}: {meshes} visible mesh(es), ")
			.Append($"{inFrustum} touching the frustum, layers {layers:X}, world AABB {world?.ToString() ?? "-"}, ")
			.Append($"in viewport pixels {pixels?.ToString() ?? "-"} of {viewport.Size}, first label alpha {alpha?.ToString("0.###") ?? "-"}; ");
	}

	private static System.Collections.Generic.IEnumerable<MeshInstance3D> Meshes(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is MeshInstance3D mesh)
			{
				yield return mesh;
			}

			foreach (MeshInstance3D inner in Meshes(child))
			{
				yield return inner;
			}
		}
	}

	/// <summary>The viewport's last rendered image: how many pixels carry any alpha, and where.</summary>
	private static string Readback(SubViewport viewport)
	{
		Image? image = viewport.GetTexture()?.GetImage();
		if (image is null || image.IsEmpty())
		{
			return "readback: no image";
		}

		image.Convert(Image.Format.Rgba8);
		byte[] data = image.GetData();
		int width = image.GetWidth();
		int count = 0;
		int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
		for (int i = 3; i < data.Length; i += 4)
		{
			if (data[i] <= 5)
			{
				continue;
			}

			++count;
			int pixel = i / 4;
			int x = pixel % width;
			int y = pixel / width;
			minX = System.Math.Min(minX, x);
			minY = System.Math.Min(minY, y);
			maxX = System.Math.Max(maxX, x);
			maxY = System.Math.Max(maxY, y);
		}

		return count == 0
			? $"readback {image.GetWidth()}x{image.GetHeight()}: NOTHING RENDERED (every pixel transparent)"
			: $"readback {image.GetWidth()}x{image.GetHeight()}: {count} pixel(s) drawn in ({minX},{minY})..({maxX},{maxY})";
	}
}
