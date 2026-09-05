using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

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
/// One 8-byte attribute record. The id is <see cref="Engine_GameHash.AttributeId"/> of the
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

	/// <summary>Index into <see cref="Engine_BgfDocument.Elements"/>, or -1 for the root.</summary>
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
		TryGetAttribute(Engine_GameHash.AttributeId(name), out attr);
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
	/// <summary>Index into <see cref="Engine_BgfDocument.Elements"/>, not a string index.</summary>
	public int ElementOrdinal { get; init; }

	public bool Active { get; init; }

	/// <summary>
	/// Attribute writes this slide applies to the element, in file order. A slide switches
	/// pieces off AND moves and resizes the survivors (menubox's <c>type5</c> switches 23 of 90
	/// elements on and carries 27 writes), so skipping these renders every enabled piece at its
	/// authored superset position.
	/// </summary>
	public BgfAttribute[] Commands { get; init; }

	public int CommandCount => Commands?.Length ?? 0;

	public int EventCount { get; init; }

	/// <summary>Animation indices this entry switches on; -1 for a pair whose flag is clear.</summary>
	public int[]? ActivatedAnimations { get; init; }
}

/// <summary>
/// One keyframe: the value at <paramref name="Time"/>, plus the cubic-Bezier control pair of
/// the segment LEAVING this key (<c>C2</c> pairs with the NEXT key's time/value). Times share
/// the element-timeline unit — 1000 ticks per timeline, the meter's 0..100 times ten.
/// </summary>
public readonly record struct BgfAnimationKey(
	float Time, float Value, float C1Time, float C1Value, float C2Time, float C2Value);

/// <summary>
/// One animation track: keyframes over ONE attribute of ONE element, scrubbed by the
/// element's inherited timeline. Active tracks evaluate every frame in the engine
/// (`sub_822846B0`); slides toggle the active bit; DYNAMIC tracks are rebased from the live
/// attribute value on slide entry.
/// </summary>
public sealed class BgfAnimation
{
	public int ElementOrdinal;
	public uint AttributeId;
	public bool ActiveOnLoad;
	public bool Dynamic;
	public BgfAnimationKey[] Keys = Array.Empty<BgfAnimationKey>();

	/// <summary>
	/// The track's value at time <paramref name="t"/> — the engine's own evaluation
	/// (`sub_82284448`): clamped to the end keys; per segment either linear in time (both
	/// outgoing control TIMES zero) or the time-Bezier solved for the parameter, then the
	/// cubic value blend.
	/// </summary>
	public float Evaluate(float t)
	{
		if (Keys.Length == 0)
		{
			return 0f;
		}

		if (Keys.Length < 2 || t <= Keys[0].Time)
		{
			return Keys[0].Value;
		}

		if (t >= Keys[^1].Time)
		{
			return Keys[^1].Value;
		}

		int next = 1;
		while (next < Keys.Length && Keys[next].Time <= t)
		{
			++next;
		}

		BgfAnimationKey k0 = Keys[next - 1];
		BgfAnimationKey k1 = Keys[next];
		if (k1.Time <= k0.Time)
		{
			return k1.Value;
		}

		float u = k0.C1Time == 0f && k0.C2Time == 0f
			? (t - k0.Time) / (k1.Time - k0.Time)
			: SolveTimeBezier(k0.Time, k0.C1Time, k0.C2Time, k1.Time, t);
		if (u <= 0f)
		{
			return k0.Value;
		}

		if (u >= 1f)
		{
			return k1.Value;
		}

		float inv = 1f - u;
		return (inv * inv * inv * k0.Value) +
			(3f * u * inv * inv * k0.C1Value) +
			(3f * u * u * inv * k0.C2Value) +
			(u * u * u * k1.Value);
	}

