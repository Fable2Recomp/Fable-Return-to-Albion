using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Launcher.Formats;

/// <summary>Element kinds, from the engine's own type-name table.</summary>
public enum BgfElementType : byte
{
	Unknown = 0,
	Node = 1,
	Camera = 2,
	Light = 3,
	Text = 4,
	Material = 5,
	Texture = 6,
	Component = 7,
	Behavior = 8,
}

/// <summary>Attribute value kinds, from the packed word's 3-bit type field.</summary>
public enum BgfValueType : byte
{
	None = 0,
	Int1 = 1,
	Int2 = 2,
	Float = 3,
	Bool = 4,
	String = 5,
	ElementRef = 6,
	None7 = 7,
}

/// <summary>
/// One 8-byte attribute record. The id is <see cref="GameHash.AttributeId"/> of the
/// authored name — sdbm, masked to 27 bits.
/// </summary>
public readonly struct BgfAttribute
{
	public readonly uint Id;
	public readonly BgfValueType Type;
	public readonly uint RawValue;

	public BgfAttribute(uint id, BgfValueType type, uint rawValue)
	{
		Id = id; Type = type; RawValue = rawValue;
	}

	public int AsInt() => unchecked((int)RawValue);
	public bool AsBool() => RawValue != 0;
	public float AsFloat() => BitConverter.UInt32BitsToSingle(RawValue);

	/// <summary>For <see cref="BgfValueType.String"/>, the value is an index into the string table.</summary>
	public int AsStringIndex() => unchecked((int)RawValue);
}

public sealed class BgfElement
{
	/// <summary>sdbm32 of the element's authored name — equal to the <c>name</c> attribute's string.</summary>
	public uint NameHash;

	/// <summary>Index into <see cref="BgfDocument.Elements"/>, or -1 for the root.</summary>
	public int ParentOrdinal;

	/// <summary>Stable object id. The <c>.bsg</c> asset table and <c>.fac</c> sidecar key on this.</summary>
	public uint ObjectId;

	public BgfElementType Type;

	/// <summary>Raw file flag byte. Bit 0 = initially active, bit 2 = extended (component) record.</summary>
	public byte Flags;

	public bool IsActive => (Flags & 1) != 0;
	public bool IsComponent => (Flags & 4) != 0;

	/// <summary>Only present on component records.</summary>
	public uint ComponentExtra;

	public BgfAttribute[] Attributes = Array.Empty<BgfAttribute>();

	public bool TryGetAttribute(uint id, out BgfAttribute attr)
	{
		foreach (var a in Attributes)
		{
			if (a.Id == id)
			{
				attr = a;
				return true;
			}
		}
		attr = default;
		return false;
	}

	public bool TryGetAttribute(string name, out BgfAttribute attr) =>
		TryGetAttribute(GameHash.AttributeId(name), out attr);
}

/// <summary>
/// One exported entry point — the contract between game code and a presentation.
/// e.g. <c>hud.bgf</c> exports <c>OnPressA</c>, <c>ShowHighlightNPC</c>.
/// </summary>
/// <summary>
/// One element's participation in a slide. The <see cref="Active"/> bool is the
/// engine's visibility control: <c>sub_82BB6870</c> applies it as
/// <c>SetFlag(element, 0x2000)</c>, which becomes <c>globalactive</c> (0x1000) and is
/// tested by the scene-graph walk — an inactive node and its ENTIRE SUBTREE are
/// skipped, so nothing under it draws.
/// </summary>
public readonly struct BgfSlideEntry
{
	/// <summary>Index into <see cref="BgfDocument.Elements"/>, not a string index.</summary>
	public int ElementOrdinal { get; init; }

	public bool Active { get; init; }

	/// <summary>
	/// Attribute writes this slide applies to the element, in file order.
	///
	/// These are NOT decoration. A component's slides are how one authored superset
	/// becomes several concrete layouts: the slide switches some pieces off AND moves
	/// and resizes the survivors. menubox's <c>type5</c> switches 23 of 90 elements on
	/// and carries 27 of these. Skipping them renders every enabled piece at its
	/// superset position, which shows up as a nine-slice box with its edge and middle
	/// tiles missing and holes where they should have been.
	/// </summary>
	public BgfAttribute[] Commands { get; init; }

	public int CommandCount => Commands?.Length ?? 0;

	public int EventCount { get; init; }
}

