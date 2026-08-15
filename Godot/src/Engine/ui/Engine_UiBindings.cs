using System.Collections.Generic;
using Godot;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Which live VALUE each element of the in-game HUD draws, read out of <c>hud.bgf</c> itself.
///
/// THIS IS THE JOIN THE HUD HAS INSTEAD OF A SCRIPT. The front end could be rebuilt natively
/// because the shipped Lua stated every number — slot positions, the auto-repeat curve, the
/// tab indents. The HUD states none of that: <c>hud.lua</c> has no layout tables, no timing
/// constants, and — checked across all 587 shipped chunks — no reader for a single hero stat.
/// Its `g_HUD.State` health and mana values are Anark authoring placeholders, each written in
/// exactly one chunk and read in none.
///
/// What the HUD has instead is a BINDING IN THE FILE. An element carries an attribute naming
/// the value it draws:
///
/// <code>
/// element 49  Bar      MeterName  "PlayerHealth"      &lt;- the fill
/// element 68  BarMax   MeterName  "PlayerHealthMax"   &lt;- where the current max sits
/// element 57  Component PulserName "PlayerLowHealthPulser"
/// element 45  PlayerHealth SlideToggleName "TopBox"
/// </code>
///
/// and C++ writes a percentage against a hash of that STRING every frame — the health path is
/// game vtable `0x82001610` slot +2 → `sub_82286FD8` → `sub_822939B0` → `sub_821A5C68` →
/// `sub_825E9148`, which reads the hero's health component (id 36) and calls
/// `sub_823FE210(registry, FNV1("PlayerHealth"), current / absoluteMax * 100)`.
///
/// So the name an element answers to is STATED IN THE DATA, and this reads it. A host-side
/// table mapping "the health bar is the node at UpperElements/PlayerHealth/…/Bar" would be the
/// same picture reached by guessing, and would silently stop being true for any element the
/// artist moved. `AddHealthBar` being a shipped no-op is consistent with this rather than
/// evidence against it: health is not special-cased anywhere, it is one generic meter among
/// twenty.
///
/// THE FILE ALSO SAYS HOW A BAR IS DRAWN, which is why nothing here has to be chosen. Under a
/// `Bar` the artist authored <c>BarMiddle</c> and <c>BarEnd</c> with <c>scale.x = 0</c> and
/// <c>pivot.x = -50</c> — an EMPTY bar pivoted on its left edge, on the builder's 100-unit
/// base quad. The fill is x-scale grown rightward from that edge, and the authored zero is the
/// proof: an element the runtime did not drive would have been authored at the size the artist
/// wanted to see.
///
/// NOT EVERY SCREEN OPTS IN, so a caller must ask rather than assume. Measured with
/// <c>REFII_UI_BINDINGS=1</c>: <c>hud</c> carries 113 bound elements and
/// <c>minigamecompleted</c> 17, but <c>shopscreen</c> carries NONE — it is driven some other
/// way, either by Lua naming its elements at call time through <c>SetGUIText</c> or by its own
/// slides. Zero bindings is therefore an ordinary answer about a screen, not a parse failure,
/// and the front-end screens return zero for the same reason.
///
/// WHAT THIS CLASS IS NOT: a renderer, and not a list of what the names MEAN. It reports the
/// bindings the file states and the hash each one is published under. Deciding that
/// "PlayerHealth" is a fraction of the absolute maximum rather than of the current one is the
/// guest's business, and reading the value is <c>HostBridge</c>'s.
/// </summary>
public sealed class Engine_UiBindings
{
	/// <summary>
	/// The attribute an element uses to name the value it draws.
	///
	/// One enum rather than one class per kind, because the kinds differ only in which
	/// registry the runtime publishes them to — the attribute is read the same way for all of
	/// them, and a caller that only wants meters filters.
	/// </summary>
	public enum Kind
	{
		/// <summary>A bar. The value is a PERCENTAGE and drives the fill's x-scale.</summary>
		Meter,

		/// <summary>A flashing element — the low-health heart. The value is a rate.</summary>
		Pulser,

		/// <summary>A text field. The value is a Babel tag HASH, never a string.</summary>
		Text,

		/// <summary>An element switched between whole slides rather than given a value.</summary>
		SlideToggle,

		/// <summary>An element faded between slides.</summary>
		SlideFader,
	}

	/// <summary>One element's binding: what it is, and the name it answers to.</summary>
	public readonly record struct Binding(
		int ElementOrdinal, string ElementName, Kind Kind, string Name, uint NameHash);

	/// <summary>
	/// The attribute each kind is authored under, spelled exactly as the executable's own
	/// string block at 0x820D9F3C spells it.
	///
	/// CASE MATTERS. An attribute id is sdbm of the authored name with no folding, so
	/// "MeterName" and "metername" are different attributes — unlike the transform and text
	/// attributes elsewhere in a `.bgf`, which the artist happened to author lower case.
	/// </summary>
	public static readonly (Kind Kind, string Attribute)[] Attributes =
	{
		(Kind.Meter, "MeterName"),
		(Kind.Pulser, "PulserName"),
		(Kind.Text, "TextName"),
		(Kind.SlideToggle, "SlideToggleName"),
		(Kind.SlideFader, "SlideFaderName"),
	};

	private readonly List<Binding> _bindings = new();

	/// <summary>Every binding found, in element order.</summary>
	public IReadOnlyList<Binding> All => _bindings;

	/// <summary>Element ordinals bound to a given name hash, by kind. One name drives MANY.</summary>
	private readonly Dictionary<(Kind, uint), List<int>> _byName = new();

