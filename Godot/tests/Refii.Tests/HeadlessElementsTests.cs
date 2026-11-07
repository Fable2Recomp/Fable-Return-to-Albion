using System.Collections.Generic;
using GdUnit4;
using Refii.Core;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// A headless presentation's elements (hooks/gui_headless_presentation.cpp) are stated as ids with a
/// parent id and a name hash; the host keys their statements by the FILE's root name hash and
/// ordinal, the keys the books read with (<see cref="Engine_UiHeadlessElements"/>).
/// </summary>
[TestSuite]
public sealed class HeadlessElementsTests
{
	private const uint Presentation = 0x4A001000u;
	private const uint Serial = 7u;

	private static uint H(string name) => Engine_GameHash.Sdbm(name);

	/// <summary>
	/// root "HUD"(0) → Layer(1) → { UpperElements(2) → Health(3, component) → Bar(4) ; Health(5) }.
	/// </summary>
	private static Engine_BgfDocument File() => new()
	{
		Elements = new[]
		{
			new BgfElement { NameHash = H("HUD"), ParentOrdinal = -1, Flags = 4 },
			new BgfElement { NameHash = H("Layer"), ParentOrdinal = 0 },
			new BgfElement { NameHash = H("UpperElements"), ParentOrdinal = 1 },
			new BgfElement { NameHash = H("Health"), ParentOrdinal = 2, Flags = 4 },
			new BgfElement { NameHash = H("Bar"), ParentOrdinal = 3 },
			new BgfElement { NameHash = H("Health"), ParentOrdinal = 1 },
		},
	};

	/// <summary>id → (parent id, name), as the guest made them: Layer, UpperElements, Health, Bar,
	/// a root-level lookup of the root's own name, and a name the file does not have.</summary>
	private static readonly Dictionary<uint, (uint Parent, string Name)> Stated = new()
	{
		[1] = (0, "Layer"),
		[2] = (1, "UpperElements"),
		[3] = (2, "Health"),
		[4] = (3, "Bar"),
		[5] = (0, "HUD"),
		[6] = (1, "Missing"),
		[7] = (1, "Health"),
	};

	private static Engine_UiHeadlessElements Resolver(List<string> log, List<string>? reads = null, uint serial = Serial) =>
		new((presentation, s) => presentation == Presentation && s == serial ? "Data\\Art\\GUI\\Gameface\\HUD.bgf" : null,
			(uint presentation, uint s, uint id, out uint parent, out uint hash) =>
			{
				parent = 0;
				hash = 0;
				if (presentation != Presentation || s != serial || !Stated.TryGetValue(id, out var e))
				{
					return false;
				}

				parent = e.Parent;
				hash = H(e.Name);
				return true;
			},
			file =>
			{
				reads?.Add(file);
				return File();
			},
			log.Add);

	private static int Resolve(Engine_UiHeadlessElements r, uint id, bool owner = false)
	{
		bool ok = r.TryResolve(Presentation, Serial, Engine_UiHeadlessElements.HeadlessOrdinal | id, owner,
			out uint root, out int ordinal);
		if (ok)
		{
			AssertThat(root).IsEqual(H("HUD"));
		}

		return ok ? ordinal : -1;
	}

	/// <summary>Each id's name path walks the file's children, first match in file order.</summary>
	[TestCase]
	public void NamePathsResolveToFileOrdinals()
	{
		var log = new List<string>();
		Engine_UiHeadlessElements r = Resolver(log);
		AssertThat(Resolve(r, 0)).IsEqual(0);
		AssertThat(Resolve(r, 1)).IsEqual(1);
		AssertThat(Resolve(r, 2)).IsEqual(2);
		AssertThat(Resolve(r, 3)).IsEqual(3);
		AssertThat(Resolve(r, 4)).IsEqual(4);
		AssertThat(Resolve(r, 7)).IsEqual(5);
		AssertThat(log.Count).IsEqual(0);
	}

	/// <summary>A root-level lookup of the root's own name answers the root (`sub_82BB5658` 0x82bb5660).</summary>
	[TestCase]
	public void RootLevelOwnNameIsTheRoot() => AssertThat(Resolve(Resolver(new List<string>()), 5)).IsEqual(0);

	/// <summary>A slide entry or pause climbs to the nearest component, as `sub_82BB9AB0` climbs to flag 0x0001.</summary>
	[TestCase]
	public void TimelineStatementsClimbToTheOwningComponent()
	{
		Engine_UiHeadlessElements r = Resolver(new List<string>());
		AssertThat(Resolve(r, 4, owner: true)).IsEqual(3);
		AssertThat(Resolve(r, 3, owner: true)).IsEqual(3);
		AssertThat(Resolve(r, 2, owner: true)).IsEqual(0);
		AssertThat(Resolve(r, 4)).IsEqual(4);
	}