/// <summary>
/// An authored visual state. Slides belong to ELEMENTS, not to the file: each element
/// owns a contiguous run and its relative index 0 is its own "master", re-applied as a
/// baseline before every switch (<c>sub_82BB5760</c>, which rejects a target index of 0).
/// </summary>
public sealed class BgfSlide
{
	public string Name = string.Empty;
	public int OwnerElement;

	/// <summary>Bits 0-1 playback mode, 2 paused, 3-4 timeline args. Bits 5-7 are never read.</summary>
	public byte Flags;

	public int StartTime;
	public int EndTime;
	public BgfSlideEntry[] Entries = Array.Empty<BgfSlideEntry>();

	public bool IsMaster => Name.Equals("Master Slide", StringComparison.Ordinal);
}

/// <summary>
/// The result of applying slides: which elements draw, and the attribute writes the
/// slides made on top of each element's own.
///
/// The two halves have to travel together. A slide switches pieces of a component off
/// AND repositions the survivors, so honouring only the visibility half renders every
/// surviving piece at its authored superset position — a nine-slice box with its edges
/// missing and holes where they belonged.
/// </summary>
public sealed class BgfSlideState
{
	public static readonly BgfSlideState Empty = new(Array.Empty<bool>(), Array.Empty<Dictionary<uint, BgfAttribute>?>());

	/// <summary>Per element ordinal: true where the element draws.</summary>
	public readonly bool[] Active;

	/// <summary>
	/// Per element ordinal: the attributes a slide wrote, or null where none did.
	/// These OVERRIDE the element's own attributes — the engine applies the element's
	/// at load and the slide's on switch, so the slide is the later write.
	/// </summary>
	public readonly Dictionary<uint, BgfAttribute>?[] Overrides;

	public BgfSlideState(bool[] active, Dictionary<uint, BgfAttribute>?[] overrides)
	{
		Active = active;
		Overrides = overrides;
	}

	/// <summary>The slide's write for an attribute on an element, if it made one.</summary>
	public bool TryGetOverride(int elementOrdinal, uint attributeId, out BgfAttribute value)
	{
		if (elementOrdinal >= 0 && elementOrdinal < Overrides.Length &&
			Overrides[elementOrdinal] is { } map)
		{
			return map.TryGetValue(attributeId, out value);
		}

		value = default;
		return false;
	}

	/// <summary>Total attribute writes across every element, for diagnostics.</summary>
	public int OverrideCount
	{
		get
		{
			int n = 0;
			foreach (var map in Overrides)
				n += map?.Count ?? 0;
			return n;
		}
	}
}

public sealed class BgfContract
{
	public int ElementOrdinal;
	public string[] Names = Array.Empty<string>();
}

/// <summary>
/// Reader for Autodesk/Anark Gameface <c>.bgf</c> presentation documents
/// (<c>art/gui/gameface/*.bgf</c>).
///
/// A 32-byte header, then nine size-prefixed sections in fixed order: Memory,
/// String, Element, Contract, Animation, Logic, Params, Slide.
///
/// Both endiannesses ship — byte 0 of the header selects. The Memory section is
/// the allocation manifest and its counts drive every later section's loop.
///
/// Sections this reader decodes: Memory, String, Element, Contract. The remaining
/// four are framed by their size prefix and skipped, which still validates the
/// whole file to EOF. Decode them when the animation/slide work needs them.
/// </summary>
public sealed class BgfDocument
{
	private const string MagicText = "AnarkBGF";
	private const int SectionsStart = 0x20;

	public bool BigEndian;
	public int Version;

	/// <summary>Authoring canvas — 960x720 for most screens, 1280x720 and 800x450 for a few.</summary>
	public int CanvasWidth;
	public int CanvasHeight;

	/// <summary>The 19 allocation counts. Indices used here: 0/1 element counts, 2 attributes, 3 contracts, 6 strings.</summary>
	public uint[] MemoryCounts = Array.Empty<uint>();

	public string[] Strings = Array.Empty<string>();
	public BgfElement[] Elements = Array.Empty<BgfElement>();
	public BgfContract[] Contracts = Array.Empty<BgfContract>();

	/// <summary>Authored visual states, in file order. Driven by <c>MemoryCounts[14]</c>.</summary>
	public BgfSlide[] Slides = Array.Empty<BgfSlide>();

	/// <summary>
	/// The state the screen shows once its slides are applied — see
	/// <see cref="ComputeSlideState"/>.
	///
	/// This is what separates a screen's real appearance from its authored superset:
	/// the file ships every variant stacked (menubox carries 20 <c>staron</c> quads
	/// where 5 stars belong), and a slide selects among them.
	/// </summary>
	public BgfSlideState SlideState = BgfSlideState.Empty;

