using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Level;

/// <summary>
/// The edge of a prop's cull radius, dithered instead of cut.
/// </summary>
/// <remarks>
/// <see cref="Engine_PropStreamer.ApplyVisibilityRange"/> culls small dressing by size tier
/// (15/20/30/40 m times <c>prop_cull_distance_scale</c>). A Godot visibility range is one test
/// per NODE, measured to the node's box centre, and a batch is one node per 32 m cell: a whole
/// cell of undergrowth appeared the frame its centre came into range. The radius stays as set —
/// that is the cost decision — and the node still culls there; what changes is the edge. Each
/// COPY dithers over the last <see cref="BandShare"/> of the radius, measured from the main
/// camera to its own origin in the body shader (<c>xr_tree_fade_out</c>, the tree band's
/// uniforms, which the shadow caster shares), so a plant fades in on its own as it crosses into
/// range and the cell's cull only ever removes copies the dither has already taken to nothing.
///
/// A copy the streamer delivers late stands inside its radius already, so distance cannot fade
/// it: it dithers in over <see cref="ArrivalSeconds"/> from the frame its mesh stood
/// (<c>xr_fade_alpha</c>), the cast-only twin or proxy beside it alike.
///
/// Per view by construction: the shader measures from each viewport's own camera
/// (<c>MAIN_CAM_INV_VIEW_MATRIX</c>, the main camera in the shadow passes too) and Godot culls
/// each viewport with its own, so on split screen each eye sees its own edge.
///
/// Only the surfaces that may discard take part: the body under <c>FABLE_SOLID</c> compiles
/// without one so Godot's shared depth-only shadow material still covers opaque props, and a
/// classic material has the band only in its tree family.
/// </remarks>
public static class Engine_PropFade
{
	/// <summary>
	/// The share of a copy's cull radius over which it dithers: 3 m at the 15 m tier, 8 m at the
	/// 40 m one. A share rather than metres so the edge keeps its proportion under
	/// <c>prop_cull_distance_scale</c>; a fifth is short enough that a copy standing inside the
	/// radius is whole, and long enough to take a walking pace a second or more to cross.
	/// </summary>
	public const float BandShare = 0.2f;

	/// <summary>
	/// How long a copy that arrives inside its radius takes to dither in: well past a few frames,
	/// so it never reads as a pop, and well short of a stride, so it never reads as an animation.
	/// </summary>
	public const float ArrivalSeconds = 0.5f;

	/// <summary>The band a radius dithers over, in metres.</summary>
	public static float Band(float radius) => radius > 0f ? radius * BandShare : 0f;

	/// <summary>
	/// The fraction of a copy drawn at <paramref name="distance"/> from the eye: whole inside
	/// the band, nothing at the radius, linear between. A radius of zero is no cull and whole.
	/// </summary>
	public static float DistanceFactor(float distance, float radius)
	{
		float band = Band(radius);
		if (band <= 0f)
		{
			return 1f;
		}

		return Math.Clamp((radius - distance) / band, 0f, 1f);
	}

	/// <summary>
	/// The body shader's out band for a radius, as <c>xr_tree_fade_out</c> reads it: (end,
	/// 1/length, power) with the end at the radius and a linear curve. All zero for no cull.
	/// </summary>
	public static (float End, float InverseLength, float Power) OutBand(float radius)
	{
		float band = Band(radius);
		return band > 0f ? (radius, 1f / band, 1f) : (0f, 0f, 0f);
	}

	/// <summary>The fraction of a copy drawn <paramref name="elapsedSeconds"/> after it stood.</summary>
	public static float ArrivalFactor(double elapsedSeconds) =>
		(float)Math.Clamp(elapsedSeconds / ArrivalSeconds, 0.0, 1.0);

	private static readonly StringName FadeIn = "xr_tree_fade_in";
	private static readonly StringName FadeOut = "xr_tree_fade_out";
	private static readonly StringName Centre = "xr_tree_centre";
	private static readonly StringName Alpha = "xr_fade_alpha";

	/// <summary>The fade uniforms a node was given, by this class or a tree's stated band.</summary>
	private sealed class Held
	{
		public bool Banded;
		public Vector4 FadeIn, FadeOut;
		public Vector3 Centre;
		public float? Alpha;
	}

	/// <summary>
	/// What each node was given, for <see cref="Copy"/>.
	/// </summary>
	/// <remarks>
	/// Held here rather than read back: `GetInstanceShaderParameter` is a value-returning server
	/// call, and with Godot's separate render thread it waits until that thread has finished the
	/// frame it is drawing. Every cast-only twin and proxy a region builds copies its body's band,
	/// so a page-in paid that wait once per copy's first read in the frame.
	/// </remarks>
	private static readonly ConditionalWeakTable<GeometryInstance3D, Held> States = new();

