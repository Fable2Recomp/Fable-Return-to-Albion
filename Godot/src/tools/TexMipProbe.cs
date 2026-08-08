using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Launcher.Formats;
using Refii.Textures;

namespace Refii.Launcher.Tools;

/// <summary>
/// Dumps the levels of one texture — the shipped ones and the synthesised tail — as PNGs,
/// so a filtering artefact can be looked at directly instead of inferred from a model.
///
/// Rendering a model tells you something is wrong at distance. It cannot tell you whether
/// the seed level was read from the wrong offset, the downsample is at fault, or the
/// re-encode is. Each level on disk separates those.
/// </summary>
public partial class TexMipProbe : Node
{
	/// <summary>Texture to probe. <c>REFII_TEX</c> overrides; a path fragment is enough.</summary>
	[Export] public string TexturePath { get; set; } = string.Empty;

	public override void _Ready()
	{
		string outDir = OS.GetEnvironment("REFII_TEX_DUMP");
		if (outDir.Length == 0)
		{
			GD.Print("set REFII_TEX_DUMP to a directory OUTSIDE the repo.");
			GetTree().Quit(1);
			return;
		}

		string dataRoot = UiGameData.Resolve();
		using BnkFileSystem filesystem = BnkFileSystem.Mount(dataRoot);

		string wanted = OS.GetEnvironment("REFII_TEX");
		if (wanted.Length == 0)
		{
			wanted = TexturePath;
		}

		if (OS.GetEnvironment("REFII_TEX_LIST") == "1")
		{
			foreach (BnkFile candidate in filesystem.EnumerateAllFiles()
				.Where(f => f.VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
				.Where(f => wanted.Length == 0 ||
					f.VirtualPath.Contains(wanted, StringComparison.OrdinalIgnoreCase))
				.OrderBy(f => f.VirtualPath, StringComparer.OrdinalIgnoreCase)
				.Take(40))
			{
				string summary = "unreadable";
				try
				{
					summary = Engine_TexData.TryDecode(candidate.ReadAllBytes(),
						out TexSurface probe, out string probeError)
						? $"{probe.Width}x{probe.Height} {probe.Format} " +
							$"{probe.MipLevels}/{Engine_TexImage.FullChainLength(probe.Width, probe.Height)} levels" +
							(probe.MipLevels > 1 ? "  <- gets a tail" : string.Empty)
						: probeError;
				}
				catch (Exception exception)
				{
					summary = exception.GetType().Name;
				}

				GD.Print($"[tex] {summary,-46} {candidate.VirtualPath}");
			}

			GetTree().Quit(0);
			return;
		}

		BnkFile? file = filesystem.EnumerateAllFiles()
			.Where(f => f.VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f.VirtualPath, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault(f => wanted.Length == 0 ||
				f.VirtualPath.Contains(wanted, StringComparison.OrdinalIgnoreCase));

		if (file is null)
		{
			GD.Print($"no .tex matching '{wanted}'");
			GetTree().Quit(1);
			return;
		}

		byte[] bytes = file.ReadAllBytes();
		TexHeader.TryParse(bytes, out TexHeader header, out _);
		if (!Engine_TexData.TryDecode(bytes, out TexSurface surface, out string error))
		{
			GD.Print($"{file.VirtualPath}: {error}");
			GetTree().Quit(1);
			return;
		}

		Directory.CreateDirectory(outDir);
		string stem = Path.GetFileNameWithoutExtension(file.VirtualPath);
		GD.Print($"[tex] {file.VirtualPath}");
		GD.Print($"[tex] {surface.Width}x{surface.Height} {surface.Format}, " +
			$"{surface.MipLevels} shipped levels, {surface.Data.Length:N0} bytes, " +
			$"full chain would be {Engine_TexImage.FullChainLength(surface.Width, surface.Height)}");

		Image.Format format = Engine_TexImage.ToGodotFormat(surface.Format);

		// Walk the SHIPPED levels straight out of the buffer.
		int width = surface.Width;
		int height = surface.Height;
		int offset = 0;
		Image? smallest = null;
		for (int level = 0; level < surface.MipLevels; ++level)
		{
			int size = BlockBytes(width, height, surface.Format);
			if (offset + size > surface.Data.Length)
			{
				GD.Print($"[tex] level {level} runs past the buffer");
				break;
			}

			var slice = new byte[size];
			Buffer.BlockCopy(surface.Data, offset, slice, 0, size);

			Image image = Image.CreateFromData(width, height, false, format, slice);
			image.Decompress();
			Save(image, Path.Combine(outDir, $"{stem}_shipped{level}_{width}x{height}.png"));
			string record = level < header.LevelOffsets.Length && header.LevelOffsets[level] > 0
				? Engine_TexPlanes.DescribeRecord(bytes, header.LevelOffsets[level])
				: "(no offset)";
			GD.Print($"[tex] shipped level {level}: {width}x{height} at +{offset}, {size} bytes | {record}");

			smallest = image;
			offset += size;
			width = Math.Max(1, width / 2);
			height = Math.Max(1, height / 2);
		}

		if (smallest is null)
		{
			GetTree().Quit(1);
			return;
		}

		// GROUND TRUTH: for a texture that ships a full chain, generate level N+1 from
		// level N ourselves and put it next to the artist's own level N+1. Any hue shift the
		// encoder introduces shows up immediately, against real data rather than against a
		// judgement about what the result ought to look like.
		if (OS.GetEnvironment("REFII_TEX_VALIDATE") == "1")
		{
			int vw = surface.Width;
			int vh = surface.Height;
			int voff = 0;
			for (int level = 0; level + 1 < surface.MipLevels; ++level)
			{
				int size = BlockBytes(vw, vh, surface.Format);
				var slice = new byte[size];
				Buffer.BlockCopy(surface.Data, voff, slice, 0, size);

				Image src = Image.CreateFromData(vw, vh, false, format, slice);
				src.Decompress();
				if (src.GetFormat() != Image.Format.Rgba8)
				{
					src.Convert(Image.Format.Rgba8);
				}

				int nw = Math.Max(1, vw / 2);
				int nh = Math.Max(1, vh / 2);
				src.Resize(nw, nh, Image.Interpolation.Lanczos);
				if (src.GetFormat() != Image.Format.Rgba8)
				{
					src.Convert(Image.Format.Rgba8);
				}

				byte[]? ours = Engine_BcEncoder.Encode(src.GetData(), nw, nh, surface.Format);
				if (ours is not null)
                {
					Image mine = Image.CreateFromData(nw, nh, false, format, ours);
					mine.Decompress();
					Save(mine, Path.Combine(outDir, $"{stem}_v{level + 1}_{nw}x{nh}_OURS.png"));
				}

				voff += size;
				vw = nw;
				vh = nh;
			}

			GD.Print($"[tex] validation images written for {surface.MipLevels - 1} levels");
		}

		// Then the SYNTHESISED tail, by the same route Engine_TexImage takes.
		int sw = Math.Max(1, width);
		int sh = Math.Max(1, height);
		while (true)
		{
			var tail = (Image)smallest.Duplicate();
			tail.Resize(sw, sh, Image.Interpolation.Lanczos);

			// Before re-encoding: this is the downsample on its own.
			Save(tail, Path.Combine(outDir, $"{stem}_tail_{sw}x{sh}_a_resized.png"));

			if (tail.GetFormat() != Image.Format.Rgba8)
			{
				tail.Convert(Image.Format.Rgba8);
			}

			byte[]? blocks = Engine_BcEncoder.Encode(tail.GetData(), sw, sh, surface.Format);
			GD.Print($"[tex] tail {sw}x{sh}: {blocks?.Length ?? -1} bytes " +
				$"(expected {BlockBytes(sw, sh, surface.Format)})");
			if (blocks is null)
			{
				break;
			}

			// The round trip back through Godot's decoder, which is where a colour shift
			// would appear if the encoder is the culprit rather than the resize.
			Image back = Image.CreateFromData(
				sw, sh, false, Engine_TexImage.ToGodotFormat(surface.Format), blocks);
			back.Decompress();
			Save(back, Path.Combine(outDir, $"{stem}_tail_{sw}x{sh}_b_encoded.png"));

			if (sw == 1 && sh == 1)
			{
				break;
			}

			sw = Math.Max(1, sw / 2);
			sh = Math.Max(1, sh / 2);
		}

		GD.Print($"[tex] wrote to {outDir}");
		GetTree().Quit(0);
	}

	private static void Save(Image image, string path)
	{
		var copy = (Image)image.Duplicate();
		if (copy.IsCompressed())
		{
			copy.Decompress();
		}

		// Magnify so a 4x4 level is actually inspectable; nearest keeps the texels honest.
		if (copy.GetWidth() < 64)
		{
			int scale = Math.Max(1, 64 / Math.Max(1, copy.GetWidth()));
			copy.Resize(copy.GetWidth() * scale, copy.GetHeight() * scale,
				Image.Interpolation.Nearest);
		}

		copy.SavePng(path);
	}

	private static int BlockBytes(int width, int height, TexSurfaceFormat format) =>
		Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) *
		(format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8);
}
