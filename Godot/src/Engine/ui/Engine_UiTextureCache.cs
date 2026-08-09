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
	public Engine_UiTextureCache(BnkFileSystem banks)
	{
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
			// The loader has already said why, in its own warning.
			_failures[relativePath] = "not loaded";
			return null;
		}

		++Loaded;
		return texture;
	}
}
