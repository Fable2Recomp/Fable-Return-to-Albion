using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Ui;

/// <summary>
/// Writes a <c>.bsg</c> scene graph — the exact inverse of <see cref="Engine_BsgDocument.Parse"/>.
///
/// The file as emitted, in the document's endianness (byte 0):
/// <code>
/// [0, 32)   header: endian byte, magic at 8 ("AnarkBGF", byte-reversed to "FGBkranA" in a
///           big-endian file, as shipped), u32 version at 16; the other bytes are the
///           document's Header as read (a new document carries zeros)
/// Memory    u32 size, u32 nodeCount, u32 assetCount, then any extra words carried
/// Scene     u32 size (= 96 × nodes); per node the 96-byte record: u32 parent, u32 objectId,
///           u8 type, [9..12) carried, 18 floats (position, rotation, scale, pivot, min, max)
///           at 12..84, f32 opacity, u8 layer, u8 rotationOrder, u8 orientation, [91..95)
///           carried, u8 forceTranslucent
/// Asset     u32 size (= 5 × assets); per asset u32 objectId, u8 type
/// </code>
/// A node is emitted from its decoded fields over its <see cref="BsgSceneNode.Raw"/> bytes, so
/// the bytes the reader does not attribute (9..12, 91..95) come back as read; a node made in
/// memory with no Raw carries zeros there. <see cref="TryWrite"/> parses what it wrote and
/// refuses bytes the shipping reader does not read as the same document.
/// </summary>
public static class Engine_BsgWriter
{
	public static byte[] Write(Engine_BsgDocument doc)
	{
		if (!TryWrite(doc, out byte[] bytes, out string error))
		{
			throw new InvalidDataException("bsg write: " + error);
		}

		return bytes;
	}

	public static bool TryWrite(Engine_BsgDocument doc, out byte[] bytes, out string error)
	{
		bytes = Emit(doc);
		Engine_BsgDocument back;
		try
		{
			back = Engine_BsgDocument.Parse(bytes);
		}
		catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
		{
			error = "the written bytes do not parse: " + e.Message;
			bytes = Array.Empty<byte>();
			return false;
		}

		if (!Same(doc, back, out string difference))
		{
			error = "the written bytes parse to a different document: " + difference;
			bytes = Array.Empty<byte>();
			return false;
		}

		error = string.Empty;
		return true;
	}

	private static byte[] Emit(Engine_BsgDocument doc)
	{
		bool be = doc.BigEndian;
		var header = new byte[Engine_BsgDocument.SectionsStart];
		Array.Copy(doc.Header, header, Math.Min(doc.Header.Length, header.Length));
		header[0] = (byte)(be ? 1 : 0);
		Encoding.ASCII.GetBytes(be ? Engine_BsgDocument.MagicTextReversed : Engine_BsgDocument.MagicText, 0, 8, header, Engine_BsgDocument.MagicOffset);
		PutU32(header, Engine_BsgDocument.VersionOffset, unchecked((uint)doc.Version), be);

		int memBytes = 8 + (4 * doc.MemoryExtra.Length);
		int sceneBytes = Engine_BsgDocument.SceneNodeSize * doc.Nodes.Length;
		int assetBytes = Engine_BsgDocument.AssetRecordSize * doc.Assets.Length;
		var d = new byte[header.Length + 4 + memBytes + 4 + sceneBytes + 4 + assetBytes];
		Array.Copy(header, d, header.Length);
		int p = header.Length;

		PutU32(d, p, (uint)memBytes, be); p += 4;
		PutU32(d, p, (uint)doc.Nodes.Length, be); p += 4;
		PutU32(d, p, (uint)doc.Assets.Length, be); p += 4;
		foreach (uint extra in doc.MemoryExtra)
		{
			PutU32(d, p, extra, be); p += 4;
		}

		PutU32(d, p, (uint)sceneBytes, be); p += 4;
		foreach (BsgSceneNode n in doc.Nodes)
		{
			EmitNode(d, p, n, be);
			p += Engine_BsgDocument.SceneNodeSize;
		}

		PutU32(d, p, (uint)assetBytes, be); p += 4;
		foreach (BsgAsset a in doc.Assets)
		{
			PutU32(d, p, a.ObjectId, be);
			d[p + 4] = a.Type;
			p += Engine_BsgDocument.AssetRecordSize;
		}

		return d;
	}

