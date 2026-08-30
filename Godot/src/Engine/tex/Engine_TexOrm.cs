using System;
using Godot;

namespace Refii.Textures;

/// <summary>
/// Packs Fable 2's material data into Godot's ORM layout: R = AO, G = roughness, B = metallic.
/// </summary>
/// <remarks>
/// WHAT THE SHIPPED DATA ACTUALLY HAS is the thing to be clear about before reading any of this.
/// A material record names five textures — diffuse, specular, normal, environment map, self
/// illumination — and NOT ONE of them is an AO map, a metallic map, a roughness map or a height
/// map. The specular texture is already a packed material map in its own right: RGB is specular
/// colour, which also weights the environment reflection, and ALPHA is the exponent selector the
/// shader turns into roughness.
///
/// SHIPPED DATA ALONE FILLS ALMOST NOTHING, and the roughness channel is a weaker claim than it
/// looks. `src/tools/SpecularCensus.cs` opened 600 of the 1,193 specular textures the material
/// records actually name: **584 of them have no alpha channel at all**. The shader's roughness is
/// `lerp(c24.x, c24.y, spec.a)`, so for 97% of materials the selector reads 1.0 and the roughness
/// is a CONSTANT. Only 16 textures carry a real exponent map, and there it is strongly bimodal
/// (p50 0.086, p90 1.000).
///
/// So on shipped data alone the packing is three constants wearing a texture, which is why it
/// was — and by default still is — off:
///
///     R = 1      no ambient occlusion exists to pack
///     G = roughness, but a constant for 97% of materials
///     B = 0      the guest sets metallic to zero unconditionally
///
/// §25 PHASE 5 IS WHAT MAKES IT WORTH ANYTHING, and two of the three channels now have a derived
/// source: R from <see cref="CavityFromNormal"/>, and B from <see cref="Metallic"/>, which reads
/// the specular map as the F0 map it already is. Both are arithmetic on shipped data — no model,
/// no invented detail — and both were measured before they were written rather than after.
///
/// It also cannot replace the specular map. There is no ORM slot for specular COLOUR, and
/// dropping it would take the environment reflection's weight with it — the guest's line is
/// `colour += specular.rgb^2 * env^2`.
/// </remarks>
public static class Engine_TexOrm
{
	/// <summary>
	/// Cavity occlusion derived from a tangent-space normal map.
	/// </summary>
	/// <remarks>
	/// DERIVED, NOT INVENTED, and the distinction is the whole reason this is allowed to exist
	/// under §3.2. It is not a model guessing what occlusion "should" look like — it is the
	/// divergence of the normal field the artist actually authored. Where neighbouring normals
	/// turn toward each other the surface is concave and light reaches it less; where they splay
	/// apart it is a ridge. That is arithmetic on shipped data, repeatable to the bit, and it adds
	/// no detail that is not already in the normal map.
	///
	/// It is still a CHANGE OF APPEARANCE. The guest applies no ambient occlusion at all, so this
	/// makes the game look different from the original — which is why the whole ORM path is off
	/// unless asked for, and why the strength is a setting rather than a constant.
	///
	/// Only XY are read, because only XY are stored: the guest rebuilds Z as
	/// <c>sqrt(1 - nx^2 - ny^2)</c> and a normal map's blue channel is not data.
	/// </remarks>
	private static byte[] CavityFromNormal(Image normal, int width, int height, float strength)
	{
		var work = (Image)normal.Duplicate();
		work.ClearMipmaps();
		if (work.IsCompressed())
		{
			work.Decompress();
		}

		work.Convert(Image.Format.Rgba8);

		// The normal and the specular are independent textures and need not share a size, so the
		// normal is resampled to whatever the ORM is being built at. Bilinear, because a sharper
		// filter would ring and ringing in a gradient field produces cavities that are not there.
		if (work.GetWidth() != width || work.GetHeight() != height)
		{
			work.Resize(width, height, Image.Interpolation.Bilinear);
		}

		byte[] pixels = work.GetData();
		var cavity = new byte[width * height];

		for (int y = 0; y < height; ++y)
		{
			for (int x = 0; x < width; ++x)
			{
				// Clamped at the edges rather than wrapped. A wrapped read makes the left edge's
				// occlusion depend on the right edge's normals, which is visible as a seam on
				// anything that is not actually tiling.
				int xm = Math.Max(0, x - 1);
				int xp = Math.Min(width - 1, x + 1);
				int ym = Math.Max(0, y - 1);
				int yp = Math.Min(height - 1, y + 1);

				float nxLeft = Decode(pixels, (y * width) + xm, 0);
				float nxRight = Decode(pixels, (y * width) + xp, 0);
				float nyUp = Decode(pixels, (ym * width) + x, 1);
				float nyDown = Decode(pixels, (yp * width) + x, 1);

				// Divergence. Negative where the field converges — a crevice.
				float divergence = (nxRight - nxLeft) + (nyDown - nyUp);

				float occlusion = Math.Clamp(1.0f + (divergence * strength), 0.0f, 1.0f);
				cavity[(y * width) + x] =
					(byte)Math.Clamp(MathF.Round(occlusion * 255.0f), 0.0f, 255.0f);
			}
		}

		return cavity;
	}