	/// <summary>
	/// Reads every binding out of a parsed <c>hud.bgf</c> (or any presentation — the front-end
	/// screens carry none, which is the correct answer for them rather than a failure).
	/// </summary>
	public static Engine_UiBindings Read(Engine_BgfDocument document)
	{
		var bindings = new Engine_UiBindings();

		// The attribute ids, hashed once. There are ~3400 elements in the HUD and five
		// attributes to test on each, so hashing the names per element would be 17,000
		// string hashes for a table that never changes.
		var ids = new (Kind Kind, uint Id)[Attributes.Length];
		for (int i = 0; i < Attributes.Length; i++)
		{
			ids[i] = (Attributes[i].Kind, Engine_GameHash.AttributeId(Attributes[i].Attribute));
		}

		for (int ordinal = 0; ordinal < document.Elements.Length; ordinal++)
		{
			BgfElement element = document.Elements[ordinal];

			foreach ((Kind kind, uint id) in ids)
			{
				if (!element.TryGetAttribute(id, out BgfAttribute attribute) ||
					attribute.Type != BgfValueType.String)
				{
					continue;
				}

				string name = document.GetString(attribute.AsStringIndex());

				// AN EMPTY BINDING IS NOT A BINDING. The artist left `MeterName ""` on several
				// elements — `TopBox`, a `left` quad, `CoopHealthAndMana`'s neighbours — and
				// hashing the empty string would publish them all under one id and make every
				// one of them follow whichever value happened to land there.
				if (name.Length == 0)
				{
					continue;
				}

				bindings.Add(new Binding(
					ordinal, ElementName(document, element, ordinal), kind, name,
					// FNV-1 of the exact string, which is the hash the RUNTIME publishes the
					// value under — `Fnv1HashWithBasis(name, -2128831035)`. Note it is NOT the
					// sdbm used for attribute ids and for element-name lookup inside a
					// presentation; the two hashes coexist and mixing them finds nothing.
					Engine_GameHash.Fnv1(name)));
			}
		}

		return bindings;
	}

	/// <summary>
	/// The element's authored name, from its own <c>name</c> attribute.
	///
	/// Read from the document rather than from a built node so this class works on a parsed
	/// file with no scene tree — which is what makes the bindings checkable headlessly. Falls
	/// back to the ordinal, because an unnamed element with a meter binding is worth seeing
	/// rather than hiding behind an empty string.
	/// </summary>
	private static string ElementName(Engine_BgfDocument document, BgfElement element, int ordinal)
	{
		if (element.TryGetAttribute("name", out BgfAttribute name) &&
			name.Type == BgfValueType.String)
		{
			string authored = document.GetString(name.AsStringIndex());
			if (authored.Length > 0)
			{
				return authored;
			}
		}

		return $"#{ordinal}";
	}

	private void Add(Binding binding)
	{
		_bindings.Add(binding);

		var key = (binding.Kind, binding.NameHash);
		if (!_byName.TryGetValue(key, out List<int>? ordinals))
		{
			_byName[key] = ordinals = new List<int>();
		}

		ordinals.Add(binding.ElementOrdinal);
	}

	/// <summary>
	/// The elements a given name drives, or an empty list.
	///
	/// A LIST, because one name genuinely drives several elements — the runtime's own setter
	/// loops the whole vector it finds (`sub_823FE210` walks `record+4`..`record+8`), and
	/// "TopBoxY" alone is on five. A lookup returning one element would drive the first and
	/// leave the rest at the artist's authored zero.
	/// </summary>
	public IReadOnlyList<int> Elements(Kind kind, uint nameHash) =>
		_byName.TryGetValue((kind, nameHash), out List<int>? ordinals)
			? ordinals
			: System.Array.Empty<int>();

	/// <summary>The elements a given name drives, by the name itself.</summary>
	public IReadOnlyList<int> Elements(Kind kind, string name) =>
		Elements(kind, Engine_GameHash.Fnv1(name));

	/// <summary>
	/// Every distinct name of a kind, with its hash — what a host asks the guest for.
	///
	/// The hashes are what the native side looks up, and taking them from the FILE rather than
	/// from a hardcoded list is the point: a meter the artist added is asked for automatically,
	/// and one nobody drives simply comes back absent.
	/// </summary>
	public IEnumerable<(string Name, uint Hash)> Names(Kind kind)
	{
		var seen = new HashSet<uint>();
		foreach (Binding binding in _bindings)
		{
			if (binding.Kind == kind && seen.Add(binding.NameHash))
			{
				yield return (binding.Name, binding.NameHash);
			}
		}
	}

	/// <summary>
	/// The bar pieces under a meter-bound element — what actually gets scaled.
	///
	/// THE NAMES ARE THE ARTIST'S, and the shape is consistent across every bar in the file:
	/// a meter element has a <c>BarMiddle</c> (the fill) and a <c>BarEnd</c> (its cap), both
	/// authored <c>scale.x = 0</c>, <c>pivot.x = -50</c>.
	/// </summary>
	public const string FillName = "BarMiddle";

	public const string FillCapName = "BarEnd";

	/// <summary>
	/// The pivot the bar pieces are authored on, in the builder's 100-unit quad space — the
	/// LEFT edge, which is why a fill grows rightward and why scaling one is not centred.
	/// </summary>
	public const float FillPivotX = -50f;

	/// <summary>A one-line description for a check tool's log.</summary>
	public string Describe()
	{
		var counts = new Dictionary<Kind, int>();
		foreach (Binding binding in _bindings)
		{
			counts[binding.Kind] = counts.GetValueOrDefault(binding.Kind) + 1;
		}

		var parts = new List<string>();
		foreach ((Kind kind, string _) in Attributes)
		{
			parts.Add($"{kind}={counts.GetValueOrDefault(kind)}");
		}

		return $"[ui-bindings] {string.Join(" ", parts)} over {_bindings.Count} bound element(s)";
	}
}