	/// <summary>The 96-byte record: the carried bytes from Raw, every attributed field from the node.</summary>
	private static void EmitNode(byte[] d, int b, BsgSceneNode n, bool be)
	{
		if (n.Raw.Length == Engine_BsgDocument.SceneNodeSize)
		{
			Array.Copy(n.Raw, 0, d, b, Engine_BsgDocument.SceneNodeSize);
		}

		PutU32(d, b, unchecked((uint)n.ParentIndex), be);
		PutU32(d, b + 4, n.ObjectId, be);
		d[b + 8] = n.NodeType;
		float[] floats =
		{
			n.PositionX, n.PositionY, n.PositionZ, n.RotationX, n.RotationY, n.RotationZ,
			n.ScaleX, n.ScaleY, n.ScaleZ, n.PivotX, n.PivotY, n.PivotZ,
			n.MinX, n.MinY, n.MinZ, n.MaxX, n.MaxY, n.MaxZ, n.Opacity,
		};
		for (int i = 0; i < floats.Length; i++)
		{
			PutU32(d, b + 12 + (4 * i), BitConverter.SingleToUInt32Bits(floats[i]), be);
		}

		d[b + 88] = n.LayerIndex;
		d[b + 89] = n.RotationOrder;
		d[b + 90] = n.Orientation;
		// The flag byte is read as non-zero; a carried non-zero value stands when it agrees.
		if (n.Raw.Length != Engine_BsgDocument.SceneNodeSize || (n.Raw[95] != 0) != n.ForceTranslucent)
		{
			d[b + 95] = (byte)(n.ForceTranslucent ? 1 : 0);
		}
	}

	/// <summary>Field-for-field equality of two parsed documents, floats by their bits; the first difference named.</summary>
	public static bool Same(Engine_BsgDocument a, Engine_BsgDocument b, out string difference)
	{
		difference = string.Empty;
		if (a.BigEndian != b.BigEndian) return Differs(out difference, "endian");
		if (a.Version != b.Version) return Differs(out difference, "version");
		if (!SameReservedHeader(a.Header, b.Header)) return Differs(out difference, "header reserved bytes");
		if (!a.MemoryExtra.SequenceEqual(b.MemoryExtra)) return Differs(out difference, "memory extra words");
		if (a.Nodes.Length != b.Nodes.Length) return Differs(out difference, $"node count {a.Nodes.Length} vs {b.Nodes.Length}");
		for (int i = 0; i < a.Nodes.Length; i++)
		{
			if (!SameNode(a.Nodes[i], b.Nodes[i])) return Differs(out difference, $"node {i}");
		}

		if (a.Assets.Length != b.Assets.Length) return Differs(out difference, "asset count");
		for (int i = 0; i < a.Assets.Length; i++)
		{
			if (a.Assets[i].ObjectId != b.Assets[i].ObjectId || a.Assets[i].Type != b.Assets[i].Type)
				return Differs(out difference, $"asset {i}");
		}

		return true;
	}

	private static bool SameNode(BsgSceneNode x, BsgSceneNode y)
	{
		if (x.ParentIndex != y.ParentIndex || x.ObjectId != y.ObjectId || x.NodeType != y.NodeType ||
			x.LayerIndex != y.LayerIndex || x.RotationOrder != y.RotationOrder || x.Orientation != y.Orientation ||
			x.ForceTranslucent != y.ForceTranslucent)
		{
			return false;
		}

		float[] fx = Floats(x), fy = Floats(y);
		for (int i = 0; i < fx.Length; i++)
		{
			if (BitConverter.SingleToUInt32Bits(fx[i]) != BitConverter.SingleToUInt32Bits(fy[i])) return false;
		}

		// The carried bytes, where both records have them.
		if (x.Raw.Length == Engine_BsgDocument.SceneNodeSize && y.Raw.Length == Engine_BsgDocument.SceneNodeSize)
		{
			for (int i = 9; i < 12; i++) if (x.Raw[i] != y.Raw[i]) return false;
			for (int i = 91; i < 95; i++) if (x.Raw[i] != y.Raw[i]) return false;
		}

		return true;
	}

	private static float[] Floats(BsgSceneNode n) => new[]
	{
		n.PositionX, n.PositionY, n.PositionZ, n.RotationX, n.RotationY, n.RotationZ,
		n.ScaleX, n.ScaleY, n.ScaleZ, n.PivotX, n.PivotY, n.PivotZ,
		n.MinX, n.MinY, n.MinZ, n.MaxX, n.MaxY, n.MaxZ, n.Opacity,
	};

	/// <summary>The header bytes the parse does not decode — [1, 8), [20, 32) — compared as carried.</summary>
	private static bool SameReservedHeader(byte[] a, byte[] b)
	{
		foreach ((int from, int to) in new[] { (1, Engine_BsgDocument.MagicOffset), (Engine_BsgDocument.VersionOffset + 4, Engine_BsgDocument.SectionsStart) })
		{
			for (int i = from; i < to; i++)
			{
				byte x = i < a.Length ? a[i] : (byte)0;
				byte y = i < b.Length ? b[i] : (byte)0;
				if (x != y) return false;
			}
		}

		return true;
	}

	private static bool Differs(out string difference, string what)
	{
		difference = what;
		return false;
	}

	private static void PutU32(byte[] d, int at, uint v, bool be)
	{
		if (be) BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at, 4), v);
		else BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at, 4), v);
	}
}
