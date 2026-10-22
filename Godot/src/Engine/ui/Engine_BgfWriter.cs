using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Writes a Gameface <c>.bgf</c> presentation — the exact inverse of <see cref="Engine_BgfDocument.Parse"/>.
///
/// The file as emitted, in the parser's order and the document's endianness (byte 0 of the header):
/// <code>
/// [0, 32)   header: endian byte, magic "AnarkBGF" at 6, u32 version at 14, u16 canvas w/h at 22;
///           the bytes between are the document's Header as read (a new document carries zeros)
/// then eight size-prefixed sections, each `u32 byteSize` then its bytes:
///   Memory    19 u32 counts, then the extra words (shipped files carry six)
///   String    per string `u32 length` + UTF-8 bytes, no terminator
///   Element   per element `u32 nameHash, u32 parent, u32 objectId, u8 type, u8 flags`
///             [+ `u8 pad, u32 extra` on a component], `u32 attrCount`, attrs as 8-byte records
///   Contract  per contract `u32 element, u32 nameCount`, u32 string indices
///   Animation per track `u32 element, u32 attrId, u8 activeOnLoad, u8 dynamic, u16 keyCount`,
///             then its keys as six 4-byte fields (times as u32 integers, values as float bits)
///   Logic     carried as bytes
///   Params    carried as bytes
///   Slide     per slide 16-byte NUL-padded name, `u32 owner, u8 flags, u32 start, u32 end,
///             u32 entryCount`; per entry `u32 element, u8 active, u32 cmdCount` + 8-byte records,
///             `u32 eventCount` + (u16 flag, u16 index) pairs
/// </code>
/// An attribute record is `u32 packed` (27-bit id | type &lt;&lt; 27 | high &lt;&lt; 30) then `u32 value`.
///
/// EVERYTHING IS CANONICAL: no field has slack the parser tolerates, so a parse written back is
/// byte-identical to the file it came from, and a document that is not byte-identical after a
/// write is a document that changed. <see cref="TryWrite"/> parses what it wrote and refuses
/// to hand back bytes the shipping reader does not read as the same document.
///
/// The Memory manifest is the allocation plan the engine's loader sizes its pools from; an edit
/// that adds elements, attributes, strings or slides calls <see cref="Recount"/> so the counts
/// state the content. Which word means what was pinned by deriving each from every shipped
/// file's content (see the Recount remarks); the words no derivation reproduces are kept as the
/// document carries them.
/// </summary>
public static class Engine_BgfWriter
{
	/// <summary>Writes the document; the bytes parse back to an equal document or this throws.</summary>
	public static byte[] Write(Engine_BgfDocument doc)
	{
		if (!TryWrite(doc, out byte[] bytes, out string error))
		{
			throw new InvalidDataException("bgf write: " + error);
		}

		return bytes;
	}