	/// <summary>Element visibility under <see cref="SlideState"/>. True where the element draws.</summary>
	public bool[] MasterActive => SlideState.Active;

	public static BgfDocument Load(string path) => Parse(File.ReadAllBytes(path));

	public static BgfDocument Parse(byte[] d)
	{
		var doc = new BgfDocument();

		doc.BigEndian = d[0] == 1;
		var r = new Reader(d, doc.BigEndian);

		if (Encoding.ASCII.GetString(d, 6, 8) != MagicText)
			throw new InvalidDataException("bgf: bad magic, expected " + MagicText);

		r.Pos = 14;
		doc.Version = checked((int)r.U32());
		if (doc.Version != 3)
			throw new InvalidDataException($"bgf: unsupported version {doc.Version}");

		r.Pos = 22;
		doc.CanvasWidth = r.U16();
		doc.CanvasHeight = r.U16();

		r.Pos = SectionsStart;

		// 1. Memory — 25 u32; the last six mirror the first six in every shipped file.
		int memSize = checked((int)r.U32());
		int memEnd = r.Pos + memSize;
		doc.MemoryCounts = new uint[19];
		for (int i = 0; i < 19; i++)
			doc.MemoryCounts[i] = r.U32();
		r.Pos = memEnd;

		int elementCount = checked((int)(doc.MemoryCounts[0] + doc.MemoryCounts[1]));
		int stringCount = checked((int)doc.MemoryCounts[6]);
		int contractCount = checked((int)doc.MemoryCounts[3]);

		// 2. String — length-prefixed, NOT NUL terminated.
		int strSize = checked((int)r.U32());
		int strEnd = r.Pos + strSize;
		doc.Strings = new string[stringCount];
		for (int i = 0; i < stringCount; i++)
		{
			int len = checked((int)r.U32());
			doc.Strings[i] = Encoding.UTF8.GetString(d, r.Pos, len);
			r.Pos += len;
		}
		Expect(r.Pos, strEnd, "String");

		// 3. Element
		int elemSize = checked((int)r.U32());
		int elemEnd = r.Pos + elemSize;
		doc.Elements = new BgfElement[elementCount];
		for (int i = 0; i < elementCount; i++)
		{
			var e = new BgfElement
			{
				NameHash = r.U32(),
				ParentOrdinal = unchecked((int)r.U32()),
				ObjectId = r.U32(),
			};
			e.Type = (BgfElementType)d[r.Pos++];
			e.Flags = d[r.Pos++];
			if (e.IsComponent)
			{
				r.Pos++; // discarded by the engine's own loader
				e.ComponentExtra = r.U32();
			}

			int attrCount = checked((int)r.U32());
			var attrs = new BgfAttribute[attrCount];
			for (int a = 0; a < attrCount; a++)
				attrs[a] = ReadAttribute(ref r);
			e.Attributes = attrs;
			doc.Elements[i] = e;
		}
		Expect(r.Pos, elemEnd, "Element");

		// 4. Contract — the presentation's exported entry points.
		int conSize = checked((int)r.U32());
		int conEnd = r.Pos + conSize;
		doc.Contracts = new BgfContract[contractCount];
		for (int i = 0; i < contractCount; i++)
		{
			var c = new BgfContract { ElementOrdinal = unchecked((int)r.U32()) };
			int n = checked((int)r.U32());
			c.Names = new string[n];
			for (int k = 0; k < n; k++)
			{
				int idx = checked((int)r.U32());
				c.Names[k] = idx >= 0 && idx < doc.Strings.Length ? doc.Strings[idx] : string.Empty;
			}
			doc.Contracts[i] = c;
		}
		Expect(r.Pos, conEnd, "Contract");

		// 5-7. Animation, Logic, Params — framed and skipped.
		for (int i = 0; i < 3; i++)
		{
			int size = checked((int)r.U32());
			r.Pos += size;
		}

		// 8. Slide. Layout from the loader sub_82BC8F30; the loop is driven by
		// MemoryCounts[14] (NOT [8] — for menubox both the slide count and the master
		// slide's own entryCount happen to be near 33, which is a coincidence).
		int slideSize = checked((int)r.U32());
		int slideEnd = r.Pos + slideSize;
		int slideCount = checked((int)doc.MemoryCounts[14]);
		doc.Slides = new BgfSlide[slideCount];
		for (int i = 0; i < slideCount; i++)
		{
			var s = new BgfSlide
			{
				Name = Encoding.ASCII.GetString(d, r.Pos, 16).TrimEnd('\0'),
			};
			r.Pos += 16;
			s.OwnerElement = unchecked((int)r.U32());
			s.Flags = d[r.Pos++];
			s.StartTime = unchecked((int)r.U32());
			s.EndTime = unchecked((int)r.U32());

			int entryCount = checked((int)r.U32());
			s.Entries = new BgfSlideEntry[entryCount];
			for (int j = 0; j < entryCount; j++)
			{
				int ordinal = unchecked((int)r.U32());
				bool active = d[r.Pos++] != 0;

				// Commands are attribute records — byte for byte the same 8-byte
				// `packed id + value` pair an element carries, so they go through the
				// same decode rather than a second reader.
				int cmdCount = checked((int)r.U32());
				var commands = new BgfAttribute[cmdCount];
				for (int c = 0; c < cmdCount; c++)
					commands[c] = ReadAttribute(ref r);

				int evtCount = checked((int)r.U32());
				r.Pos += evtCount * 4;

				s.Entries[j] = new BgfSlideEntry
				{
					ElementOrdinal = ordinal,
					Active = active,
					Commands = commands,
					EventCount = evtCount,
				};
			}
			doc.Slides[i] = s;
		}
		Expect(r.Pos, slideEnd, "Slide");

		doc.SlideState = doc.ComputeSlideState(null);

		if (r.Pos != d.Length)
			throw new InvalidDataException($"bgf: {d.Length - r.Pos} trailing bytes, expected 0");

		return doc;
	}

