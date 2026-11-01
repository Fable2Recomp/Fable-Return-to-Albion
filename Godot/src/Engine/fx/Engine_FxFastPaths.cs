using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;

namespace Refii.Fx;

/// <summary>
/// `fx_cpu_fastpaths`: the effect system's main-thread shortcuts that leave the picture as it was.
/// Off draws through the original loops; the debug channel's `render` op flips it live
/// (`fxFastPaths`), so the two are A/B'd in one process.
/// </summary>
/// <remarks>
/// Main-thread owned, like the emitters it serves. Each shortcut states what it keeps: the emit
/// loop's early rejection, of a particle or of a whole emitter, and the cheaper sort give the same
/// vertex buffers bit for bit; skipping the sort of an order-independent batch gives the same quads
/// in emission order, the same sum; the bounds are the exact box of the same points; the
/// step-alpha and mesh-particle writes leave every drawn value as before.
/// </remarks>
internal static class Engine_FxFastPaths
{
	public const string Key = "fx_cpu_fastpaths";

	/// <summary>Read once per emit and per step; the reply of the debug op states it.</summary>
	internal static bool Enabled;

	/// <summary>Applies the setting at launch; the debug channel may change it afterwards.</summary>
	internal static void Configure(IReadOnlyDictionary<string, string> config) =>
		Enabled = Refii.Core.Engine_RefiiConfig.Bool(config, Key, false);

	/// <summary>Validates the optional lever before the debug endpoint mutates rendering state.</summary>
	/// <returns>The requested flag or null without changing process state.</returns>
	internal static bool? ReadRequest(JsonElement request)
	{
		if (!request.TryGetProperty("fxFastPaths", out JsonElement value)) return null;
		if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			throw new ArgumentException("fxFastPaths must be a boolean.");
		return value.GetBoolean();
	}

	/// <summary>
	/// Whether a batch's blend gives the same sum in any order, so back-to-front buys nothing.
	/// `add`, `addone` (and the additive layer it goes to) and `disp` are Godot's blend_add, `sub`
	/// is blend_sub: dst ± src·a with src independent of dst, and none of them writes depth or reads
	/// the screen. A saturating target clamps the same either way for non-negative terms. `mix`,
	/// `mixalpha` and `darken` are order-dependent; `revsub` draws a darken pass first.
	/// </summary>
	/// <remarks>Rounding is per blend, so the sum can differ from back-to-front in its last bit.</remarks>
	internal static bool OrderIndependent(string mode) => mode is "add" or "addone" or "disp" or "sub";

	/// <summary>
	/// The highest alpha a particle of these visuals can reach before its fade and intensity: the
	/// largest keyframe alpha, widened by the rounding of the lerp between two of them. Infinity
	/// (no early rejection) when any alpha is not finite.
	/// </summary>
	internal static float AlphaCeiling(float[] alphas)
	{
		float max = 0f, magnitude = 0f;
		foreach (float a in alphas)
		{
			if (!float.IsFinite(a) || MathF.Abs(a) > 1e30f) return float.PositiveInfinity;
			if (a > max) max = a;
			magnitude = MathF.Max(magnitude, MathF.Abs(a));
		}

		// `x + (y - x)·w` for w in [0, 1] stays within a few ulps of [min, max]; 1e-5 of the
		// largest magnitude is two orders past that.
		return max + (magnitude * 1e-5f) + 1e-30f;
	}

	/// <summary>
	/// Whether a particle whose keyframe alpha is at most <paramref name="ceiling"/> is rejected
	/// whatever that alpha is, from its fade × intensity term alone: the emit loop's `a × term
	/// ≤ 0.003`. Exact, since a correctly rounded product is monotonic in each factor; a negative
	/// or NaN term is left to the full test.
	/// </summary>
	internal static bool RejectedByFade(float ceiling, float fadeTerm) =>
		fadeTerm >= 0f && ceiling * fadeTerm <= 0.003f;