	/// <summary>
	/// Gives <paramref name="node"/> a fade band: in, out and the point it is measured from. The
	/// only way a band is written, so <see cref="Copy"/> can hand it on.
	/// </summary>
	public static void StateBand(GeometryInstance3D node, Vector4 fadeIn, Vector4 fadeOut, Vector3 centre)
	{
		node.SetInstanceShaderParameter(FadeOut, fadeOut);
		node.SetInstanceShaderParameter(FadeIn, fadeIn);
		node.SetInstanceShaderParameter(Centre, centre);
		Held held = States.GetOrCreateValue(node);
		held.Banded = true;
		held.FadeIn = fadeIn;
		held.FadeOut = fadeOut;
		held.Centre = centre;
		Refii.AndroidRuntime.AndroidShadowTwinDither.Band(node, fadeOut.X);
	}

	private static void StateAlpha(GeometryInstance3D node, float alpha)
	{
		node.SetInstanceShaderParameter(Alpha, alpha);
		States.GetOrCreateValue(node).Alpha = alpha;
		Refii.AndroidRuntime.AndroidShadowTwinDither.Alpha(node, alpha);
	}

	/// <summary>
	/// States the radius a node's copies dither to, or clears it with zero. A batch measures each
	/// copy at its origin, the point its cell's spread was taken about, so the cull at radius plus
	/// spread never removes a copy the dither still shows; a single measures at its box centre,
	/// the point Godot culls it by.
	/// </summary>
	public static void SetRadius(GeometryInstance3D node, Mesh mesh, float radius)
	{
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			return;
		}

		(float end, float inverseLength, float power) = OutBand(radius);
		StateBand(node, Vector4.Zero, new Vector4(end, inverseLength, power, 0f),
			node is MultiMeshInstance3D ? Vector3.Zero : mesh.GetAabb().GetCenter());
	}

	/// <summary>Gives a cast-only twin or proxy its body's band and arrival, so the shadow follows the copy.</summary>
	public static void Copy(GeometryInstance3D from, GeometryInstance3D to)
	{
		if (!States.TryGetValue(from, out Held? held) || !held.Banded)
		{
			return;
		}

		StateBand(to, held.FadeIn, held.FadeOut, held.Centre);
		if (held.Alpha is { } alpha)
		{
			StateAlpha(to, alpha);
		}
	}

	private readonly record struct Arrival(GeometryInstance3D Node, GeometryInstance3D? Caster, long Since);

	private static readonly List<Arrival> Arrivals = new();

	private static ulong _tickedFrame = ulong.MaxValue;

	/// <summary>Whether any copy is still dithering in from its arrival.</summary>
	public static bool Fading => Arrivals.Count != 0;

	/// <summary>
	/// Starts a node that just stood dithering in, with the twin or proxy that casts for it. Called
	/// after the shadow hand-off, so the caster exists to be found. Under the load hold no one
	/// sees it arrive: it stands whole, the uniform's default.
	/// </summary>
	public static void BeginArrival(GeometryInstance3D node)
	{
		if (Refii.Models.Engine_ClassicMaterial.Active || Refii.Game.GameWorld.WorldBuildHeld)
		{
			return;
		}

		GeometryInstance3D? caster = node switch
		{
			MultiMeshInstance3D batch => batch.GetNodeOrNull<GeometryInstance3D>(Engine_PropShadow.NodeName),
			MeshInstance3D single => Refii.Entities.Engine_ShadowProxy.ProxyOf(single),
			_ => null,
		};

		StateAlpha(node, 0f);
		if (caster is not null)
		{
			StateAlpha(caster, 0f);
		}
		Arrivals.Add(new Arrival(node, caster, System.Diagnostics.Stopwatch.GetTimestamp()));
	}

	/// <summary>Advances every arrival; once per frame however many streamers call it.</summary>
	public static void Tick()
	{
		ulong frame = Engine.GetProcessFrames();
		if (frame == _tickedFrame || Arrivals.Count == 0)
		{
			return;
		}

		_tickedFrame = frame;
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		for (int i = Arrivals.Count - 1; i >= 0; i--)
		{
			Arrival arrival = Arrivals[i];
			if (!GodotObject.IsInstanceValid(arrival.Node))
			{
				Arrivals.RemoveAt(i);
				continue;
			}

			float alpha = ArrivalFactor((now - arrival.Since) / (double)System.Diagnostics.Stopwatch.Frequency);
			StateAlpha(arrival.Node, alpha);
			if (arrival.Caster is { } caster && GodotObject.IsInstanceValid(caster))
			{
				StateAlpha(caster, alpha);
			}

			if (alpha >= 1f)
			{
				Arrivals.RemoveAt(i);
			}
		}
	}
}
