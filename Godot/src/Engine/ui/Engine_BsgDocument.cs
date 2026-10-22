using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// One scene-graph node. The record is 96 bytes; the fields decoded here are the
/// ones confirmed against shipped data. The rest is deliberately left as
/// <see cref="Raw"/> rather than guessed at.
/// </summary>
public sealed class BsgSceneNode
{
	/// <summary>Index of the parent node, or -1 for a root.</summary>
	public int ParentIndex;

	/// <summary>Matches a <see cref="BgfElement.ObjectId"/>.</summary>
	public uint ObjectId;

	/// <summary>
	/// Object class, dispatched by <c>switch</c> at <c>sub_822D83A0</c>+0x74. Verified
	/// against the shipped set (26,153 nodes), which is what settles 4 and 5 — the
	/// bucket a light is pushed to (<c>204*layer + base + 12</c>) reads like a layer
	/// list, and text always routes to the translucent pass, so both are easy to
	/// mislabel from the code alone:
	/// <list type="bullet">
	/// <item>1 — transform/group only, no geometry (8,731: every Component, and Nodes
	///   that only carry children)</item>
	/// <item>2 — renderable geometry (15,838 Nodes)</item>
	/// <item>3 — camera (59, one per Camera element)</item>
	/// <item>4 — light (56, one per Light element)</item>
	/// <item>5 — text (1,417, one per Text element; always drawn translucent)</item>
	/// <item>8 — layer carrier (exactly 52 — one per screen)</item>
	/// </list>
	/// 6 and 7 belong to the 12-byte asset records, never to a scene node.
	///
	/// This is NOT <see cref="BgfElementType"/> + 1: Camera/Light/Text line up that way
	/// by coincidence, but Component and Node both map to 1, and Node also takes 2 and 8.
	/// </summary>
	public byte NodeType;

	/// <summary>
	/// Base local transform. The <c>.bgf</c> overrides any component it carries an
	/// attribute for; these are the values used when it does not.
	/// </summary>
	public float PositionX, PositionY, PositionZ;

	/// <summary>Euler angles in RADIANS — unlike the <c>.bgf</c>, which authors degrees.</summary>
	public float RotationX, RotationY, RotationZ;

	public float ScaleX = 1, ScaleY = 1, ScaleZ = 1;

	/// <summary>
	/// Always zero in every shipped file — the exporter bakes the pivot into
	/// <see cref="PositionX"/> and ships this zeroed. Read faithfully anyway, because
	/// the runtime does apply it (<c>sub_8229E4B8</c>).
	/// </summary>
	public float PivotX, PivotY, PivotZ;

	/// <summary>Base opacity, 0..1. The <c>.bgf</c>'s <c>opacity</c> attribute is 0..100.</summary>
	public float Opacity = 1f;

	/// <summary>Render layer, 0-7; 255 means none (the root carries 255).</summary>
	public byte LayerIndex;

	/// <summary>Euler application order, 0-11. Always 4 in shipped data.</summary>
	public byte RotationOrder;

	/// <summary>Z-handedness flag; set on records 0 and 1 of every file.</summary>
	public byte Orientation;

	/// <summary>Forces the translucent draw bucket regardless of opacity.</summary>
	public bool ForceTranslucent;

	/// <summary>
	/// Local bounding box. Ships inverted — min (1,1,1) and max (-1,-1,-1) — as the
	/// engine's "empty" sentinel, which scripts branch on. An empty box therefore
	/// reports dimensions of (-2,-2,-2); do not normalise it to zero.
	/// </summary>
	public float MinX, MinY, MinZ;
	public float MaxX, MaxY, MaxZ;

	public bool IsEmptyBounds => MaxX < MinX;

	/// <summary>The full 96-byte record, for fields not yet attributed.</summary>
	public byte[] Raw = Array.Empty<byte>();
}

/// <summary>
/// Binds a presentation element to a concrete engine resource.
/// <see cref="ObjectId"/> matches a <see cref="BgfElement.ObjectId"/>.
/// </summary>
public readonly struct BsgAsset
{
	public readonly uint ObjectId;

	/// <summary>Asset kind. 6 pairs with a <c>.bgf</c> material, 7 with a texture.</summary>
	public readonly byte Type;

	public BsgAsset(uint objectId, byte type)
	{
		ObjectId = objectId;
		Type = type;
	}
}

/// <summary>
/// Reader for the <c>.bsg</c> scene graph that ships beside every <c>.bgf</c>.
///
/// Layout mirrors <see cref="Engine_BgfDocument"/>: a header carrying the endian flag and
/// an <c>AnarkBGF</c> magic, then size-prefixed sections — Memory (two counts),
/// Scene Graph (96-byte nodes) and Asset (5-byte <c>{objectId, type}</c> records).
///
/// All 52 shipped files parse exactly to EOF, and write back byte-identical through
/// <see cref="Engine_BsgWriter"/>. Note the file contains no strings:
/// texture names come from the presentation's own path attributes, or better, from
/// hashing the extracted texture bank — see <see cref="Engine_TextureNameTable"/>.
/// </summary>
public sealed class Engine_BsgDocument
{
	public const string MagicText = "AnarkBGF";
	public const string MagicTextReversed = "FGBkranA";
	public const int MagicOffset = 8;
	public const int VersionOffset = 0x10;
	public const int SectionsStart = 0x20;
	public const int SceneNodeSize = 96;
	public const int AssetRecordSize = 5;

	public bool BigEndian;
	public int Version;

	/// <summary>
	/// The 32-byte header as read; the endian byte, magic and version are decoded from it and
	/// written back over it by <see cref="Engine_BsgWriter"/>, the rest re-emitted as it was.
	/// </summary>
	public byte[] Header = new byte[SectionsStart];