	/// <summary>
	/// Solves the time Bezier <c>B(u; t0, c1, c2, t1) = t</c> for <c>u</c> — bisection,
	/// which is exact enough for a 1000-tick timeline and cannot diverge on the degenerate
	/// control layouts artists produce.
	/// </summary>
	private static float SolveTimeBezier(float t0, float c1, float c2, float t1, float t)
	{
		float lo = 0f;
		float hi = 1f;
		for (int i = 0; i < 24; i++)
		{
			float mid = (lo + hi) * 0.5f;
			float inv = 1f - mid;
			float at = (inv * inv * inv * t0) + (3f * mid * inv * inv * c1) +
				(3f * mid * mid * inv * c2) + (mid * mid * mid * t1);
			if (at < t)
			{
				lo = mid;
			}
			else
			{
				hi = mid;
			}
		}

		return (lo + hi) * 0.5f;
	}
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
/// The result of applying slides: which elements draw, and the attribute writes the slides made
/// on top of each element's own. Both halves travel together — honouring only visibility leaves
/// every surviving piece at its authored superset position.
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
public sealed class Engine_BgfDocument
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

	/// <summary>The keyframe tracks value channels scrub. See the Animation section parse.</summary>
	public BgfAnimation[] Animations = Array.Empty<BgfAnimation>();

	/// <summary>The Animation section's byte size, kept for the parse diagnosis.</summary>
	public int AnimationSectionSize;

	public string[] Strings = Array.Empty<string>();
	public BgfElement[] Elements = Array.Empty<BgfElement>();
	public BgfContract[] Contracts = Array.Empty<BgfContract>();

	/// <summary>Authored visual states, in file order. Driven by <c>MemoryCounts[14]</c>.</summary>
	public BgfSlide[] Slides = Array.Empty<BgfSlide>();

	/// <summary>
	/// The state the screen shows once its slides are applied (see
	/// <see cref="ComputeSlideState"/>). The file ships every variant stacked — menubox carries
	/// 20 <c>staron</c> quads where 5 stars belong — and a slide selects among them.
	/// </summary>
	public BgfSlideState SlideState = BgfSlideState.Empty;

	/// <summary>Element visibility under <see cref="SlideState"/>. True where the element draws.</summary>
	public bool[] MasterActive => SlideState.Active;

	public static Engine_BgfDocument Load(string path) => Parse(File.ReadAllBytes(path));

