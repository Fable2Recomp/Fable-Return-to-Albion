using System;
using Godot;

namespace Refii.Textures;

/// <summary>
/// Height, integrated from a tangent-space normal map. §25 phase 5's last derived map.
/// </summary>
/// <remarks>
/// DERIVED, NOT INVENTED, on the same footing as the AO and metallic channels beside it. A
/// tangent-space normal IS a surface gradient — <c>(dh/dx, dh/dy) = (-nx/nz, -ny/nz)</c> — so a
/// height field is recovered from one by integration rather than guessed at by a model. Fable 2
/// ships no height map and §25 phase 5 asks for one; this is what makes parallax possible without
/// inventing relief that was never authored.
///
/// MEASURED BEFORE IT WAS WRITTEN, by <c>src/tools/NormalCensus.cs</c> over the 1,701 distinct
/// normal maps the material records name (8,016 of 8,931 records — 89.8% — name one):
///
///     relief fraction   p10 0.193   p50 0.786   p90 0.994
///     curl / gradient   p10 0.125   p50 0.255   p90 0.437   max 0.694
///
/// The first line says there is relief to recover: 346 of 400 maps are at least a quarter
/// non-flat, unlike the specular maps, whose median texel was grey.
///
/// THE SECOND LINE DECIDED THE ALGORITHM, and it is why this is not a running sum. Integrating a
/// gradient has a unique answer only if the field is conservative — curl zero — and a quarter to a
/// half of the shipped gradient magnitude is curl. Artists paint normal maps; nothing forces them
/// to be integrable. A cumulative sum along rows would accumulate that inconsistency into visible
/// drift and smearing, so the height is instead the LEAST-SQUARES fit: the field whose gradient
/// best matches, solving the Poisson equation <c>∇²h = div(g)</c>, which discards the curl rather
/// than smearing it.
///
/// BAND LIMITED ON PURPOSE, which is also why a bounded iteration count is correct rather than a
/// compromise. Gauss-Seidel converges high frequencies in a handful of passes and low frequencies
/// only slowly — and for a TILING detail texture the low frequencies are not merely unnecessary,
/// they are harmful: a global tilt or bowl across the texture makes adjacent tiles disagree at
/// every seam. Stopping early solves exactly the local relief parallax needs and leaves the
/// large-scale shape at zero, where a tiling texture wants it.
/// </remarks>
public static class Engine_TexHeight
{
	/// <summary>
	/// Red-black Gauss-Seidel passes over the Poisson system.
	/// </summary>
	/// <remarks>
	/// 24 rather than "until it converges", because converging is the wrong target — see the band
	/// limit above. It is enough to settle detail up to roughly a dozen texels across, which is the
	/// scale parallax displaces at, and cheap: the whole solve is a few passes over the image on a
	/// background worker, once per texture in the lifetime of the cache.
	/// </remarks>
	private const int Passes = 24;

	/// <summary>
	/// The smallest nz the gradient is allowed to be divided by.
	/// </summary>
	/// <remarks>
	/// At a silhouette nz tends to zero and <c>-nx/nz</c> tends to infinity. Without a floor a
	/// handful of texels would dominate the whole solve and the result would be a picture of the
	/// division rather than of the surface. 0.2 caps the gradient at 5, which is a slope no
	/// authored detail reaches.
	/// </remarks>
	private const float MinZ = 0.2f;