	/// <summary>
	/// Writes the document and checks the result parses back to the same document. False, with
	/// the first difference named, when it does not — nothing is handed back then.
	/// </summary>
	public static bool TryWrite(Engine_BgfDocument doc, out byte[] bytes, out string error)
	{
		bytes = Emit(doc);
		Engine_BgfDocument back;
		try
		{
			back = Engine_BgfDocument.Parse(bytes);
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

	/// <summary>The bytes, with no check. <see cref="TryWrite"/> is the public path.</summary>
	private static byte[] Emit(Engine_BgfDocument doc)
	{
		// The manifest states the content: restated on every write, which is what makes an
		// edited document consistent and costs a shipped one nothing (Recount reproduces its
		// stored manifest — the round-trip test asserts that on all 52).
		Recount(doc);

		var w = new Writer(doc.BigEndian);

		// Header: the document's own bytes with the decoded fields written over them.
		var header = new byte[Engine_BgfDocument.SectionsStart];
		Array.Copy(doc.Header, header, Math.Min(doc.Header.Length, header.Length));
		header[0] = (byte)(doc.BigEndian ? 1 : 0);
		Encoding.ASCII.GetBytes(Engine_BgfDocument.MagicText, 0, 8, header, 6);
		PutU32(header, 14, unchecked((uint)doc.Version), doc.BigEndian);
		PutU16(header, 22, (ushort)doc.CanvasWidth, doc.BigEndian);
		PutU16(header, 24, (ushort)doc.CanvasHeight, doc.BigEndian);
		w.Bytes(header);

		// 1. Memory
		w.Section(s =>
		{
			for (int i = 0; i < Engine_BgfDocument.MemoryCountCount; i++)
				s.U32(i < doc.MemoryCounts.Length ? doc.MemoryCounts[i] : 0u);
			foreach (uint extra in doc.MemoryExtra)
				s.U32(extra);
		});

		// 2. String
		w.Section(s =>
		{
			foreach (string text in doc.Strings)
			{
				byte[] utf8 = Encoding.UTF8.GetBytes(text);
				s.U32((uint)utf8.Length);
				s.Bytes(utf8);
			}
		});

		// 3. Element
		w.Section(s =>
		{
			foreach (BgfElement e in doc.Elements)
			{
				s.U32(e.NameHash);
				s.U32(unchecked((uint)e.ParentOrdinal));
				s.U32(e.ObjectId);
				s.U8((byte)e.Type);
				s.U8(e.Flags);
				if (e.IsComponent)
				{
					s.U8(e.ComponentPad);
					s.U32(e.ComponentExtra);
				}

				s.U32((uint)e.Attributes.Length);
				foreach (BgfAttribute a in e.Attributes)
					s.Attribute(a);
			}
		});

		// 4. Contract
		w.Section(s =>
		{
			foreach (BgfContract c in doc.Contracts)
			{
				s.U32(unchecked((uint)c.ElementOrdinal));
				s.U32((uint)c.NameIndices.Length);
				foreach (int index in c.NameIndices)
					s.U32(unchecked((uint)index));
			}
		});

		// 5. Animation
		w.Section(s =>
		{
			if (doc.Animations.Length == 0 && doc.AnimationRaw is { } raw)
			{
				s.Bytes(raw);
				return;
			}

			foreach (BgfAnimation a in doc.Animations)
			{
				s.U32(unchecked((uint)a.ElementOrdinal));
				s.U32(a.AttributeId);
				s.U8((byte)(a.ActiveOnLoad ? 1 : 0));
				s.U8((byte)(a.Dynamic ? 1 : 0));
				s.U16((ushort)a.Keys.Length);
				foreach (BgfAnimationKey k in a.Keys)
				{
					s.Time(k.Time);
					s.F32(k.Value);
					s.Time(k.C1Time);
					s.F32(k.C1Value);
					s.Time(k.C2Time);
					s.F32(k.C2Value);
				}
			}
		});

		// 6-7. Logic, Params
		w.Section(s => s.Bytes(doc.LogicSection));
		w.Section(s => s.Bytes(doc.ParamsSection));

		// 8. Slide
		w.Section(s =>
		{
			foreach (BgfSlide slide in doc.Slides)
			{
				var name = new byte[16];
				Encoding.ASCII.GetBytes(slide.Name, 0, Math.Min(slide.Name.Length, 16), name, 0);
				s.Bytes(name);
				s.U32(unchecked((uint)slide.OwnerElement));
				s.U8(slide.Flags);
				s.U32(unchecked((uint)slide.StartTime));
				s.U32(unchecked((uint)slide.EndTime));
				s.U32((uint)slide.Entries.Length);
				foreach (BgfSlideEntry e in slide.Entries)
				{
					s.U32(unchecked((uint)e.ElementOrdinal));
					s.U8((byte)(e.Active ? 1 : 0));
					BgfAttribute[] commands = e.Commands ?? Array.Empty<BgfAttribute>();
					s.U32((uint)commands.Length);
					foreach (BgfAttribute c in commands)
						s.Attribute(c);
					BgfAnimationEvent[] events = e.AnimationEvents ?? Array.Empty<BgfAnimationEvent>();
					s.U32((uint)events.Length);
					foreach (BgfAnimationEvent ev in events)
					{
						s.U16(ev.Flag);
						s.U16(ev.Index);
					}
				}
			}
		});

		return w.ToArray();
	}

	/// <summary>
	/// Restates the Memory manifest from the content. The words pinned here reproduce every
	/// shipped file's stored value when derived from that file's content (the round-trip test
	/// asserts it on every presentation in the banks); the others are left as carried.
	/// </summary>
	/// <remarks>
	/// Pinned: [0] elements that are not components, [1] component elements, [2] attribute records
	/// on elements, [3] contracts, [4] contract names, [6] strings, [8] animation tracks, [9]
	/// keyframes, [14] slides, [15] slide entries, [16] slide commands, [17] slide animation
	/// events. The extra words mirror the first six. [5], [7], [10]..[13] and [18] are left as
	/// carried ([5], [7] and [10]..[13] are zero in every shipped file; [18] is not derived). An
	/// undecoded Animation section (<see cref="Engine_BgfDocument.AnimationRaw"/>) keeps its
	/// track and key words too, since the content they count was not read.
	/// </remarks>
	public static void Recount(Engine_BgfDocument doc)
	{
		if (doc.MemoryCounts.Length < Engine_BgfDocument.MemoryCountCount)
		{
			Array.Resize(ref doc.MemoryCounts, Engine_BgfDocument.MemoryCountCount);
		}

		uint[] m = doc.MemoryCounts;
		m[0] = (uint)doc.Elements.Count(e => !e.IsComponent);
		m[1] = (uint)doc.Elements.Count(e => e.IsComponent);
		m[2] = (uint)doc.Elements.Sum(e => e.Attributes.Length);
		m[3] = (uint)doc.Contracts.Length;
		m[4] = (uint)doc.Contracts.Sum(c => c.NameIndices.Length);
		m[6] = (uint)doc.Strings.Length;
		if (doc.AnimationRaw is null)
		{
			m[8] = (uint)doc.Animations.Length;
			m[9] = (uint)doc.Animations.Sum(a => a.Keys.Length);
		}

		m[14] = (uint)doc.Slides.Length;
		m[15] = (uint)doc.Slides.Sum(s => s.Entries.Length);
		m[16] = (uint)doc.Slides.Sum(s => s.Entries.Sum(e => e.CommandCount));
		m[17] = (uint)doc.Slides.Sum(s => s.Entries.Sum(e => e.AnimationEvents?.Length ?? 0));

		for (int i = 0; i < doc.MemoryExtra.Length && i < 6; i++)
		{
			doc.MemoryExtra[i] = m[i];
		}
	}

	// ---- equality ---------------------------------------------------------------------------

	/// <summary>
	/// Field-for-field equality of two parsed documents, floats by their bits. The first
	/// difference is named with its path.
	/// </summary>
	public static bool Same(Engine_BgfDocument a, Engine_BgfDocument b, out string difference)
	{
		difference = string.Empty;
		if (a.BigEndian != b.BigEndian) return Differs(out difference, "endian");
		if (a.Version != b.Version) return Differs(out difference, "version");
		if (a.CanvasWidth != b.CanvasWidth || a.CanvasHeight != b.CanvasHeight) return Differs(out difference, "canvas");
		if (!SameReservedHeader(a.Header, b.Header)) return Differs(out difference, "header reserved bytes");
		if (!a.MemoryCounts.SequenceEqual(b.MemoryCounts)) return Differs(out difference, "memory counts");
		if (!a.MemoryExtra.SequenceEqual(b.MemoryExtra)) return Differs(out difference, "memory extra words");
		if (!a.Strings.SequenceEqual(b.Strings, StringComparer.Ordinal)) return Differs(out difference, "strings");

		if (a.Elements.Length != b.Elements.Length) return Differs(out difference, $"element count {a.Elements.Length} vs {b.Elements.Length}");
		for (int i = 0; i < a.Elements.Length; i++)
		{
			BgfElement x = a.Elements[i], y = b.Elements[i];
			if (x.NameHash != y.NameHash || x.ParentOrdinal != y.ParentOrdinal || x.ObjectId != y.ObjectId ||
				x.Type != y.Type || x.Flags != y.Flags || x.ComponentExtra != y.ComponentExtra ||
				(x.IsComponent && x.ComponentPad != y.ComponentPad))
			{
				return Differs(out difference, $"element {i} record");
			}

			if (!SameAttributes(x.Attributes, y.Attributes)) return Differs(out difference, $"element {i} attributes");
		}

		if (a.Contracts.Length != b.Contracts.Length) return Differs(out difference, "contract count");
		for (int i = 0; i < a.Contracts.Length; i++)
		{
			if (a.Contracts[i].ElementOrdinal != b.Contracts[i].ElementOrdinal ||
				!a.Contracts[i].NameIndices.SequenceEqual(b.Contracts[i].NameIndices))
			{
				return Differs(out difference, $"contract {i}");
			}
		}

		if (a.Animations.Length != b.Animations.Length) return Differs(out difference, "animation count");
		for (int i = 0; i < a.Animations.Length; i++)
		{
			BgfAnimation x = a.Animations[i], y = b.Animations[i];
			if (x.ElementOrdinal != y.ElementOrdinal || x.AttributeId != y.AttributeId ||
				x.ActiveOnLoad != y.ActiveOnLoad || x.Dynamic != y.Dynamic || x.Keys.Length != y.Keys.Length)
			{
				return Differs(out difference, $"animation {i} record");
			}

			for (int k = 0; k < x.Keys.Length; k++)
			{
				if (!SameKey(x.Keys[k], y.Keys[k])) return Differs(out difference, $"animation {i} key {k}");
			}
		}

		if (!(a.AnimationRaw ?? Array.Empty<byte>()).SequenceEqual(b.AnimationRaw ?? Array.Empty<byte>()))
			return Differs(out difference, "undecoded animation bytes");
		if (!a.LogicSection.SequenceEqual(b.LogicSection)) return Differs(out difference, "logic section");
		if (!a.ParamsSection.SequenceEqual(b.ParamsSection)) return Differs(out difference, "params section");

		if (a.Slides.Length != b.Slides.Length) return Differs(out difference, "slide count");
		for (int i = 0; i < a.Slides.Length; i++)
		{
			BgfSlide x = a.Slides[i], y = b.Slides[i];
			if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal) || x.OwnerElement != y.OwnerElement ||
				x.Flags != y.Flags || x.StartTime != y.StartTime || x.EndTime != y.EndTime ||
				x.Entries.Length != y.Entries.Length)
			{
				return Differs(out difference, $"slide {i} '{x.Name}' record");
			}

			for (int j = 0; j < x.Entries.Length; j++)
			{
				BgfSlideEntry p = x.Entries[j], q = y.Entries[j];
				if (p.ElementOrdinal != q.ElementOrdinal || p.Active != q.Active ||
					!SameAttributes(p.Commands ?? Array.Empty<BgfAttribute>(), q.Commands ?? Array.Empty<BgfAttribute>()) ||
					!(p.AnimationEvents ?? Array.Empty<BgfAnimationEvent>()).SequenceEqual(q.AnimationEvents ?? Array.Empty<BgfAnimationEvent>()))
				{
					return Differs(out difference, $"slide {i} '{x.Name}' entry {j}");
				}
			}
		}

		return true;
	}