	public static Engine_BgfDocument Parse(byte[] d)
	{
		var doc = new Engine_BgfDocument();

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

		// 5. Animation — the keyframe tracks a value channel scrubs. Layout from the
		// engine's own parser (`sub_82BC8688`): per record `{u32 elementIndex, u32 attrId,
		// u8 activeOnLoad, u8 dynamic, u16 keyframeCount}` (12 bytes), then EVERY record's
		// keyframes as six 4-byte fields each — time, value, then the cubic-Bezier control
		// pair of the outgoing segment — ALL read as floats (the engine converts the integer
		// times with fcfid+frsp).
		//
		// The two pool counts live in the Memory manifest but their INDICES are not pinned,
		// so they are solved against the section size (12·A + 24·K) and the parse validates:
		// the per-record keyframe counts must sum to K and land exactly on the section end.
		// A file that fails the solve keeps an empty table rather than a wrong one.
		{
			int animSize = checked((int)r.U32());
			int animEnd = r.Pos + animSize;
			doc.Animations = Array.Empty<BgfAnimation>();
			doc.AnimationSectionSize = animSize;

			if (animSize > 0)
			{
				int solvedRecords = -1;
				for (int i = 0; i < doc.MemoryCounts.Length && solvedRecords < 0; i++)
				{
					for (int j = 0; j < doc.MemoryCounts.Length; j++)
					{
						long a = doc.MemoryCounts[i];
						long k = doc.MemoryCounts[j];
						if (a > 0 && (12 * a) + (24 * k) == animSize)
						{
							solvedRecords = (int)a;
							break;
						}
					}
				}

				// Interleaved: each 12-byte record is followed immediately by its own
				// keyframes (hud.bgf: 1472 records, 3464 keys, landing on the section end).
				if (solvedRecords > 0)
				{
					var animations = new BgfAnimation[solvedRecords];
					bool valid = true;
					for (int i = 0; i < solvedRecords; i++)
					{
						if (r.Pos + 12 > animEnd)
						{
							valid = false;
							break;
						}

						var animation = new BgfAnimation
						{
							ElementOrdinal = unchecked((int)r.U32()),
							AttributeId = r.U32(),
							ActiveOnLoad = d[r.Pos++] == 1,
							Dynamic = d[r.Pos++] == 1,
						};

						int keyCount = r.U16();
						if (r.Pos + (24L * keyCount) > animEnd)
						{
							valid = false;
							break;
						}

						animation.Keys = new BgfAnimationKey[keyCount];
						for (int k = 0; k < keyCount; k++)
						{
							animation.Keys[k] = new BgfAnimationKey(
								r.TimeF32(), r.F32(), r.TimeF32(), r.F32(),
								r.TimeF32(), r.F32());
						}

						animations[i] = animation;
					}

					// The walk must land the section EXACTLY, or the solve picked the wrong
					// count pair — keep nothing rather than garbage.
					if (valid && r.Pos == animEnd)
					{
						doc.Animations = animations;
					}
				}
			}

			r.Pos = animEnd;
		}

		// 6-7. Logic, Params — framed and skipped.
		for (int i = 0; i < 2; i++)
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

				// Commands are attribute records — the same 8-byte `packed id + value` pair
				// an element carries, so they use the same decode.
				int cmdCount = checked((int)r.U32());
				var commands = new BgfAttribute[cmdCount];
				for (int c = 0; c < cmdCount; c++)
					commands[c] = ReadAttribute(ref r);

				// The tracks this slide activates: `(flag, animationIndex)` u16 pairs. A slide
				// switches on a SUBSET of its element's tracks (`TurnOn` and `TurnOffFast` on
				// one fader are one track each) — see Engine_HudView.BindTracks.
				int evtCount = checked((int)r.U32());
				var activated = new int[evtCount];
				for (int e = 0; e < evtCount; e++)
				{
					int flag = r.U16();
					int index = r.U16();
					activated[e] = flag != 0 ? index : -1;
				}

				s.Entries[j] = new BgfSlideEntry
				{
					ElementOrdinal = ordinal,
					Active = active,
					Commands = commands,
					EventCount = evtCount,
					ActivatedAnimations = activated,
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
	/// The screen's state after applying, per owning element, its master slide then one named
	/// slide — the order <c>sub_82BB5760</c> uses. The master alone is not a renderable state:
	/// across all 52 screens it switches 100% of elements off, so a static render needs both.
	/// Each entry's attribute COMMANDS are applied along with its active bit.
	///
	/// Which named slide is correct is not derivable from the file: nothing in the binary applies
	/// one at load, the choice is script's, and Fable also drives <c>active</c> from C++ at ~30
	/// call sites. <paramref name="choose"/> picks per owner; null takes the first non-master, a
	/// preview heuristic.
	/// </summary>
	/// <summary>
	/// Recomputes the slide state, choosing a specific slide on the SCENE owner. The front end's
	/// Lua keeps a scene-level slide index — <c>FrontEnd.SLIDE = {PressStart=1, Title=2,
	/// NewGame=3, LoadGame=4, Option=5, Multiplayer=6, Debug=7}</c> — navigated with
	/// <c>goToSlide(SCENE, n)</c>; <c>frontendmainmenu.bgf</c> has exactly seven slides on owner
	/// 0. Without it the preview heuristic guesses per owner and draws all seven pages at once.
	///
	/// <paramref name="sceneSlide"/> is 1-based, as the Lua counts. Out of range leaves the scene
	/// owner on the heuristic.
	/// </summary>
	public void SetSceneSlide(int sceneSlide)
	{
		SlideState = ComputeSlideState((owner, owned) =>
		{
			if (owner != SceneOwnerElement)
			{
				return null;
			}

			// The master is the baseline and is applied separately, so it is not one of
			// the numbered slides the script indexes.
			BgfSlide[] numbered = owned.Where(s => !s.IsMaster).ToArray();
			return sceneSlide >= 1 && sceneSlide <= numbered.Length
				? numbered[sceneSlide - 1]
				: null;
		});
	}

	/// <summary>
	/// The element the script means by <c>SCENE</c> — element 0, the root component.
	/// Every screen dumped so far puts the scene-level slide set on it.
	/// </summary>
	public const int SceneOwnerElement = 0;

	/// <summary>
	/// Applies the LIVE slide of every element, as read from the running guest.
	///
	/// Indexed by element ORDINAL, not by name: the runtime walks depth-first (child, then
	/// sibling) in exactly the order elements appear in this file. A name hash is not a key — a
	/// screen has far more elements than distinct names (3812 elements, 289 names on the main
	/// menu), so hashing forces every "Group" onto one widget's state.
	///
	/// A length mismatch means the guest is on a different presentation, and nothing is applied.
	/// </summary>
	public bool SetElementSlides(IReadOnlyList<int> slidesByOrdinal)
	{
		if (slidesByOrdinal.Count != Elements.Length)
		{
			return false;
		}

		SlideState = ComputeSlideState((owner, owned) =>
		{
			if (owner < 0 || owner >= slidesByOrdinal.Count)
			{
				return null;
			}

			// Slide 0 is the master, which ComputeSlideState already applies as the
			// baseline — there is no numbered slide on top of it.
			int slide = slidesByOrdinal[owner];
			if (slide <= 0)
			{
				return null;
			}

			BgfSlide[] numbered = owned.Where(s => !s.IsMaster).ToArray();
			return slide <= numbered.Length ? numbered[slide - 1] : null;
		});

		return true;
	}

	public BgfSlideState ComputeSlideState(Func<int, BgfSlide[], BgfSlide?>? choose) =>
		ComputeSlideState(choose, null);

	/// <summary>
	/// THE LATER goToSlide WINS. The engine keeps ONE active bit per element and every
	/// `goToSlide` writes its entries into it, so when two owners name the same element (a
	/// pickbox's `unselected` turns its `frame` OFF; the frame's own opener turns it on) the
	/// call made LAST is the state on screen. <paramref name="order"/> ranks owners by when
	/// their stated slide last changed; owners are applied ascending, so the most recent
	/// write lands last. Null keeps document order.
	/// </summary>
	public BgfSlideState ComputeSlideState(
		Func<int, BgfSlide[], BgfSlide?>? choose, Func<int, int>? order)
	{
		// Start from the element's OWN authored state, not "everything visible": slides turn
		// things on and off from there, and an element no slide mentions keeps its own bit.
		var active = new bool[Elements.Length];
		for (int i = 0; i < Elements.Length; ++i)
		{
			active[i] = Elements[i].IsActive;
		}
		var overrides = new Dictionary<uint, BgfAttribute>?[Elements.Length];

		var byOwner = new Dictionary<int, List<BgfSlide>>();
		foreach (BgfSlide s in Slides)
		{
			if (!byOwner.TryGetValue(s.OwnerElement, out var list))
				byOwner[s.OwnerElement] = list = new List<BgfSlide>();
			list.Add(s);
		}

		IEnumerable<KeyValuePair<int, List<BgfSlide>>> owners = order is null
			? byOwner
			: byOwner.OrderBy(pair => order(pair.Key)).ThenBy(pair => pair.Key);
		foreach ((int owner, List<BgfSlide> owned) in owners)
		{
			// The master is identified by name, not position — nothing in the format promises
			// it is stored first.
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

				// A command is a tween's START, not its pose: `ShowPages` commands
				// `Pages.position.x = 405` and activates the track running 405 → 0 over 400
				// ticks, so the slide's resting pose is the track's LAST key.
				foreach (int index in e.ActivatedAnimations ?? Array.Empty<int>())
				{
					if (index < 0 || index >= Animations.Length)
						continue;

					BgfAnimation track = Animations[index];
					if (track.Keys.Length == 0 || track.ElementOrdinal < 0 ||
						track.ElementOrdinal >= active.Length)
						continue;

					var map = overrides[track.ElementOrdinal] ??= new Dictionary<uint, BgfAttribute>();
					map[track.AttributeId] = new BgfAttribute(
						track.AttributeId, BgfValueType.Float,
						BitConverter.SingleToUInt32Bits(track.Keys[^1].Value));
				}
			}
		}
	}

	/// <summary>
	/// Default slide for a static preview: the first non-master that is not authored to be
	/// invisible. The plain first non-master is often an <c>Intro</c> — the animation's START,
	/// e.g. menubox writing its <c>Group</c> to <c>opacity 0, position.y -600</c> where
	/// <c>Outro</c> carries the resting <c>opacity 100, position.y 30</c>.
	///
	/// Scored on the data, in two parts:
	///
	/// <list type="number">
	/// <item>Fewest entries switched ON and then written <c>opacity 0</c>.</item>
	/// <item>On a tie, smallest total positional displacement — <c>abilitiesscreen</c>'s
	///   <c>ShowPages</c> writes <c>position.x 405</c> where <c>HidePages</c> writes 0.</item>
	/// </list>
	///
	/// Ordinary pairs score zero on both, so file order still decides for them. A preview
	/// heuristic only: pass a chooser to <see cref="ComputeSlideState"/> when you know better.
	/// </summary>
	private BgfSlide? PickForPreview(List<BgfSlide> owned)
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

				// Score the slide's END pose, not its start: a command is where a tween begins
				// and an activated track where it ends — `Outro` commands the menu on canvas at
				// -400 and runs it to -800; `on` commands the dialog to opacity 0 and runs it
				// to 100.
				var pose = new Dictionary<uint, float>();
				foreach (BgfAttribute a in e.Commands)
				{
					if (a.Type == BgfValueType.Float)
						pose[a.Id] = a.AsFloat();
				}

				foreach (int index in e.ActivatedAnimations ?? Array.Empty<int>())
				{
					if (index >= 0 && index < Animations.Length &&
						Animations[index].Keys.Length > 0 &&
						Animations[index].ElementOrdinal == e.ElementOrdinal)
					{
						pose[Animations[index].AttributeId] = Animations[index].Keys[^1].Value;
					}
				}

				foreach ((uint id, float value) in pose)
				{
					if (id == OpacityId && value <= 0f)
						hidden++;
					else if (id == PositionXId || id == PositionYId)
						displacement += Math.Abs(value);
				}
			}

			// Two rules in order: reject a slide that hides the screen, then one that parks it
			// off the canvas.
			if (hidden < bestHidden || (hidden == bestHidden && displacement < bestDisplacement))
			{
				bestHidden = hidden;
				bestDisplacement = displacement;
				best = s;
			}
		}

		return best;
	}

	private static readonly uint OpacityId = Engine_GameHash.AttributeId("opacity");
	private static readonly uint PositionXId = Engine_GameHash.AttributeId("position.x");
	private static readonly uint PositionYId = Engine_GameHash.AttributeId("position.y");

	/// <summary>
	/// Reads one 8-byte attribute record: a packed descriptor then the value word. Element
	/// attributes and slide commands are the same record.
	/// </summary>
	private static BgfAttribute ReadAttribute(ref Reader r)
	{
		uint packed = r.U32();
		uint value = r.U32();
		return new BgfAttribute(
			packed & Engine_GameHash.AttributeIdMask,
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

		/// <summary>A float stored as its bit pattern.</summary>
		public float F32() => BitConverter.UInt32BitsToSingle(U32());

		/// <summary>An INTEGER time field, as the engine reads it — converted to float at load
		/// (`fcfid`+`frsp` in the shipped parser).</summary>
		public float TimeF32() => U32();
	}
}