	/// <summary>
	/// Cavity for a TILING source, straight from decoded RGBA bytes.
	/// </summary>
	/// <remarks>
	/// The terrain's layer textures repeat across the whole ground, so the neighbourhood WRAPS —
	/// the clamped variant above is right for a prop's unwrapped UVs and puts a visible seam line
	/// at every tile boundary here, repeated over the entire region. Same divergence, same
	/// strength semantics, different topology.
	/// </remarks>
	public static byte[] CavityFromTilingNormal(
		byte[] rgba, int width, int height, float strength)
	{
		var cavity = new byte[width * height];

		for (int y = 0; y < height; ++y)
		{
			int ym = ((y - 1) + height) % height;
			int yp = (y + 1) % height;

			for (int x = 0; x < width; ++x)
			{
				int xm = ((x - 1) + width) % width;
				int xp = (x + 1) % width;

				float nxLeft = Decode(rgba, (y * width) + xm, 0);
				float nxRight = Decode(rgba, (y * width) + xp, 0);
				float nyUp = Decode(rgba, (ym * width) + x, 1);
				float nyDown = Decode(rgba, (yp * width) + x, 1);

				float divergence = (nxRight - nxLeft) + (nyDown - nyUp);
				float occlusion = Math.Clamp(1.0f + (divergence * strength), 0.0f, 1.0f);
				cavity[(y * width) + x] =
					(byte)Math.Clamp(MathF.Round(occlusion * 255.0f), 0.0f, 255.0f);
			}
		}

		return cavity;
	}

	/// <summary>
	/// Metallic, derived from the specular map's colour. §25 phase 5.
	/// </summary>
	/// <remarks>
	/// THE SPECULAR MAP IS AN F0 MAP IN ALL BUT NAME, and that is what makes this a derivation
	/// rather than an invention. A dielectric's reflectance at normal incidence is grey and dim —
	/// about 0.04 for almost everything that is not a metal — while a metal's is BRIGHT and
	/// COLOURED, because a metal's reflection carries its own hue: copper is orange, gold is
	/// yellow, steel is bright but neutral. Fable 2's specular RGB is exactly that quantity: the
	/// shipped pixel shader weights the environment reflection with it,
	/// <c>colour += specular.rgb^2 * env^2</c>.
	///
	/// SO THE PAIR IS THE SIGNAL, NOT CHROMA ALONE. A dark, faintly tinted texel is a tinted
	/// dielectric highlight and there is a great deal of that; a bright coloured one is metal.
	/// Requiring both is what separates them.
	///
	/// MEASURED BEFORE IT WAS WRITTEN, by <c>src/tools/SpecularCensus.cs</c> over the 1,193
	/// distinct specular textures the material records actually name:
	///
	///     chroma        p50 0.031   p90 0.165   p99 0.518
	///     luminance     p50 0.144   p90 0.520   p99 0.932
	///     127 of 600 textures are >=25% bright AND coloured
	///
	/// A fifth of the game's specular maps describe metal, and the median texel is grey and dark.
	/// That is a real distribution with a real tail, not uniform noise — had it come back grey
	/// everywhere, this function would not exist and phase 5 would have got AO and nothing else.
	///
	/// NEUTRAL METALS ARE THE KNOWN GAP. Polished steel is bright and grey, and this reports it as
	/// a dielectric because brightness alone cannot tell it from a strong highlight on painted
	/// wood. Under-reporting is the right direction to fail in: a wrongly metallic surface loses
	/// its diffuse colour entirely and reads as a mirror, while a wrongly dielectric one is merely
	/// the shipped look.
	/// </remarks>
	private static byte Metallic(float r, float g, float b, float strength)
	{
		if (strength <= 0.0f)
		{
			return 0;
		}

		float max = Math.Max(r, Math.Max(g, b));
		float min = Math.Min(r, Math.Min(g, b));
		float chroma = max - min;
		float luminance = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);

		// SMOOTHSTEP RATHER THAN A THRESHOLD, because a hard cut puts a visible outline wherever
		// the specular map crosses it — the census's p50 chroma of 0.031 and p90 of 0.165 mean a
		// great many texels sit near any threshold that would be chosen.
		float colouredness = Smoothstep(0.06f, 0.35f, chroma);
		float brightness = Smoothstep(0.20f, 0.60f, luminance);

