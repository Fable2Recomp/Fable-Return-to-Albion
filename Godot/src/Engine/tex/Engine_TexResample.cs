using System;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Textures;

/// <summary>
/// The built-in upscaler: a good classical resample, no model and no dependency.
/// </summary>
/// <remarks>
/// IT IS NOT A STAND-IN FOR REAL-ESRGAN, it is the baseline that one has to beat. Two reasons
/// it exists before the neural backend rather than after it:
///
/// The whole §6 machinery — the priority queue, the workers, the re-encode, the cache round
/// trip, the main-thread swap — was untestable with no backend registered. Everything downstream
/// of <c>ITexUpscaler</c> had never run. This exercises all of it in a real session without
/// vendoring ncnn or a Vulkan inference context.
///
/// And §3.2 asks for original semantics over a generic guess. A classical resample invents
/// nothing: it cannot hallucinate a brick edge that the artist did not author, which is exactly
/// the failure mode a perceptual model has on 2008 console art. Where it wins is the same place
/// the GPU's magnification filter loses — it is a proper reconstruction filter rather than
/// bilinear stretch — and where it loses is genuine detail synthesis, which is what the neural
/// backend is for.
/// </remarks>
public sealed class Engine_TexResample : ITexUpscaler
{
	/// <summary>
	/// Rides in the cache key, so the trailing number is not decoration.
	/// </summary>
	/// <remarks>
	/// Bump it on any change to the filtering below. Without that, a change to the gamma handling
	/// or the filter choice leaves every texture already on disk answering with the old result and
	/// the change reads as having done nothing.
	///
	/// NOT bumped for the zero-copy rework of the buffers: it removed two full-size allocations
	/// per pass and changed no arithmetic, so the output is bit-identical and every cached entry
	/// is still correct. Bumping on a change that cannot alter the result would throw away a
	/// region's worth of work to prove nothing, and it teaches the next reader that the rule is
	/// decorative.
	/// </remarks>
	public string Id => "resample-1";

	public bool TryUpscale(Image source, TexFilterClass filter, int scale, out Image? result)
	{
		result = null;

		if (scale <= 1 || source.IsCompressed() || source.HasMipmaps())
		{
			// The contract says decompressed, mipless RGBA8. Declining beats quietly coping,
			// because coping here would hide a caller that stopped honouring it.
			return false;
		}

		int width = source.GetWidth() * scale;
		int height = source.GetHeight() * scale;

		switch (filter)
		{
			case TexFilterClass.Perceptual:
				result = Colour(source, width, height);
				return result is not null;

			case TexFilterClass.NormalAware:
				result = Normal(source, width, height);
				return result is not null;

			case TexFilterClass.Scalar:
				result = Scalar(source, width, height);
				return result is not null;

			default:
				return false;
		}
	}

	/// <summary>
	/// Colour, resampled in LINEAR light and returned gamma encoded.
	/// </summary>
	/// <remarks>
	/// THE ENCODING IS GAMMA 2.0, NOT sRGB. Fable's pixel shaders decode colour by squaring —
	/// <c>r4.yzw = r4.yzw * r4.yzw</c> in PSHADER_STANDARDMATERIAL_..._AMB1 — so squaring is the
	/// decode and the square root is the encode, exactly. Using sRGB's ~2.4 curve with its linear
	/// toe would shift every midtone.
	///
	/// Resampling gamma-encoded values directly is wrong in a specific, visible way: the average
	/// of two encoded values is not the encoding of their average, so every high-contrast edge
	/// darkens as it is filtered. That is worth undoing HERE, where the whole point is to add
	/// resolution, even though <see cref="Engine_TexMipGen"/> deliberately box filters in gamma
	/// space for the mip chain — changing that would alter how existing textures look at
	/// distance, which is a different question from this one and not one this class should
	/// decide.
	///
	/// ALPHA IS NEVER LINEARIZED. It is coverage, not light, and on an albedo it is also the
	/// scissor threshold the material tests against.
	///
	/// Float throughout. Squaring in 8 bits and taking the root again quantizes the darks to a
	/// handful of distinct values, which reads as banding in every shadowed area.
	/// </remarks>
	private static Image? Colour(Image source, int width, int height)
	{
		var work = (Image)source.Duplicate();
		work.Convert(Image.Format.Rgbaf);

		byte[] before = work.GetData();
		Span<float> texels = MemoryMarshal.Cast<byte, float>(before.AsSpan());
		for (int i = 0; i + 3 < texels.Length; i += 4)
		{
			texels[i] *= texels[i];
			texels[i + 1] *= texels[i + 1];
			texels[i + 2] *= texels[i + 2];
		}

		work.SetData(work.GetWidth(), work.GetHeight(), false, Image.Format.Rgbaf, before);

		// Lanczos is the right filter for colour and the wrong one for everything else in this
		// file: it is the sharpest of the reconstruction filters Godot offers, and it rings.
		// Ringing on a photographic surface reads as acuity; on a normal or a mask it is an
		// out-of-range value.
		work.Resize(width, height, Image.Interpolation.Lanczos);

		byte[] after = work.GetData();
		texels = MemoryMarshal.Cast<byte, float>(after.AsSpan());
		for (int i = 0; i + 3 < texels.Length; i += 4)
		{
			// Clamped before the root because Lanczos overshoots below zero at a hard edge, and
			// the root of a negative is NaN — which survives the conversion back to 8 bits as
			// whatever the cast happens to produce.
			texels[i] = MathF.Sqrt(Math.Clamp(texels[i], 0.0f, 1.0f));
			texels[i + 1] = MathF.Sqrt(Math.Clamp(texels[i + 1], 0.0f, 1.0f));
			texels[i + 2] = MathF.Sqrt(Math.Clamp(texels[i + 2], 0.0f, 1.0f));
			texels[i + 3] = Math.Clamp(texels[i + 3], 0.0f, 1.0f);
		}

		work.SetData(work.GetWidth(), work.GetHeight(), false, Image.Format.Rgbaf, after);
		work.Convert(Image.Format.Rgba8);
		return work;
	}

