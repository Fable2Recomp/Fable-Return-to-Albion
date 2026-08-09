using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using Refii.Textures;

namespace Refii.Launcher.Tools;

/// <summary>
/// Measures what completing Fable 2's short mip chains would actually cost.
///
/// Fable 2 stops its chains well above 1x1 and Godot derives every level down to 1x1, so
/// a short chain is rejected and the texture uploads unmipped. Fixing that means
/// synthesising the missing tail — and the question is whether that is affordable at
/// load, which is a measurement, not a judgement call.
///
/// The instinct is that generating mip levels is expensive because mip generation on a
/// full-size texture is. That is not what this does: the tail starts BELOW the smallest
/// level the file ships, so the largest image ever touched is the file's last level. What
/// this run establishes is whether the per-call overhead of Godot's compressor swamps
/// that, and whether runtime compression is available outside the editor at all.
/// </summary>
public partial class MipCost : Node
{
	/// <summary>Textures to time the tail generation on. 0 times every incomplete one.</summary>
	[Export] public int TimedSample { get; set; } = 400;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA or stage assets/game.");
			GetTree().Quit(1);
			return;
		}

		using BnkFileSystem filesystem = BnkFileSystem.Mount(dataRoot);
		List<BnkFile> textures = filesystem.EnumerateAllFiles()
			.Where(f => f.VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f.VirtualPath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		GD.Print($"[mip] {textures.Count:N0} .tex files");

		int complete = 0;
		int incomplete = 0;
		int raw = 0;
		int singleLevel = 0;
		int unparsed = 0;
		long baseBytes = 0;
		long tailBytes = 0;
		var missingHistogram = new SortedDictionary<int, int>();
		var candidates = new List<(byte[] Bytes, string Path)>();
		var singles = new List<(byte[] Bytes, string Path)>();

		foreach (BnkFile file in textures)
		{
			byte[] bytes;
			try
			{
				bytes = file.ReadAllBytes();
			}
			catch (Exception)
			{
				++unparsed;
				continue;
			}

			if (!TexHeader.TryParse(bytes, out TexHeader header, out _))
			{
				++unparsed;
				continue;
			}

			if (header.IsRaw)
			{
				++raw;
				continue;
			}

			int full = FullChainLength(header.Width, header.Height);
			int have = Math.Max(1, Math.Min(header.LevelCount, 12));

			if (have >= full)
			{
				++complete;
				continue;
			}

			++incomplete;
			if (have == 1)
			{
				++singleLevel;
			}

			int missing = full - have;
			missingHistogram.TryGetValue(missing, out int seen);
			missingHistogram[missing] = seen + 1;

			// Bytes in the levels the file HAS against bytes in the tail it omits. This is
			// the ratio that decides whether the tail is a rounding error or a real cost.
			baseBytes += ChainBytes(header.Width, header.Height, have, header.BlockStride);
			tailBytes += ChainBytes(header.Width, header.Height, full, header.BlockStride) -
				ChainBytes(header.Width, header.Height, have, header.BlockStride);

			// Two populations with costs orders of magnitude apart: a multi-level file needs
			// only the handful of levels below its smallest, while a single-level file has
			// its ENTIRE chain synthesised from a full-size base. Sampling them together and
			// quoting one average hides whichever is rarer — and taking the first N
			// alphabetically had sampled no single-level textures at all.
			List<(byte[] Bytes, string Path)> bucket = have == 1 ? singles : candidates;
			if (bucket.Count < Math.Max(TimedSample, 0) || TimedSample == 0)
			{
				bucket.Add((bytes, file.VirtualPath));
			}
		}

		GD.Print(new string('-', 70));
		GD.Print($"[mip] complete chains    {complete,7:N0}");
		GD.Print($"[mip] SHORT chains       {incomplete,7:N0}   <- these get a synthesised tail");
		GD.Print($"[mip] of those, SINGLE   {singleLevel,7:N0}   level only - the whole chain is synthesised");
		GD.Print($"[mip] raw/untiled        {raw,7:N0}");
		GD.Print($"[mip] unparsed           {unparsed,7:N0}");
		GD.Print($"[mip] shipped levels     {baseBytes / 1024.0 / 1024.0,7:F1} MB (short chains only)");
		GD.Print($"[mip] MISSING tail       {tailBytes / 1024.0,7:F1} KB" +
			(baseBytes > 0 ? $"   = {tailBytes * 100.0 / baseBytes:F2}% of those textures" : string.Empty));

		GD.Print("[mip] levels missing per texture:");
		foreach ((int missing, int count) in missingHistogram)
		{
			GD.Print($"[mip]   {missing,2} level(s) short : {count,6:N0} textures");
		}

		GD.Print(new string('-', 70));
		GD.Print("[mip] MULTI-LEVEL files (tail only)");
		TimeTailGeneration(candidates);

		GD.Print(new string('-', 70));
		GD.Print("[mip] SINGLE-LEVEL files (whole chain from the base)");
		TimeTailGeneration(singles);

		GetTree().Quit(0);
	}

	/// <summary>
	/// Times the real work: decompress the file's smallest level, then resize and compress
	/// each missing level below it.
	/// </summary>
	private void TimeTailGeneration(List<(byte[] Bytes, string Path)> candidates)
	{
		GD.Print(new string('-', 70));
		if (candidates.Count == 0)
		{
			GD.Print("[mip] nothing to time.");
			return;
		}

		int generated = 0;
		int failed = 0;
		int compressUnavailable = 0;
		int mismatched = 0;
		long producedBytes = 0;
		string firstFailure = string.Empty;

		// BASELINE: what loading these textures already costs today, with no tail work at
		// all. Without this the tail number is meaningless — a few ms per texture sounds
		// expensive until you see it sitting next to a decode that costs far more.
		var baseline = Stopwatch.StartNew();
		foreach ((byte[] bytes, string _) in candidates)
		{
			Engine_TexData.TryDecode(bytes, out _, out _);
		}

		baseline.Stop();

		var resizeWatch = new Stopwatch();
		var encodeWatch = new Stopwatch();

		var watch = Stopwatch.StartNew();
		foreach ((byte[] bytes, string path) in candidates)
		{
			if (!TexHeader.TryParse(bytes, out TexHeader header, out _))
			{
				++failed;
				continue;
			}

			int have = Math.Max(1, Math.Min(header.LevelCount, 12));

			// The SMALLEST level the file ships — the only one the tail needs. Decompressing
			// the base instead means inflating a 512x512 surface to generate an 8x8, which is
			// where the naive version of this spends all its time.
			int smallestWidth = header.Width;
			int smallestHeight = header.Height;
			for (int level = 1; level < have; ++level)
			{
				smallestWidth = Math.Max(1, smallestWidth / 2);
				smallestHeight = Math.Max(1, smallestHeight / 2);
			}

			int offset = header.LevelOffsets[have - 1];
			if (offset <= 0 || offset >= bytes.Length)
			{
				++failed;
				continue;
			}

			if (!Engine_TexPlanes.TryDecodeLevel(
					bytes, offset, header, smallestWidth, smallestHeight,
					out byte[] smallest, out _))
			{
				++failed;
				continue;
			}

			TexSurfaceFormat surfaceFormat = header.BlockStride == 16
				? (header.CodecId == 3 ? TexSurfaceFormat.Bc5 : TexSurfaceFormat.Bc3)
				: header.SurfaceFormat;
			if (surfaceFormat == TexSurfaceFormat.Unsupported)
			{
				++failed;
				continue;
			}

			Image? seed = Image.CreateFromData(
				smallestWidth, smallestHeight, false,
				Engine_TexImage.ToGodotFormat(surfaceFormat), smallest);
			if (seed is null || seed.GetWidth() != smallestWidth)
			{
				++failed;
				continue;
			}

			Error decompressed = seed.Decompress();
			if (decompressed != Error.Ok)
			{
				++failed;
				if (firstFailure.Length == 0)
				{
					firstFailure = $"{path}: decompress -> {decompressed}";
				}

				continue;
			}

			int w = Math.Max(1, smallestWidth / 2);
			int h = Math.Max(1, smallestHeight / 2);
			bool ok = true;
			Image current = seed;
			while (w > 1 || h > 1)
			{
				// Mirrors Engine_TexImage: each level halves the one above it.
				resizeWatch.Start();
				var tail = (Image)current.Duplicate();
				tail.Resize(w, h, Image.Interpolation.Lanczos);
				if (tail.GetFormat() != Image.Format.Rgba8)
				{
					tail.Convert(Image.Format.Rgba8);
				}

				current = tail;
				byte[] pixels = tail.GetData();
				resizeWatch.Stop();

				encodeWatch.Start();
				byte[]? encoded = Engine_BcEncoder.Encode(pixels, w, h, surfaceFormat);
				encodeWatch.Stop();
				if (encoded is null)
				{
					++compressUnavailable;
					ok = false;
					if (firstFailure.Length == 0)
					{
						firstFailure = $"{path}: encoder refused {w}x{h} {surfaceFormat}";
					}

					break;
				}

				// An encoder that returns something of the wrong length would make this whole
				// measurement a timing of no-ops. Check the block maths agrees.
				int expected = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) *
					(surfaceFormat is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8);
				int actual = encoded.Length;
				if (actual != expected)
				{
					++mismatched;
					if (firstFailure.Length == 0)
					{
						firstFailure = $"{path}: {w}x{h} {tail.GetFormat()} produced " +
							$"{actual} bytes, expected {expected}";
					}
				}
				else
				{
					producedBytes += actual;
				}

				w = Math.Max(1, w / 2);
				h = Math.Max(1, h / 2);
			}

			if (ok)
			{
				++generated;
			}
		}

		watch.Stop();

		int n = Math.Max(1, candidates.Count);
		double basePer = baseline.Elapsed.TotalMilliseconds / n;
		double tailPer = watch.Elapsed.TotalMilliseconds / n;

		GD.Print($"[mip] sample of {candidates.Count:N0} short-chain textures");
		GD.Print($"[mip] decode TODAY      {basePer,8:F3} ms each  " +
			$"({baseline.Elapsed.TotalSeconds:F2}s total)");
		GD.Print($"[mip] tail generation   {tailPer,8:F3} ms each  " +
			$"({watch.Elapsed.TotalSeconds:F2}s total)");
		GD.Print($"[mip] marginal cost     {tailPer * 100.0 / Math.Max(0.0001, basePer),8:F1}% " +
			"on top of the decode we already pay");
		GD.Print($"[mip]   of which resize+readback {resizeWatch.Elapsed.TotalMilliseconds / n,7:F3} ms, " +
			$"encode {encodeWatch.Elapsed.TotalMilliseconds / n:F3} ms");
		GD.Print($"[mip] generated {generated:N0}, failed {failed:N0}, " +
			$"encoder refused {compressUnavailable:N0}, WRONG SIZE {mismatched:N0}");
		GD.Print($"[mip] tail bytes produced {producedBytes:N0} " +
			$"({producedBytes / (double)Math.Max(1, generated):F1} per texture)");
		if (firstFailure.Length != 0)
		{
			GD.Print($"[mip] first failure: {firstFailure}");
		}
	}

	private static int FullChainLength(int width, int height)
	{
		int levels = 1;
		int w = width;
		int h = height;
		while (w > 1 || h > 1)
		{
			w = Math.Max(1, w / 2);
			h = Math.Max(1, h / 2);
			++levels;
		}

		return levels;
	}

	private static long ChainBytes(int width, int height, int levels, int blockStride)
	{
		long total = 0;
		int w = width;
		int h = height;
		for (int level = 0; level < levels; ++level)
		{
			total += (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockStride;
			w = Math.Max(1, w / 2);
			h = Math.Max(1, h / 2);
		}

		return total;
	}
}
