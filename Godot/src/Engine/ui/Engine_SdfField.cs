using System;

namespace Refii.Ui;

/// <summary>
/// A signed distance field from a coverage mask — the form UI text draws from, so a glyph's edge
/// stays one screen pixel wide at any magnification instead of blurring with the texture.
/// </summary>
/// <remarks>
/// <para>Exact squared Euclidean distances (Felzenszwalb–Huttenlocher, two separable passes) to the
/// nearest pixel of the other side of the 0.5 coverage contour, less the half pixel to that
/// pixel's edge. Inside the anti-aliased band the coverage itself is the finer measure — a
/// smoothly upsampled mask ramps 0→1 across about <c>edgeWidth</c> pixels — so there the distance
/// is <c>(0.5 − coverage) × edgeWidth</c>.</para>
/// <para>Encoding: <c>0.5 − s / (2 · spread)</c>, s in pixels, negative inside, clamped to a byte. The
/// 0.5 contour is the coverage's own half point, so the glyphs keep their shipped weight.</para>
/// <para>Pure .NET: held by tests without a Godot type in the host.</para>
/// </remarks>
public static class Engine_SdfField
{
	private const float Far = 1e20f;

	/// <param name="coverage">Row-major coverage 0..1, <paramref name="width"/> × <paramref name="height"/>.</param>
	/// <param name="spread">Pixels of distance either side of the edge the byte range spans.</param>
	/// <param name="edgeWidth">Pixels the coverage takes to ramp across an edge.</param>
	public static byte[] FromCoverage(ReadOnlySpan<float> coverage, int width, int height,
		float spread, float edgeWidth)
	{
		int count = width * height;
		if (coverage.Length < count || width <= 0 || height <= 0)
		{
			throw new ArgumentException("coverage is smaller than width × height");
		}

		var toInside = new float[count];
		var toOutside = new float[count];
		for (int i = 0; i < count; ++i)
		{
			bool inside = coverage[i] >= 0.5f;
			toInside[i] = inside ? 0f : Far;
			toOutside[i] = inside ? Far : 0f;
		}

		Transform(toInside, width, height);
		Transform(toOutside, width, height);

		float band = edgeWidth * 0.5f;
		float scale = 1f / (2f * MathF.Max(spread, 1e-3f));
		var field = new byte[count];
		for (int i = 0; i < count; ++i)
		{
			float a = coverage[i];
			float s = a >= 0.5f
				? -(MathF.Sqrt(toOutside[i]) - 0.5f)
				: MathF.Sqrt(toInside[i]) - 0.5f;

			if (a > 0f && a < 1f)
			{
				float fine = (0.5f - a) * edgeWidth;
				if (MathF.Abs(fine) <= band)
				{
					s = fine;
				}
			}

			float value = Math.Clamp(0.5f - (s * scale), 0f, 1f);
			field[i] = (byte)MathF.Round(value * 255f);
		}

		return field;
	}

	/// <summary>The 2D squared distance transform in place: columns, then rows.</summary>
	private static void Transform(float[] grid, int width, int height)
	{
		int longest = Math.Max(width, height);
		var f = new float[longest];
		var d = new float[longest];
		var v = new int[longest];
		var z = new float[longest + 1];

		for (int x = 0; x < width; ++x)
		{
			for (int y = 0; y < height; ++y)
			{
				f[y] = grid[(y * width) + x];
			}

			Transform1D(f, d, v, z, height);
			for (int y = 0; y < height; ++y)
			{
				grid[(y * width) + x] = d[y];
			}
		}

		for (int y = 0; y < height; ++y)
		{
			int row = y * width;
			for (int x = 0; x < width; ++x)
			{
				f[x] = grid[row + x];
			}

			Transform1D(f, d, v, z, width);
			for (int x = 0; x < width; ++x)
			{
				grid[row + x] = d[x];
			}
		}
	}

	/// <summary>The lower envelope of parabolas over one line (Felzenszwalb & Huttenlocher 2012).</summary>
	private static void Transform1D(float[] f, float[] d, int[] v, float[] z, int n)
	{
		int k = 0;
		v[0] = 0;
		z[0] = -Far;
		z[1] = Far;
		for (int q = 1; q < n; ++q)
		{
			float s = Intersection(f, q, v[k]);
			while (s <= z[k])
			{
				--k;
				s = Intersection(f, q, v[k]);
			}

			++k;
			v[k] = q;
			z[k] = s;
			z[k + 1] = Far;
		}

		k = 0;
		for (int q = 0; q < n; ++q)
		{
			while (z[k + 1] < q)
			{
				++k;
			}

			float dq = q - v[k];
			d[q] = (dq * dq) + f[v[k]];
		}
	}

	private static float Intersection(float[] f, int q, int p) =>
		((f[q] + (q * (float)q)) - (f[p] + (p * (float)p))) / (2f * (q - p));
}
