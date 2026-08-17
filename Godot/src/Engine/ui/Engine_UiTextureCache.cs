using System;
using System.Collections.Generic;
using Godot;
using Refii.Bnk;
using Refii.Textures;

using Refii.Core;

using Refii.Text;

namespace Refii.Ui;

/// <summary>
/// Serves UI textures by engine path, through the one loader everything else uses.
///
/// It owns no reading, no decoding and no cache of its own: it points
/// <see cref="Engine_BnkTextureLoader"/> at the banks it was given and then asks for
/// <c>game://…</c>, which is exactly what the editor's asset browser and the running game
/// do. Godot's own resource cache does the caching.
///
/// That matters beyond tidiness. This class used to read the bank and call the decoder
/// itself, so a UI texture reached the screen by a different route than the same file
/// opened in the editor — two paths to keep in agreement, and a place for them to drift.
/// The decode was already shared; this makes the routing shared too.
///
/// A <c>.tex</c> arrives as ONE byte range: the bank filesystem stitches the header and
/// payload entries the format splits across two banks, so nothing here pairs them.
/// </summary>
public sealed class Engine_UiTextureCache
{
	private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Paths that could not be loaded, and why.</summary>
	public IReadOnlyDictionary<string, string> Failures => _failures;

	/// <summary>How many distinct textures resolved.</summary>
	public int Loaded { get; private set; }

	/// <summary>
	/// Points the shared loader at these banks.
	///
	/// The resolver is process-wide because the loader is registered with Godot's
	/// ResourceLoader, which is process-wide. Callers that mount their own banks — the
	/// screen viewer, the scene exporter — set it here; the running game sets the same
	/// thing from Engine_GameAssets over the same mount.
	/// </summary>
	private readonly BnkFileSystem _banks;

	public Engine_UiTextureCache(BnkFileSystem banks)
	{
		_banks = banks;
		Engine_BnkTextureLoader.EnsureRegistered();
		Engine_BnkTextureLoader.SetResolver(
			path => banks.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null);
	}

	/// <summary>
	/// Resolves a path such as <c>art\gui\frames\frames_04.tex</c>, or null when the
	/// asset is missing or its codec is not supported; the reason lands in
	/// <see cref="Failures"/>.
	/// </summary>
	public Texture2D? Load(string relativePath)
	{
		if (relativePath.Length == 0)
		{
			return null;
		}

		// Separators are normalised because the same texture is named with backslashes in
		// the .fac sidecar and forward slashes in the bank index; a cache keyed on the raw
		// string would hold both and a loader keyed on one would miss the other.
		string virtualPath = relativePath.Replace('\\', '/');

		var texture = ResourceLoader.Load<Texture2D>(
			Engine_BnkTextureLoader.PathPrefix + virtualPath);
		if (texture is null)
		{
			// Says which side failed. ResourceLoader answers a miss with a bare "Cannot open
			// file" whether our loader was consulted or not, so record what the bank itself
			// thinks of the same path — a hit here means the asset is present and the loader
			// route is the fault, a miss means the path never resolves.
			_failures[relativePath] = _banks.TryGetFile(virtualPath, out BnkFile probe)
				? $"bank has it ({probe.Size} bytes) but the loader returned null"
				: "not in the bank under this path";
			return null;
		}

		++Loaded;
		return texture;
	}
}
