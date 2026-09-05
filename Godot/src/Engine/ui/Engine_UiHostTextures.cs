using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Textures the HOST supplies to an owned book, keyed like the guest's — by a hash the book's
/// texture channel carries. The guest's hashes are FNV-1 of asset paths; these sit in a range
/// no path hashes to, so a host texture and a shipped one can never collide on the channel.
/// </summary>
public static class Engine_UiHostTextures
{
	private const uint FirstKey = 0xFFFF0000u;

	private static readonly Dictionary<Texture2D, uint> Keys = new();
	private static readonly Dictionary<uint, Texture2D> Textures = new();
	private static uint _next = FirstKey;

	/// <summary>The key for a texture, minted on first sight.</summary>
	public static uint Register(Texture2D texture)
	{
		if (Keys.TryGetValue(texture, out uint key))
		{
			return key;
		}

		key = _next++;
		Keys[texture] = key;
		Textures[key] = texture;
		return key;
	}

	/// <summary>The texture behind a key, or null for any key not minted here.</summary>
	public static Texture2D? TryGet(uint key) =>
		key >= FirstKey && Textures.TryGetValue(key, out Texture2D? texture) ? texture : null;
}