	/// <summary>
	/// Whether every point of a box is past an emitter's fade-out end <paramref name="fadeOutEnd"/>
	/// as the emit measures it, `(eye - p).Length()` in floats: the nearest point of the box in
	/// doubles, beyond the end by a millimetre and the rounding of coordinates this large. Past it,
	/// the fade-out factor is zero in both of its forms.
	/// </summary>
	internal static bool BeyondFadeOut(Vector3 lo, Vector3 hi, Vector3 eye, float fadeOutEnd)
	{
		if (!float.IsFinite(fadeOutEnd)) return false;
		double dx = Math.Max(Math.Max((double)lo.X - eye.X, (double)eye.X - hi.X), 0.0);
		double dy = Math.Max(Math.Max((double)lo.Y - eye.Y, (double)eye.Y - hi.Y), 0.0);
		double dz = Math.Max(Math.Max((double)lo.Z - eye.Z, (double)eye.Z - hi.Z), 0.0);
		double near = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
		double scale = Math.Max(Math.Max(MaxAbs(lo), MaxAbs(hi)), MaxAbs(eye));
		return near > fadeOutEnd + 1e-3 + (1e-5 * (Math.Abs((double)fadeOutEnd) + scale));
	}

	private static double MaxAbs(Vector3 v) => Math.Max(Math.Max(Math.Abs((double)v.X), Math.Abs((double)v.Y)), Math.Abs((double)v.Z));

	/// <summary>
	/// The original back-to-front order of a batch's quads, cheaper: the quad centres keyed once,
	/// sorted as packed integers when no two are tied (any sort then gives the one order), else by
	/// the original comparison sort. Six-vertex blocks are copied once, into <paramref name="sorted"/>.
	/// </summary>
	/// <param name="verts">The batch, unmodified.</param>
	/// <param name="eye">The camera in the batch's space.</param>
	/// <param name="sorted">Scratch the reordered batch is written to.</param>
	/// <param name="order">Scratch for a tied batch's comparison sort.</param>
	/// <param name="keys">Scratch for the packed keys, grown as needed.</param>
	/// <returns><paramref name="verts"/> when there is nothing to reorder, else <paramref name="sorted"/>.</returns>
	internal static List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> SortQuadsBackToFront(
		List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> verts, Vector3 eye,
		List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> sorted,
		List<(float D, int Q)> order, ref ulong[] keys)
	{
		int quads = verts.Count / 6;
		if (quads < 2) return verts;
		ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> source = CollectionsMarshal.AsSpan(verts);
		if (keys.Length < quads) keys = new ulong[Math.Max(quads, keys.Length * 2)];
		Span<ulong> packed = keys.AsSpan(0, quads);
		bool packable = true;
		for (int q = 0; q < quads; q++)
		{
			// The same arithmetic as the original key, term for term.
			Vector3 c = 0.5f * (source[q * 6].Pos + source[(q * 6) + 1].Pos);
			float d = (c - eye).LengthSquared();

			// A non-negative float's bits order as its value does; NaN sorts by CompareTo's rule.
			if (!(d >= 0f)) packable = false;
			packed[q] = ((ulong)~BitConverter.SingleToUInt32Bits(d) << 32) | (uint)q;
		}

		if (packable)
		{
			packed.Sort();
			for (int i = 1; i < quads; i++)
			{
				if ((packed[i] >> 32) == (packed[i - 1] >> 32))
				{
					packable = false;
					break;
				}
			}
		}

		CollectionsMarshal.SetCount(sorted, quads * 6);
		Span<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> target = CollectionsMarshal.AsSpan(sorted);
		if (packable)
		{
			for (int i = 0; i < quads; i++)
			{
				int q = (int)(uint)packed[i];
				source.Slice(q * 6, 6).CopyTo(target.Slice(i * 6, 6));
			}

			return sorted;
		}

		// A tie: equal keys are left in whatever order the original's unstable sort puts them, so
		// that sort runs on the same keys in the same sequence.
		order.Clear();
		for (int q = 0; q < quads; q++)
		{
			Vector3 c = 0.5f * (source[q * 6].Pos + source[(q * 6) + 1].Pos);
			order.Add(((c - eye).LengthSquared(), q));
		}

		order.Sort(FarthestFirst);
		ReadOnlySpan<(float D, int Q)> ranked = CollectionsMarshal.AsSpan(order);
		for (int i = 0; i < quads; i++)
		{
			source.Slice(ranked[i].Q * 6, 6).CopyTo(target.Slice(i * 6, 6));
		}

		return sorted;
	}

	/// <summary>The original comparison: the farthest quad first.</summary>
	internal static readonly Comparison<(float D, int Q)> FarthestFirst = (l, r) => r.D.CompareTo(l.D);