	/// <summary>
	/// Solves for a height field and returns it as one byte per texel, or null if there is none.
	/// </summary>
	/// <param name="normal">A tangent-space normal map, uncompressed RGBA8.</param>
	/// <param name="width">The ORM's width; the normal is sampled to it.</param>
	/// <param name="height">The ORM's height.</param>
	/// <returns>
	/// Height in 0..255, centred on 128, or null when the map carries no usable relief.
	/// </returns>
	/// <remarks>
	/// RETURNS NULL RATHER THAN A FLAT FIELD when the normal map carries no relief. For such a
	/// texture a height channel is a constant that costs the ORM its alpha and forces a larger
	/// block format, so saying "there is no height here" lets the caller keep the cheaper one.
	/// </remarks>
	public static byte[]? TrySolve(Image normal, int width, int height)
	{
		if (width < 4 || height < 4)
		{
			return null;
		}

		var source = (Image)normal.Duplicate();
		source.ClearMipmaps();
		if (source.IsCompressed())
		{
			source.Decompress();
		}

		source.Convert(Image.Format.Rgba8);
		if (source.GetWidth() != width || source.GetHeight() != height)
		{
			source.Resize(width, height, Image.Interpolation.Bilinear);
		}

		byte[] pixels = source.GetData();
		int texels = width * height;
		if (pixels.Length < texels * 4)
		{
			return null;
		}

		var gx = new float[texels];
		var gy = new float[texels];
		var relief = 0;

		for (var i = 0; i < texels; ++i)
		{
			// X IN R, Y IN G, decoded as v*2-1, and Z RECONSTRUCTED rather than read: a normal
			// map's blue channel is not data. The same convention Engine_TexOrm's cavity uses, so
			// the two derivations cannot disagree about what a normal is.
			float nx = (pixels[i * 4] / 255.0f * 2.0f) - 1.0f;
			float ny = (pixels[(i * 4) + 1] / 255.0f * 2.0f) - 1.0f;
			float nz = MathF.Sqrt(Math.Max(1.0f - (nx * nx) - (ny * ny), MinZ * MinZ));

			gx[i] = -nx / nz;
			gy[i] = -ny / nz;

			if (MathF.Sqrt((gx[i] * gx[i]) + (gy[i] * gy[i])) >= 0.02f)
			{
				++relief;
			}
		}

		// FLAT MEANS FLAT, not "less relief than the median texture". This gate exists only to
		// reject a normal map that carries no relief at all, so that a texture with nothing to say
		// keeps DXT1 instead of paying DXT5 for a constant alpha.
		//
		// It was 25%, borrowed from the census's "346 of 400 maps are at least a quarter non-flat".
		// That is a statistic about the population, not a threshold for usefulness, and using it as
		// one threw away every texture whose relief is LOCAL — a plate with rivets on a fifth of
		// its area has real height on that fifth. The probe caught it: a synthetic bump covering
		// 19.6% of the image was declined.
		//
		// 2% is above the 8-bit quantisation floor and below anything an artist drew on purpose.
		// The range check after the solve is what rejects the rest.
		if (relief * 50 < texels)
		{
			return null;
		}

		// THE RIGHT-HAND SIDE is the divergence of the gradient field. Central differences, and
		// WRAPPED rather than clamped, because these textures tile — a clamped edge solves a
		// different problem at the border and puts a seam exactly where tiling makes it repeat.
		var divergence = new float[texels];
		for (var y = 0; y < height; ++y)
		{
			int up = ((y - 1 + height) % height) * width;
			int down = ((y + 1) % height) * width;
			int row = y * width;

			for (var x = 0; x < width; ++x)
			{
				int left = (x - 1 + width) % width;
				int right = (x + 1) % width;

				divergence[row + x] =
					((gx[row + right] - gx[row + left]) * 0.5f) +
					((gy[down + x] - gy[up + x]) * 0.5f);
			}
		}

		var field = new float[texels];
		var next = new float[texels];

		for (var pass = 0; pass < Passes; ++pass)
		{
			for (var y = 0; y < height; ++y)
			{
				int up = ((y - 1 + height) % height) * width;
				int down = ((y + 1) % height) * width;
				int row = y * width;

				for (var x = 0; x < width; ++x)
				{
					int left = (x - 1 + width) % width;
					int right = (x + 1) % width;

					// Jacobi rather than in-place Gauss-Seidel: the update is order independent,
					// so it gives the same answer however the loop is arranged, which matters for
					// a value that lands in a content-addressed cache.
					next[row + x] = 0.25f * (
						field[row + left] + field[row + right] +
						field[up + x] + field[down + x] -
						divergence[row + x]);
				}
			}

			(field, next) = (next, field);
		}

		// ANCHORED ON THE MEAN, not stretched min-to-max. The solve has an arbitrary additive
		// constant — Poisson fixes the gradient, not the level — and the consumer treats 0.5 as
		// the plane that does not move. A min/max stretch put that plane wherever the relief's
		// shape happened to land it: a texture that is mostly flat with a few deep pits ended
		// near 1.0 across its flat majority and the whole surface displaced constantly. Mapping
		// the mean to 128 and scaling by the larger excursion keeps the shape, keeps both tails
		// in range, and pins the typical texel to zero offset.
		float min = float.MaxValue;
		float max = float.MinValue;
		float sum = 0.0f;
		foreach (float value in field)
		{
			min = Math.Min(min, value);
			max = Math.Max(max, value);
			sum += value;
		}

		if (max - min <= 1e-6f)
		{
			return null;
		}

		float mean = sum / texels;
		float scale = 127.0f / Math.Max(max - mean, mean - min);

		var result = new byte[texels];
		for (var i = 0; i < texels; ++i)
		{
			result[i] = (byte)Math.Clamp(
				MathF.Round(128.0f + ((field[i] - mean) * scale)), 0.0f, 255.0f);
		}

		return result;
	}
}