	/// <summary>A name the file lacks, or an id never stated, is dropped and logged once.</summary>
	[TestCase]
	public void UnresolvedIdsAreDroppedAndLoggedOnce()
	{
		var log = new List<string>();
		Engine_UiHeadlessElements r = Resolver(log);
		AssertThat(Resolve(r, 6)).IsEqual(-1);
		AssertThat(Resolve(r, 6)).IsEqual(-1);
		AssertThat(Resolve(r, 99)).IsEqual(-1);
		AssertThat(log.Count).IsEqual(2);
	}

	/// <summary>The file is read once per presentation serial; a stale serial resolves nothing.</summary>
	[TestCase]
	public void TheFileIsReadOncePerSerial()
	{
		var reads = new List<string>();
		Engine_UiHeadlessElements r = Resolver(new List<string>(), reads);
		Resolve(r, 1);
		Resolve(r, 3);
		AssertThat(reads.Count).IsEqual(1);
		AssertThat(r.TryResolve(Presentation, Serial + 1, Engine_UiHeadlessElements.HeadlessOrdinal | 1, false,
			out _, out _)).IsFalse();
	}

	/// <summary>The drain holds a headless statement under the file's keys, where a book reads it.</summary>
	[TestCase]
	public void StatementsLandUnderTheFileKeys()
	{
		// Replaces the default resolver, which would reach the host bridge.
		Engine_UiSlideStatements.Headless = Resolver(new List<string>());
		uint word = Engine_UiHeadlessElements.HeadlessOrdinal | 4;
		Engine_UiSlideStatements.Apply(1u, Presentation, Serial, word, 2u, 0u);
		Engine_UiSlideStatements.Apply(4u, Presentation, Serial, word, 1u, 0u);
		Engine_UiSlideStatements.ApplyText(Presentation, Serial, word, "Gold");

		Engine_UiSlideStatements.Told? owner = Engine_UiSlideStatements.Of(Presentation, H("HUD"), 3);
		AssertThat(owner.HasValue).IsTrue();
		AssertThat(owner.GetValueOrDefault().Slide).IsEqual(2);
		AssertThat(Engine_UiSlideStatements.ActiveOf(Presentation, H("HUD"), 4, 0) == true).IsTrue();
		AssertThat(Engine_UiSlideStatements.TextOf(Presentation, H("HUD"), 4, 0)).IsEqual("Gold");
	}

	/// <summary>
	/// A name the file repeats under one parent: child-by-name made the first, the child walk
	/// (ExpandLocked) the rest in file order, so the k-th id made under that parent with that name
	/// is its k-th file child of the name — however the ids' statements arrive.
	/// </summary>
	[TestCase]
	public void RepeatedNamesTakeTheirFileOrderByMadeOrder()
	{
		// root "HUD"(0) → Icons(1) → { Icon(2), Icon(3), Icon(4) }.
		var file = new Engine_BgfDocument
		{
			Elements = new[]
			{
				new BgfElement { NameHash = H("HUD"), ParentOrdinal = -1, Flags = 4 },
				new BgfElement { NameHash = H("Icons"), ParentOrdinal = 0 },
				new BgfElement { NameHash = H("Icon"), ParentOrdinal = 1 },
				new BgfElement { NameHash = H("Icon"), ParentOrdinal = 1 },
				new BgfElement { NameHash = H("Icon"), ParentOrdinal = 1 },
			},
		};
		var made = new Dictionary<uint, (uint Parent, string Name)>
		{
			[1] = (0, "Icons"),
			[2] = (1, "Icon"),
			[3] = (1, "Icon"),
			[4] = (1, "Icon"),
		};
		var r = new Engine_UiHeadlessElements(
			(presentation, s) => "Data\\Art\\GUI\\Gameface\\HUD.bgf",
			(uint presentation, uint s, uint id, out uint parent, out uint hash) =>
			{
				parent = 0;
				hash = 0;
				if (!made.TryGetValue(id, out var e))
				{
					return false;
				}

				parent = e.Parent;
				hash = H(e.Name);
				return true;
			},
			_ => file,
			_ => { });

		// The third's statement first: still the third Icon.
		AssertThat(Resolve(r, 4)).IsEqual(4);
		AssertThat(Resolve(r, 2)).IsEqual(2);
		AssertThat(Resolve(r, 3)).IsEqual(3);
	}
}
