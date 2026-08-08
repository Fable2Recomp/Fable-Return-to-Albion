using System;
using System.Collections.Generic;
using Godot;
using Refii.Bnk;
using Refii.Textures;

namespace Refii.Launcher.Formats;

/// <summary>
/// Serves UI textures by engine path, caching by path.
///
/// This is a thin adapter, not a decoder: the bytes come from
/// <see cref="BnkFileSystem"/> and the decode is
/// <see cref="Engine_TexImage.TryCreateTexture"/>. The UI stack used to carry its own
/// bank reader and <c>.tex</c> decoder; those were superseded by the engine ones and
/// deleted, and this exists only so the UI keeps a by-path lookup with a cache in front
/// of it.
///
/// A <c>.tex</c> reaches the engine loader as ONE byte range — the bank filesystem
/// already stitches the header and payload entries that the format splits across two
/// banks — so nothing here pairs them.
/// </summary>
public sealed class UiTextureCache
{
	private readonly BnkFileSystem _banks;
	private readonly Dictionary<string, Texture2D?> _cache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Textures that could not be produced, with the reason. Useful for a conversion report.</summary>
	public readonly Dictionary<string, string> Failures = new(StringComparer.OrdinalIgnoreCase);

	public int Loaded { get; private set; }

	public UiTextureCache(BnkFileSystem banks) => _banks = banks;

	/// <summary>
	/// Resolves a path such as <c>art\gui\frames\frames_04.tex</c>, or null when the
	/// asset is missing or its codec is not supported; the reason lands in
	/// <see cref="Failures"/>.
	/// </summary>
	public Texture2D? Load(string relativePath)
	{
		if (relativePath.Length == 0)
			return null;
		if (_cache.TryGetValue(relativePath, out Texture2D? cached))
			return cached;

		Texture2D? texture = LoadUncached(relativePath, out string error);
		_cache[relativePath] = texture;

		if (texture is null && error.Length > 0)
			Failures[relativePath] = error;
		else if (texture is not null)
			Loaded++;

		return texture;
	}

	private Texture2D? LoadUncached(string relativePath, out string error)
	{
		error = string.Empty;

		if (!_banks.TryGetFile(relativePath, out BnkFile file))
		{
			error = "not present in any mounted bank";
			return null;
		}

		byte[] bytes;
		try
		{
			bytes = file.ReadAllBytes();
		}
		catch (Exception exception)
		{
			error = "read: " + exception.Message;
			return null;
		}

		ImageTexture? texture = Engine_TexImage.TryCreateTexture(bytes, out string decodeError);
		if (texture is null)
			error = decodeError.Length > 0 ? decodeError : "decode failed";

		return texture;
	}
}
