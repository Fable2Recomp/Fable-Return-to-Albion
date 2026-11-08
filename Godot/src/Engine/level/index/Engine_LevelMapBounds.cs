using System;
using System.IO;
using System.IO.Compression;

namespace Refii.Level;

/// <summary>
/// A LEVEL'S MAP BOUNDS, from its own heightfields: what the guest's level object holds at
/// <c>L+152..164</c> (minX, minY, maxX, maxY, whole metres) and the region maps divide a world
/// position by (<c>sub_829ABFC0</c>: <c>lwz r4,0x98 / r7,0x9C / r6,0xA0 / r8,0xA4</c> at 0x829AC898..8A4,
/// the larger span picked by <c>fsel</c> at 0x829AC8F8).
///
/// The guest fills them as it loads each <c>.ghf</c> the scenario's <c>.list</c> names
/// (<c>Level_LoadGhfListEntry</c> → <c>sub_82354780</c> → <c>sub_82355B58</c>): from the file's header,
/// <c>minX = (int)f32@0</c>, <c>minY = (int)f32@4</c> (<c>fctiwz</c> 0x8235486C / 0x82354854),
/// <c>maxX = minX + i32@0xC / 2</c>, <c>maxY = minY + i32@0x10 / 2</c> (signed halves, <c>srawi/addze</c>
/// 0x823547D0); the first heightfield sets the rectangle (stores 0x82355BA0..BB8) and every later one
/// widens it to the union (0x82355BC0..C10). Disc: the same sequence at 0x8234FC20.
/// </summary>
public static class Engine_LevelMapBounds
{
	/// <summary>The guest's rectangle, whole metres.</summary>
	public readonly record struct Bounds(int MinX, int MinY, int MaxX, int MaxY)
	{
		/// <summary>The region maps' divisor: the larger of the two spans.</summary>
		public float Extent => Math.Max(MaxX - MinX, MaxY - MinY);

		public Bounds Union(Bounds other) => new(
			Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
			Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
	}

	/// <summary>One <c>.ghf</c>'s rectangle from its 0x14-byte header (gzip or plain); false when it has none.</summary>
	public static bool TryRect(byte[] ghf, out Bounds rect)
	{
		rect = default;
		if (Head(ghf) is not { } head)
		{
			return false;
		}

		float originX = BitConverter.Int32BitsToSingle(unchecked((int)Word(head, 0x00)));
		float originY = BitConverter.Int32BitsToSingle(unchecked((int)Word(head, 0x04)));
		int width = unchecked((int)Word(head, 0x0C));
		int height = unchecked((int)Word(head, 0x10));
		if (!float.IsFinite(originX) || !float.IsFinite(originY) || Math.Abs(originX) > 1.0e7f || Math.Abs(originY) > 1.0e7f ||
			width < 2 || height < 2 || width > 8192 || height > 8192)
		{
			return false;
		}

		int minX = (int)originX;
		int minY = (int)originY;
		rect = new Bounds(minX, minY, minX + (width / 2), minY + (height / 2));
		return true;
	}

	/// <summary>The union over every <c>.ghf</c> line of a scenario's manifest; null when none reads.</summary>
	public static Bounds? Of(ScenarioManifest manifest, Func<string, byte[]?> read)
	{
		Bounds? union = null;
		foreach (string line in manifest.Ghf)
		{
			if (Engine_LevelManifest.TryResolvePath(line, read, out string path) &&
				read(path) is { } bytes && TryRect(bytes, out Bounds rect))
			{
				union = union is { } so ? so.Union(rect) : rect;
			}
		}

		return union;
	}

	private static byte[]? Head(byte[] ghf)
	{
		const int HeaderSize = 0x14;
		if (ghf is null || ghf.Length < 2)
		{
			return null;
		}

		if (!Refii.Core.Engine_GameGzip.IsGzip(ghf))
		{
			return ghf.Length >= HeaderSize ? ghf : null;
		}

		try
		{
			using var source = new MemoryStream(ghf);
			using var gzip = new GZipStream(source, CompressionMode.Decompress);
			var head = new byte[HeaderSize];
			int got = 0;
			while (got < HeaderSize)
			{
				int n = gzip.Read(head, got, HeaderSize - got);
				if (n <= 0)
				{
					return null;
				}

				got += n;
			}

			return head;
		}
		catch (InvalidDataException)
		{
			return null;
		}
	}

	private static uint Word(byte[] data, int at) =>
		((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];
}