	/// <summary>
	/// The screen's state after applying, per owning element, its master slide followed
	/// by one named slide — the order <c>sub_82BB5760</c> uses (master as baseline, then
	/// the target).
	///
	/// The master slide alone is NOT a renderable state: measured across all 52 screens
	/// it switches 100% of elements off. It is a clean slate that a named slide then
	/// turns back on, so a static render needs both.
	///
	/// Each entry carries attribute COMMANDS as well as its active bit, and both halves
	/// are applied. A component's slides are how one authored superset becomes several
	/// concrete layouts, and the layout half lives entirely in those commands.
	///
	/// <b>Which named slide is correct is not derivable from the file.</b> Nothing in the
	/// binary applies a slide at load; the choice is made by script, and Fable also
	/// drives <c>active</c> directly from C++ at ~30 call sites. <paramref name="choose"/>
	/// picks per owner; null takes the first non-master, which is a heuristic for
	/// previewing, not a reproduction of what the game shows.
	/// </summary>
	public BgfSlideState ComputeSlideState(Func<int, BgfSlide[], BgfSlide?>? choose)
	{
		var active = new bool[Elements.Length];
		Array.Fill(active, true);
		var overrides = new Dictionary<uint, BgfAttribute>?[Elements.Length];

		var byOwner = new Dictionary<int, List<BgfSlide>>();
		foreach (BgfSlide s in Slides)
		{
			if (!byOwner.TryGetValue(s.OwnerElement, out var list))
				byOwner[s.OwnerElement] = list = new List<BgfSlide>();
			list.Add(s);
		}

		foreach ((int owner, List<BgfSlide> owned) in byOwner)
		{
			// The master is identified by name, not by position: nothing in the format
			// promises it is stored first, and taking owned[0] on faith would silently
			// apply a named slide as the baseline.
			BgfSlide? master = owned.FirstOrDefault(s => s.IsMaster);
			if (master is not null)
				Apply(master);

			BgfSlide? target = choose is not null
				? choose(owner, owned.ToArray())
				: PickForPreview(owned);
			if (target is not null)
				Apply(target);
		}
		return new BgfSlideState(active, overrides);

		void Apply(BgfSlide s)
		{
			foreach (BgfSlideEntry e in s.Entries)
			{
				if (e.ElementOrdinal < 0 || e.ElementOrdinal >= active.Length)
					continue;

				active[e.ElementOrdinal] = e.Active;

				foreach (BgfAttribute a in e.Commands)
				{
					var map = overrides[e.ElementOrdinal] ??= new Dictionary<uint, BgfAttribute>();
					map[a.Id] = a;
				}
			}
		}
	}

