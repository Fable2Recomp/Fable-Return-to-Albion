using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Refii.Ui;

/// <summary>
/// `canvas_lean`: the 2D pass's shortcuts that leave the picture as it was. Off draws as before;
/// the debug channel's `render` op flips it live (`canvasLean`), so the two are A/B'd in one process.
/// </summary>
/// <remarks>
/// <para>WHERE THE 2D PASS GOES ON A TILER. Godot 4.7 renders each CanvasLayer that has anything to
/// draw as its own render pass over the whole target (RendererCanvasRenderRD::_render_batch_items,
/// one draw_list_begin per canvas, load and store). On an Adreno each pass loads the full-resolution
/// target into tile memory and writes it back: at 2280x1080 RGBA8 about 20 MB, more than the HUD's
/// and the touch controls' own pixels cost. A screen-texture read adds a full copy on top.</para>
/// <para>WHAT IT DOES. The touch controls ride on the UI layer's canvas while no layer between the
/// two has anything showing (AndroidCanvasLean), drawn last in it, as they were: one pass instead of
/// two. The screen fade is not drawn while its alpha is below half an 8-bit step, where a blend over
/// the screen rounds back to every pixel it started from.</para>
/// <para>Main-thread owned. The touch layer's state is published here for the debug channel.</para>
/// </remarks>
internal static class Engine_CanvasLean
{
	public const string Key = "canvas_lean";

	/// <summary>Read each frame; the debug op's reply states it.</summary>
	internal static bool Enabled;

	/// <summary>The layer the touch controls are drawn on while they share it, else null.</summary>
	internal static string? SharedInto;

	/// <summary>The touch controls' own layer while they share another's, else null.</summary>
	internal static string? SharedFrom;

	/// <summary>Why they are not shared this frame: the blocking layer, or the unmet condition.</summary>
	internal static string? Blocker;

	/// <summary>
	/// The alpha under which a blend over the screen changes no 8-bit value: dst + a(src - dst)
	/// moves by at most a, and under half a step that rounds back to dst.
	/// </summary>
	internal const float InvisibleAlpha = 0.5f / 255f;

	/// <summary>Whether a full-screen blend at <paramref name="alpha"/> leaves every pixel as it was.</summary>
	internal static bool Invisible(float alpha) => alpha < InvisibleAlpha;

	/// <summary>Validates the optional lever before the debug endpoint mutates rendering state.</summary>
	/// <returns>The requested flag or null without changing process state.</returns>
	internal static bool? ReadRequest(JsonElement request)
	{
		if (!request.TryGetProperty("canvasLean", out JsonElement value)) return null;
		if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			throw new ArgumentException("canvasLean must be a boolean.");
		return value.GetBoolean();
	}

	/// <summary>One other layer of the same viewport, as the sharing test needs it.</summary>
	/// <param name="Name">For the report.</param>
	/// <param name="Layer">Its CanvasLayer.layer.</param>
	/// <param name="Shows">Whether it is visible with a visible item in it.</param>
	internal readonly record struct OtherLayer(string Name, int Layer, bool Shows);

	/// <summary>
	/// Whether the layer at <paramref name="upper"/> can be drawn last in the canvas of the layer at
	/// <paramref name="lower"/> without changing the picture: nothing between them shows. A layer
	/// on either index is a blocker too, since Godot orders equal indices by node, not by intent.
	/// </summary>
	/// <returns>Null when it can, else the name of the first layer in the way.</returns>
	internal static string? FirstBetween(int lower, int upper, IEnumerable<OtherLayer> others)
	{
		if (lower >= upper) return "order";
		foreach (OtherLayer other in others)
		{
			if (other.Shows && other.Layer >= lower && other.Layer <= upper) return other.Name;
		}

		return null;
	}

	/// <summary>
	/// The colour traffic of one canvas render pass on a tiler: the target loaded into tile memory
	/// and stored back, at <paramref name="bytesPerPixel"/> (4 for RGBA8, 8 for RGBA16F).
	/// </summary>
	internal static long PassBytes(int width, int height, int bytesPerPixel) =>
		2L * Math.Max(0, width) * Math.Max(0, height) * bytesPerPixel;
}