	private static bool Differs(out string difference, string what)
	{
		difference = what;
		return false;
	}

	/// <summary>
	/// The header bytes the parse does not decode — [1, 6), [18, 22), [26, 32) — compared as
	/// carried; the decoded fields are compared through the document's own members, and a new
	/// document's header is composed at write time.
	/// </summary>
	private static bool SameReservedHeader(byte[] a, byte[] b)
	{
		foreach ((int from, int to) in new[] { (1, 6), (18, 22), (26, Engine_BgfDocument.SectionsStart) })
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

	private static bool SameAttributes(BgfAttribute[] a, BgfAttribute[] b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i].Packed != b[i].Packed || a[i].RawValue != b[i].RawValue) return false;
		}

		return true;
	}

	private static bool SameKey(BgfAnimationKey a, BgfAnimationKey b) =>
		Bits(a.Time) == Bits(b.Time) && Bits(a.Value) == Bits(b.Value) &&
		Bits(a.C1Time) == Bits(b.C1Time) && Bits(a.C1Value) == Bits(b.C1Value) &&
		Bits(a.C2Time) == Bits(b.C2Time) && Bits(a.C2Value) == Bits(b.C2Value);

	private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

	// ---- byte emission ----------------------------------------------------------------------

	private static void PutU32(byte[] d, int at, uint v, bool be)
	{
		if (be) BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at, 4), v);
		else BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at, 4), v);
	}

	private static void PutU16(byte[] d, int at, ushort v, bool be)
	{
		if (be) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at, 2), v);
		else BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at, 2), v);
	}

	private sealed class Writer
	{
		private readonly MemoryStream _out = new();
		private readonly bool _be;
		private readonly byte[] _scratch = new byte[4];

		public Writer(bool bigEndian) => _be = bigEndian;

		public void U8(byte v) => _out.WriteByte(v);

		public void U16(ushort v)
		{
			PutU16(_scratch, 0, v, _be);
			_out.Write(_scratch, 0, 2);
		}

		public void U32(uint v)
		{
			PutU32(_scratch, 0, v, _be);
			_out.Write(_scratch, 0, 4);
		}

		public void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));

		/// <summary>An integer time field: the parse converts the stored u32 to float, this stores the float back as that integer.</summary>
		public void Time(float t) => U32((uint)Math.Round(Math.Clamp(t, 0f, uint.MaxValue)));

		public void Bytes(byte[] bytes) => _out.Write(bytes, 0, bytes.Length);

		public void Attribute(BgfAttribute a)
		{
			U32(a.Packed);
			U32(a.RawValue);
		}

		/// <summary>A size-prefixed section: the body is written to a sub-writer and framed.</summary>
		public void Section(Action<Writer> body)
		{
			var inner = new Writer(_be);
			body(inner);
			byte[] bytes = inner.ToArray();
			U32((uint)bytes.Length);
			Bytes(bytes);
		}

		public byte[] ToArray() => _out.ToArray();
	}
}