		return (byte)Math.Clamp(
			MathF.Round(colouredness * brightness * strength * 255.0f), 0.0f, 255.0f);
	}

	private static float Smoothstep(float edge0, float edge1, float value)
	{
		float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
		return t * t * (3.0f - (2.0f * t));
	}

	private static float Decode(byte[] pixels, int texel, int channel) =>
		((pixels[(texel * 4) + channel] / 255.0f) * 2.0f) - 1.0f;

	/// <summary>
	/// Builds an ORM image from a specular map, an optional normal map, and the exponent range.
	/// </summary>
	/// <remarks>
	/// The roughness conversion is the shader's, character for character: the guest builds
	/// <c>power = lerp(c24.x, c24.y, spec.a)</c> and the Godot side turns that exponent into
	/// <c>sqrt(2 / (power + 2))</c>. Recomputing it differently here would put a second,
	/// disagreeing definition of roughness in the codebase, and the two would drift.
	///
	/// <paramref name="specular"/> arrives however it was decoded — usually block compressed — so
	/// it is decompressed and converted first. The result is RGBA8 and uncompressed; the caller
	/// compresses and mips it like any other derived texture.
	/// </remarks>
	public static Image? TryPack(
		Image? specular,
		Image? normal,
		float powerMin,
		float powerMax,
		float aoStrength,
		float metallicStrength,
		bool withHeight,
		out bool packedHeight)
	{
		packedHeight = false;

		// A NORMAL MAP ALONE IS ENOUGH: two of the four channels — AO and height — derive from
		// it and need no specular at all. The census says 57.5% of records name a specular but
		// 89.8% name a normal, so gating the whole pack on the specular silently cost a third of
		// the asset set its occlusion and parallax. Without a specular the roughness and metallic
		// stay at the shader's own no-specular values (1.0 and 0), so behaviour is unchanged
		// where it was already right.
		byte[]? pixels = null;
		int width;
		int height;
		if (specular is not null)
		{
			var source = (Image)specular.Duplicate();
			source.ClearMipmaps();
			if (source.IsCompressed())
			{
				source.Decompress();
			}

			source.Convert(Image.Format.Rgba8);
			width = source.GetWidth();
			height = source.GetHeight();
			pixels = source.GetData();
		}
		else if (normal is not null)
		{
			width = normal.GetWidth();
			height = normal.GetHeight();
		}
		else
		{
			return null;
		}

		if (width <= 0 || height <= 0)
		{
			return null;
		}

		var orm = new byte[width * height * 4];

		// AO comes from the NORMAL map when there is one and the player asked for it. Without
		// either, R stays at 1 and the channel carries no information — which is the honest
		// answer, because Fable 2 ships no occlusion data of its own.
		byte[]? cavity = normal is not null && aoStrength > 0.0f
			? CavityFromNormal(normal, width, height, aoStrength)
			: null;

		// HEIGHT RIDES IN THE ALPHA, which is free: Godot's ORM convention uses R, G and B and says
		// nothing about the fourth channel. One texture then carries every derived scalar a
		// material has, instead of a fourth binding for a single byte.
		//
		// It costs the block format, and that is the honest trade to state: a constant alpha
		// compresses as DXT1, a varying one forces DXT5 and twice the bytes. TrySolve returns null
		// for a flat normal map precisely so a texture with no relief keeps the cheaper format.
		byte[]? relief = normal is not null && withHeight
			? Engine_TexHeight.TrySolve(normal, width, height)
			: null;
		packedHeight = relief is not null;

		if (cavity is null && relief is null && pixels is null)
		{
			// Nothing derivable: an ORM of four constants carries no information.
			return null;
		}

		for (int i = 0; i + 3 < orm.Length; i += 4)
		{
			orm[i] = cavity is null ? (byte)255 : cavity[i / 4];  // AO: derived, or absent

			if (pixels is not null && i + 3 < pixels.Length)
			{
				// ALPHA is the exponent selector. The RGB beside it is specular colour and has
				// no place in ORM, which is exactly why the specular texture still has to be
				// bound.
				float alpha = pixels[i + 3] / 255.0f;
				float power = powerMin + ((powerMax - powerMin) * alpha);
				float roughness = Math.Clamp(MathF.Sqrt(2.0f / (power + 2.0f)), 0.0f, 1.0f);

				orm[i + 1] = (byte)Math.Clamp(
					MathF.Round(roughness * 255.0f), 0.0f, 255.0f); // roughness
				orm[i + 2] = Metallic(
					pixels[i] / 255.0f, pixels[i + 1] / 255.0f, pixels[i + 2] / 255.0f,
					metallicStrength);
			}
			else
			{
				// The shader's own no-specular values, so a normal-only ORM changes nothing
				// the specular path had already settled.
				orm[i + 1] = 255;
				orm[i + 2] = 0;
			}

			orm[i + 3] = relief is null ? (byte)255 : relief[i / 4];
		}

		return Image.CreateFromData(width, height, false, Image.Format.Rgba8, orm);
	}

	/// <summary>
	/// The cache identity for one packed result.
	/// </summary>
	/// <remarks>
	/// The exponent range belongs in the key because the packing depends on it. Every shipped
	/// record states (1, 100), so in practice this is one value — but a material that varied it
	/// would otherwise be served another material's roughness, and a cache that can serve the
	/// wrong answer for a reason nobody wrote down is worse than no cache.
	/// </remarks>
	public static string CacheId(float powerMin, float powerMax) =>
		$"orm-{powerMin:R}-{powerMax:R}";
}
