using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_EnvironmentTextures
{
    public const uint NullHash = 0x811C9DC5u;

    private static Func<IEnumerable<string>>? _texturePaths;

    public static void SetTexturePathSource(Func<IEnumerable<string>>? source) =>
        _texturePaths = source;

    public static uint Hash(string value, bool lowercase)
    {
        if (lowercase)
        {
            value = value.ToLowerInvariant();
        }

        uint hash = NullHash;
        foreach (char c in value)
        {
            byte b = (byte)(c == '/' ? '\\' : c);
            hash *= 0x01000193u;
            hash ^= b;
        }

        return hash;
    }

    public static string? ResolvePath(uint hash)
    {
        if (hash == 0 || hash == NullHash || _texturePaths is null)
        {
            return null;
        }

        foreach (string path in _texturePaths())
        {
            int slash = path.LastIndexOf('/');
            string leaf = slash < 0 ? path : path[(slash + 1)..];

            if (Hash(path, true) == hash ||
                Hash(leaf, true) == hash ||
                Hash(path, false) == hash ||
                Hash(leaf, false) == hash)
            {
                return path;
            }
        }

        return null;
    }

    public static Texture2D? Load(uint hash, Func<string, byte[]?> read, out string path)
    {
        path = string.Empty;

        string? resolved = ResolvePath(hash);
        if (resolved is null)
        {
            return null;
        }

        path = resolved;

        byte[]? bytes = read(resolved);
        if (bytes is null)
        {
            return null;
        }

        return Engine_TexImage.TryCreateTexture(bytes, out _);
    }
}