	/// <summary>The Memory section's words after the two counts (none in any shipped file), carried for the writer.</summary>
	public uint[] MemoryExtra = Array.Empty<uint>();

	public BsgSceneNode[] Nodes = Array.Empty<BsgSceneNode>();
	public BsgAsset[] Assets = Array.Empty<BsgAsset>();

	public static Engine_BsgDocument Load(string path) => Parse(File.ReadAllBytes(path));

	public static Engine_BsgDocument Parse(byte[] d)
	{
		var doc = new Engine_BsgDocument { BigEndian = d[0] == 1 };

		// Big-endian .bsg stores the magic byte-reversed, as "FGBkranA" — it is written
		// as a byte-swapped 64-bit word. Note .bgf does NOT do this: its magic reads the
		// same either way. Accept both spellings rather than assuming from the endian flag.
		string magic = Encoding.ASCII.GetString(d, MagicOffset, 8);
		if (magic != MagicText && magic != MagicTextReversed)
			throw new InvalidDataException($"bsg: bad magic '{magic}', expected {MagicText} or {MagicTextReversed}");

		int p = VersionOffset;
		doc.Version = checked((int)ReadU32(d, ref p, doc.BigEndian));
		if (doc.Version != 2)
			throw new InvalidDataException($"bsg: unsupported version {doc.Version}");

		doc.Header = d.AsSpan(0, SectionsStart).ToArray();
		p = SectionsStart;

		// 1. Memory — two counts.
		int memSize = checked((int)ReadU32(d, ref p, doc.BigEndian));
		int memEnd = p + memSize;
		int nodeCount = checked((int)ReadU32(d, ref p, doc.BigEndian));
		int assetCount = checked((int)ReadU32(d, ref p, doc.BigEndian));
		doc.MemoryExtra = new uint[Math.Max(0, (memEnd - p) / 4)];
		for (int i = 0; i < doc.MemoryExtra.Length; i++)
			doc.MemoryExtra[i] = ReadU32(d, ref p, doc.BigEndian);
		p = memEnd;

		// 2. Scene graph.
		int sgSize = checked((int)ReadU32(d, ref p, doc.BigEndian));
		if (sgSize != nodeCount * SceneNodeSize)
			throw new InvalidDataException(
				$"bsg: scene graph is {sgSize} bytes, expected {nodeCount} x {SceneNodeSize}");

		doc.Nodes = new BsgSceneNode[nodeCount];
		for (int i = 0; i < nodeCount; i++)
		{
			int b = p + i * SceneNodeSize;
			// Layout is code-proven from the loader sub_82BCB4E8, which is a pure
			// sequential unpacker — every field is a verbatim Read with no arithmetic,
			// so the file offsets are just the running sum of the read sizes. Bytes
			// +9..+11 are padding the loader reads into a stack slot and discards, and
			// +91..+94 is a field with no known reader (always zero in shipped data).
			bool be = doc.BigEndian;
			var n = new BsgSceneNode
			{
				ParentIndex = unchecked((int)ReadU32At(d, b, be)),
				ObjectId = ReadU32At(d, b + 4, be),
				NodeType = d[b + 8],

				PositionX = ReadF32At(d, b + 12, be),
				PositionY = ReadF32At(d, b + 16, be),
				PositionZ = ReadF32At(d, b + 20, be),

				RotationX = ReadF32At(d, b + 24, be),
				RotationY = ReadF32At(d, b + 28, be),
				RotationZ = ReadF32At(d, b + 32, be),

				ScaleX = ReadF32At(d, b + 36, be),
				ScaleY = ReadF32At(d, b + 40, be),
				ScaleZ = ReadF32At(d, b + 44, be),

				PivotX = ReadF32At(d, b + 48, be),
				PivotY = ReadF32At(d, b + 52, be),
				PivotZ = ReadF32At(d, b + 56, be),

				MinX = ReadF32At(d, b + 60, be),
				MinY = ReadF32At(d, b + 64, be),
				MinZ = ReadF32At(d, b + 68, be),
				MaxX = ReadF32At(d, b + 72, be),
				MaxY = ReadF32At(d, b + 76, be),
				MaxZ = ReadF32At(d, b + 80, be),

				Opacity = ReadF32At(d, b + 84, be),
				LayerIndex = d[b + 88],
				RotationOrder = d[b + 89],
				Orientation = d[b + 90],
				ForceTranslucent = d[b + 95] != 0,

				Raw = d.AsSpan(b, SceneNodeSize).ToArray(),
			};
			doc.Nodes[i] = n;
		}
		p += sgSize;

		// 3. Assets.
		int asSize = checked((int)ReadU32(d, ref p, doc.BigEndian));
		if (asSize != assetCount * AssetRecordSize)
			throw new InvalidDataException(
				$"bsg: asset section is {asSize} bytes, expected {assetCount} x {AssetRecordSize}");

		doc.Assets = new BsgAsset[assetCount];
		for (int i = 0; i < assetCount; i++)
		{
			int b = p + i * AssetRecordSize;
			doc.Assets[i] = new BsgAsset(ReadU32At(d, b, doc.BigEndian), d[b + 4]);
		}
		p += asSize;

		if (p != d.Length)
			throw new InvalidDataException($"bsg: {d.Length - p} trailing bytes, expected 0");

		return doc;
	}

	private static uint ReadU32(byte[] d, ref int p, bool be)
	{
		uint v = ReadU32At(d, p, be);
		p += 4;
		return v;
	}

	private static uint ReadU32At(byte[] d, int p, bool be)
	{
		var s = d.AsSpan(p, 4);
		return be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
	}

	private static float ReadF32At(byte[] d, int p, bool be)
	{
		var s = d.AsSpan(p, 4);
		return be ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
	}
}