	/// <summary>
	/// Default slide for a static preview: the first non-master that is not authored to
	/// be invisible.
	///
	/// Taking the first non-master outright is wrong in a specific, common way. A screen
	/// whose owner ships <c>Intro</c> and <c>Outro</c> has them in that order, and
	/// <c>Intro</c> is the animation's START — menubox's writes its <c>Group</c> to
	/// <c>opacity 0, position.y -600</c>, i.e. transparent and 600 units below frame.
	/// <c>Outro</c> carries the resting values (<c>opacity 100, position.y 30</c>). So
	/// "first" previews the state the screen is authored NOT to be seen in, and the
	/// symptom is a screen that renders correctly just outside the camera.
	///
	/// The test is the data, not the name, and it has two parts:
	///
	/// <list type="number">
	/// <item>Count the entries this slide switches ON and then writes <c>opacity 0</c>
	///   to. Fewest wins. That is what rejects an <c>Intro</c>.</item>
	/// <item>On a tie, prefer the smallest total positional displacement. A slide that
	///   parks its subtree hundreds of units off the canvas — <c>abilitiesscreen</c>'s
	///   <c>ShowPages</c> writes <c>position.x 405</c> where <c>HidePages</c> writes 0 —
	///   is one the player never sees in that state either, and on the busy screens it
	///   is what pushes most of the content outside the authored camera.</item>
	/// </list>
	///
	/// Ordinary pairs are left alone: a button's <c>Unselected</c>/<c>Selected</c> both
	/// score zero on both counts, so file order still decides.
	///
	/// It remains a preview heuristic. Which slide the GAME shows is script's decision;
	/// pass a chooser to <see cref="ComputeSlideState"/> when you know better.
	/// </summary>
	private static BgfSlide? PickForPreview(List<BgfSlide> owned)
	{
		BgfSlide? best = null;
		int bestHidden = int.MaxValue;
		float bestDisplacement = float.MaxValue;

		foreach (BgfSlide s in owned)
		{
			if (s.IsMaster)
				continue;

			int hidden = 0;
			float displacement = 0f;

			foreach (BgfSlideEntry e in s.Entries)
			{
				if (!e.Active)
					continue;

				foreach (BgfAttribute a in e.Commands)
				{
					if (a.Type != BgfValueType.Float)
						continue;

					if (a.Id == OpacityId && a.AsFloat() <= 0f)
						hidden++;
					else if (a.Id == PositionXId || a.Id == PositionYId)
						displacement += Math.Abs(a.AsFloat());
				}
			}

			// Two rules, in order. Both exist because a slide that hides the screen or
			// parks it off the side of the canvas is one the player never sees, so it
			// cannot be what a static preview should show.
			if (hidden < bestHidden || (hidden == bestHidden && displacement < bestDisplacement))
			{
				bestHidden = hidden;
				bestDisplacement = displacement;
				best = s;
			}
		}

		return best;
	}

	private static readonly uint OpacityId = GameHash.AttributeId("opacity");
	private static readonly uint PositionXId = GameHash.AttributeId("position.x");
	private static readonly uint PositionYId = GameHash.AttributeId("position.y");

	/// <summary>
	/// Reads one 8-byte attribute record: a packed descriptor then the value word. Used
	/// for both element attributes and slide commands — they are the same record, and
	/// reading them with one function is what keeps that true.
	/// </summary>
	private static BgfAttribute ReadAttribute(ref Reader r)
	{
		uint packed = r.U32();
		uint value = r.U32();
		return new BgfAttribute(
			packed & GameHash.AttributeIdMask,
			(BgfValueType)((packed >> 27) & 7),
			value);
	}

	/// <summary>The element's authored name, read from its <c>name</c> attribute.</summary>
	public string GetName(BgfElement e) =>
		e.TryGetAttribute("name", out var a) && a.Type == BgfValueType.String
			? GetString(a.AsStringIndex())
			: string.Empty;

	public string GetString(int index) =>
		index >= 0 && index < Strings.Length ? Strings[index] : string.Empty;

	private static void Expect(int actual, int expected, string section)
	{
		if (actual != expected)
			throw new InvalidDataException(
				$"bgf: {section} section ended at {actual}, expected {expected}");
	}

	private struct Reader
	{
		private readonly byte[] _d;
		private readonly bool _be;
		public int Pos;

		public Reader(byte[] d, bool bigEndian)
		{
			_d = d; _be = bigEndian; Pos = 0;
		}

		public uint U32()
		{
			var s = _d.AsSpan(Pos, 4);
			Pos += 4;
			return _be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
		}

		public ushort U16()
		{
			var s = _d.AsSpan(Pos, 2);
			Pos += 2;
			return _be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
		}
	}
}