	/// <summary>
	/// A tangent-space normal, resampled and then put back on the unit sphere.
	/// </summary>
	/// <remarks>
	/// XY ONLY, because that is all the guest stores and all it reads: it fetches
	/// <c>.xy</c> from g_NormalTextureSampler and rebuilds the third component as
	/// <c>sqrt(1 - nx^2 - ny^2)</c>. So Z is not data to be preserved, it is a consequence, and
	/// the one thing that must hold after resampling is <c>nx^2 + ny^2 &lt;= 1</c> — the value
	/// under that square root cannot go negative.
	///
	/// Averaging two unit normals shortens the result, which is correct and is what makes a
	/// filtered normal map read as a smoother surface. The failure is in the other direction:
	/// an overshooting filter can push the length past 1 and put a NaN in the guest's
	/// reconstruction. Cubic rather than Lanczos for that reason, and the clamp below for the
	/// residue.
	///
	/// No gamma anywhere. A normal is a direction, not light.
	/// </remarks>
	private static Image? Normal(Image source, int width, int height)
	{
		var work = (Image)source.Duplicate();
		work.Resize(width, height, Image.Interpolation.Cubic);
		work.Convert(Image.Format.Rgba8);

		byte[] texels = work.GetData();
		for (int i = 0; i + 3 < texels.Length; i += 4)
		{
			float nx = (texels[i] / 255.0f) * 2.0f - 1.0f;
			float ny = (texels[i + 1] / 255.0f) * 2.0f - 1.0f;

			float length = MathF.Sqrt((nx * nx) + (ny * ny));
			if (length <= 1.0f)
			{
				continue;
			}

			nx /= length;
			ny /= length;
			texels[i] = Encode(nx);
			texels[i + 1] = Encode(ny);
		}

		work.SetData(width, height, false, Image.Format.Rgba8, texels);
		return work;
	}

	/// <summary>
	/// A control signal: resampled per channel, with nothing mixed across channels.
	/// </summary>
	/// <remarks>
	/// This is the specular path, and the channel that decides it is ALPHA — the shipped shader
	/// builds <c>power = lerp(c24.x, c24.y, spec.a)</c>, so alpha selects the exponent rather
	/// than describing an image. Cubic, not Lanczos: an overshoot in that channel is an exponent
	/// outside the range the material declared, and it would show up as a highlight the artist
	/// never authored rather than as extra sharpness.
	///
	/// No gamma. The RGB here IS squared by the shader like the albedo, but the alpha beside it
	/// is not, and splitting the channels apart to treat them differently would mix a control
	/// signal into a colour transform for a map whose visible contribution is a specular tint.
	/// </remarks>
	private static Image? Scalar(Image source, int width, int height)
	{
		var work = (Image)source.Duplicate();
		work.Resize(width, height, Image.Interpolation.Cubic);
		work.Convert(Image.Format.Rgba8);
		return work;
	}

	private static byte Encode(float component) =>
		(byte)Math.Clamp(MathF.Round(((component * 0.5f) + 0.5f) * 255.0f), 0.0f, 255.0f);

}