	/// <summary>
	/// A batch's vertices into a surface's running box, and how far a corner reaches and which
	/// step-alpha uniforms its moving vertices read. The box is the exact minimum and maximum of
	/// the points; the reach is the largest corner-offset length, the square root taken once of
	/// the largest square, which is the same float as the largest root.
	/// </summary>
	/// <param name="vertices">The batch, unmodified.</param>
	/// <param name="lo">The box's minimum so far; seeded by the first point when <paramref name="has"/> is false.</param>
	/// <param name="hi">The box's maximum so far.</param>
	/// <param name="has">Whether a point has been taken.</param>
	/// <param name="maxOffset">The largest corner-offset length so far.</param>
	/// <param name="motionClasses">Bit 0: a moving full-rate vertex (CUSTOM0.w 0); bit 1: a moving half-rate one.</param>
	internal static void Bounds(ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> vertices,
		ref Vector3 lo, ref Vector3 hi, ref bool has, ref float maxOffset, ref int motionClasses)
	{
		if (vertices.Length == 0) return;
		if (!has)
		{
			lo = hi = vertices[0].Pos;
			has = true;
		}

		float lx = lo.X, ly = lo.Y, lz = lo.Z, hx = hi.X, hy = hi.Y, hz = hi.Z;
		float reachSq = 0f;
		int classes = motionClasses;
		for (int i = 0; i < vertices.Length; i++)
		{
			ref readonly var v = ref vertices[i];
			Vector3 p = v.Pos;

			// Aabb.Expand's comparisons, so a NaN coordinate is passed over as it was.
			if (p.X < lx) lx = p.X;
			if (p.X > hx) hx = p.X;
			if (p.Y < ly) ly = p.Y;
			if (p.Y > hy) hy = p.Y;
			if (p.Z < lz) lz = p.Z;
			if (p.Z > hz) hz = p.Z;
			float sq = v.Off.LengthSquared();
			if (sq > reachSq) reachSq = sq;
			Vector4 m = v.Motion;
			if (m.X != 0f || m.Y != 0f || m.Z != 0f) classes |= m.W == 0f ? 1 : m.W == 1f ? 2 : 3;
		}

		lo = new Vector3(lx, ly, lz);
		hi = new Vector3(hx, hy, hz);
		float reach = Mathf.Sqrt(reachSq);
		if (reach > maxOffset) maxOffset = reach;
		motionClasses = classes;
	}

	/// <summary>The MultiMesh buffer floats per instance: a Transform3D's 12 and a Color's 4.</summary>
	internal const int MeshInstanceStride = 16;

	/// <summary>
	/// One mesh particle into a MultiMesh buffer as `multimesh_instance_set_transform` and
	/// `_set_color` store it: the basis rows with the origin's component after each, then the colour.
	/// </summary>
	internal static void WriteInstance(Span<float> buffer, int index, in Transform3D transform, Color colour)
	{
		Span<float> d = buffer.Slice(index * MeshInstanceStride, MeshInstanceStride);
		WriteTransform(d, transform);
		d[12] = colour.R; d[13] = colour.G; d[14] = colour.B; d[15] = colour.A;
	}

	/// <summary>The transform part of <see cref="WriteInstance"/> only.</summary>
	internal static void WriteTransform(Span<float> buffer, int index, in Transform3D transform) =>
		WriteTransform(buffer.Slice(index * MeshInstanceStride, MeshInstanceStride), transform);

	private static void WriteTransform(Span<float> d, in Transform3D t)
	{
		Basis b = t.Basis;
		d[0] = b.X.X; d[1] = b.Y.X; d[2] = b.Z.X; d[3] = t.Origin.X;
		d[4] = b.X.Y; d[5] = b.Y.Y; d[6] = b.Z.Y; d[7] = t.Origin.Y;
		d[8] = b.X.Z; d[9] = b.Y.Z; d[10] = b.Z.Z; d[11] = t.Origin.Z;
	}

	/// <summary>
	/// The instances past the drawn count, filled with the first drawn one: never drawn, and
	/// inside the box the drawn ones already make when the server bounds the whole buffer.
	/// </summary>
	internal static void FillTail(Span<float> buffer, int drawn)
	{
		if (drawn <= 0) return;
		ReadOnlySpan<float> first = buffer.Slice(0, MeshInstanceStride);
		for (int i = drawn * MeshInstanceStride; i < buffer.Length; i += MeshInstanceStride)
			first.CopyTo(buffer.Slice(i, MeshInstanceStride));
	}
}
