using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

using Godot;

namespace Refii.Titles.Fable2.PubGames;

/// <summary>A board's triangles cache (Engine_SwfAssets.SaveParts): each array read or written at once.</summary>
public static class Engine_SwfPartsIo
{
    /// <summary>Fills the array from the stream: the bytes <see cref="Write(Stream, Vector2[])"/> wrote.</summary>
    /// <exception cref="EndOfStreamException">The stream ends first.</exception>
    public static void Read(Stream stream, Vector2[] vectors) =>
        stream.ReadExactly(MemoryMarshal.AsBytes(vectors.AsSpan()));

    /// <summary>Writes each vector as two little-endian floats, all at once.</summary>
    public static void Write(Stream stream, Vector2[] vectors) =>
        stream.Write(MemoryMarshal.AsBytes(vectors.AsSpan()));

    /// <summary>Fills the array from four bytes a colour, as <see cref="Color.Color8"/> reads them.</summary>
    /// <exception cref="EndOfStreamException">The stream ends first.</exception>
    public static void Read(Stream stream, Color[] colours)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Max(colours.Length * 4, 1));
        try
        {
            stream.ReadExactly(bytes, 0, colours.Length * 4);
            for (int i = 0, b = 0; i < colours.Length; i++, b += 4)
            {
                colours[i] = Color.Color8(bytes[b], bytes[b + 1], bytes[b + 2], bytes[b + 3]);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>Writes each colour as its four eight-bit channels.</summary>
    public static void Write(Stream stream, Color[] colours)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Max(colours.Length * 4, 1));
        try
        {
            for (int i = 0, b = 0; i < colours.Length; i++, b += 4)
            {
                Color c = colours[i];
                bytes[b] = (byte)c.R8;
                bytes[b + 1] = (byte)c.G8;
                bytes[b + 2] = (byte)c.B8;
                bytes[b + 3] = (byte)c.A8;
            }

            stream.Write(bytes, 0, colours.Length * 4);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}
