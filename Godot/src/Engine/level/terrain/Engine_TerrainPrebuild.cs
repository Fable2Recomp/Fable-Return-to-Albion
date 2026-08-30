using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Refii.Bnk;
using Refii.Textures;

namespace Refii.Level;

/// <summary>
/// Warms the enhanced-texture cache for every terrain layer in the mount, behind the boot
/// warm-up screen — the ground's counterpart of <see cref="Engine_TexPrebuild.RunOrm"/>.
/// </summary>
/// <remarks>
/// The splat builder enhances its layers SYNCHRONOUSLY at level load (the texture arrays are
/// assembled once and cannot swap a texture in later), so a cold cache turns a region's first
/// load into a burst of neural upscales that reads as a freeze. This sweep walks every .ehf
/// palette in the banks and runs the same <see cref="Engine_TexLibrary.EnhanceDecoded"/> the
/// loader runs — the cache is content-addressed on the source bytes, so a warm entry here IS a
/// hit there, whatever the path spelling.
///
/// LIKE RunOrm, IT NEVER TOUCHES THE GLOBAL RESOLVER — it mounts and disposes its own bank
/// filesystem.
/// </remarks>
public static class Engine_TerrainPrebuild
{
	public readonly record struct Progress(int Done, int Total, int Built, int Failed, string Current);

	private static readonly object Gate = new();

	public static Progress Run(
		string gameDirectory,
		Action<Progress>? report = null,
		CancellationToken cancel = default)
	{
		if (gameDirectory.Length == 0 || !System.IO.Directory.Exists(gameDirectory))
		{
			return new Progress(0, 0, 0, 0, "no game data");
		}

		if (!Engine_TexEnhance.Enabled)
		{
			return new Progress(0, 0, 0, 0, "enhancement off");
		}

		// "play" and "off" both skip the sweep — one defers the cost to level loads, the
		// other never spends it at all ("off" is already caught by Enabled above).
		if (Engine_TexSettings.UpscaleWhen != "boot")
		{
			return new Progress(0, 0, 0, 0, $"upscaling deferred: {Engine_TexSettings.UpscaleWhen}");
		}

		using BnkFileSystem banks = BnkFileSystem.Mount(gameDirectory);
		byte[]? Read(string path) =>
			banks.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

		// Every .ehf palette in the mount, not one level's: the sweep pays once for the whole
		// game. Path → is-a-normal-map, deduplicated across levels that share layers.
		var unique = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		foreach (BnkFile file in banks.EnumerateAllFiles())
		{
			if (!file.VirtualPath.EndsWith(".ehf", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			byte[]? ehf;
			try
			{
				ehf = file.ReadAllBytes();
			}
			catch (Exception)
			{
				continue;
			}

			if (ehf is null || !Engine_EhfBody.TryParse(ehf, out EhfParsedBody parsed, out _))
			{
				continue;
			}

			foreach (EhfLodEntry lod in parsed.Lods)
			{
				AddPath(unique, lod.BaseDiffuse, false);
				AddPath(unique, lod.DetailDiffuse, false);
				AddPath(unique, lod.BaseNormal, true);
				AddPath(unique, lod.DetailNormal, true);
			}
		}

		var work = new List<KeyValuePair<string, bool>>(unique);
		int total = work.Count;
		int done = 0;
		int built = 0;
		int failed = 0;
		string current = string.Empty;

		var options = new System.Threading.Tasks.ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Engine_TexSettings.Workers),
			CancellationToken = CancellationToken.None,
		};

		System.Threading.Tasks.Parallel.ForEach(work, options, item =>
		{
			if (cancel.IsCancellationRequested)
			{
				return;
			}

			// Built = an enhanced artefact now sits in the cache. A texture the policy declines
			// (already at target size, no backend for its class) is DONE, not FAILED — only an
			// unreadable or undecodable source counts as a failure.
			bool read = false;
			bool enhanced = false;
			try
			{
				byte[]? bytes = Read(item.Key) ?? Read("data/" + item.Key);
				if (bytes is not null &&
					Engine_TexImage.TryCreateImage(bytes, out bool hasAlpha, out _) is Image image)
				{
					read = true;
					enhanced = Engine_TexLibrary.EnhanceDecoded(
						bytes, image,
						item.Value ? TexSemantic.Normal : TexSemantic.Albedo,
						hasAlpha, item.Key) is not null;
				}
			}
			catch (Exception)
			{
				// One bad asset must not end the sweep.
			}

			lock (Gate)
			{
				++done;
				if (enhanced)
				{
					++built;
				}
				else if (!read)
				{
					++failed;
				}

				current = item.Key;
			}

			if (report is not null && done % 8 == 0)
			{
				report(Snapshot(done, total, built, failed, current));
			}
		});

		Progress final = Snapshot(done, total, built, failed, current);
		report?.Invoke(final);
		return final;
	}

	private static void AddPath(Dictionary<string, bool> unique, string path, bool normalMap)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		unique.TryAdd(path.Replace('\\', '/').Trim('/'), normalMap);
	}

	private static Progress Snapshot(int done, int total, int built, int failed, string current)
	{
		lock (Gate)
		{
			return new Progress(done, total, built, failed, current);
		}
	}
}
