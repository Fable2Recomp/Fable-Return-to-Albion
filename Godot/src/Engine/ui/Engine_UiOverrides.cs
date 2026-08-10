using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// Host-owned edits to the guest's front end — the layer that makes the UI TWO-WAY.
///
/// Everything else in this area is a mirror: the guest owns the elements and Godot draws what
/// it is told. That is right for fidelity and useless for change. Relabelling "Downloadable
/// Content" to "Multiplayer", hiding an entry that has no meaning in this port, or adding one
/// the original never had, all need the host to own part of the presentation.
///
/// KEYED BY NAME HASH, NOT ORDINAL. Live slides, text and visibility are all applied by
/// position in the guest's depth-first walk, which is fine for a wholesale mirror and wrong
/// for this: an override has to name ONE element and keep naming it across screens, slide
/// changes and rebuilds. The walker already reports each element's name hash, and that is
/// stable in a way an index is not.
///
/// A hash is not unique — 77 elements shared 47 names on the title screen — so an override
/// matches EVERY element with that name by default. <see cref="Occurrence"/> narrows it to the
/// nth when that matters, rather than pretending the hash was unique.
///
/// WHAT THIS DELIBERATELY DOES NOT DO: it does not write into guest memory. An element the
/// guest does not know about cannot be navigated by the guest's own focus logic, and injecting
/// one so that it could would mean reproducing the runtime's element and slide bookkeeping
/// inside its address space — emulation, for something the host can simply own. The guest's
/// entry stays and keeps its navigation; the host changes what it SAYS and, separately, what
/// activating it DOES.
/// </summary>
public sealed class Engine_UiOverrides
{
	/// <summary>One edit to one element.</summary>
	public sealed class Rule
	{
		/// <summary>sdbm name hash of the element this applies to.</summary>
		public uint NameHash;

		/// <summary>
		/// Which element with that name, or -1 for all of them. Names repeat, so an override
		/// aimed at one widget would otherwise silently hit its siblings too.
		/// </summary>
		public int Occurrence = -1;

		/// <summary>Replacement text, or null to leave the guest's text alone.</summary>
		public string? Text;

		/// <summary>
		/// Force the element hidden or shown, or null to follow the guest.
		///
		/// Hiding here is a HOST decision layered over <c>globalactive</c>; it does not pretend
		/// the guest hid it, and the diagnostic keeps the two separable.
		/// </summary>
		public bool? Visible;

		/// <summary>
		/// Host action to run when the guest reports this element activated, or null to let the
		/// guest's own behaviour stand.
		///
		/// This is the half that makes a relabel honest. A button that reads "Multiplayer" and
		/// still runs the guest's downloadable-content path is worse than one that reads
		/// "Downloadable Content", because it lies about what it will do.
		/// </summary>
		public string? Action;
	}

	private readonly Dictionary<uint, List<Rule>> _byName = new();

	/// <summary>Whether any rule is registered — the whole layer is skipped when not.</summary>
	public bool IsEmpty => _byName.Count == 0;

	public void Add(Rule rule)
	{
		if (!_byName.TryGetValue(rule.NameHash, out List<Rule>? rules))
		{
			rules = new List<Rule>();
			_byName[rule.NameHash] = rules;
		}

		rules.Add(rule);
	}

	/// <summary>Registers an override by element NAME, hashed the way the runtime hashes it.</summary>
	public void Add(string elementName, Rule rule)
	{
		rule.NameHash = Refii.Core.Engine_GameHash.Sdbm(elementName);
		Add(rule);
	}

	/// <summary>
	/// The rule for the <paramref name="occurrence"/>th element named <paramref name="nameHash"/>,
	/// or null when nothing overrides it.
	/// </summary>
	public Rule? Find(uint nameHash, int occurrence)
	{
		if (!_byName.TryGetValue(nameHash, out List<Rule>? rules))
		{
			return null;
		}

		foreach (Rule rule in rules)
		{
			if (rule.Occurrence < 0 || rule.Occurrence == occurrence)
			{
				return rule;
			}
		}

		return null;
	}

	/// <summary>
	/// Applies the rules over the live arrays the guest produced, in place.
	///
	/// AFTER the guest's values, not instead of them: the mirror stays the default and an
	/// override is a deliberate, named exception. That ordering is what keeps a screen with no
	/// rules byte-identical to the pure mirror, so this layer cannot be the cause of a
	/// fidelity problem that predates it.
	/// </summary>
	/// <param name="nameHashes">Each element's name hash, by ordinal.</param>
	/// <param name="text">Live text by ordinal, edited in place.</param>
	/// <param name="visible">Live visibility by ordinal, edited in place.</param>
	/// <returns>How many elements an override touched.</returns>
	public int Apply(IReadOnlyList<uint> nameHashes, string?[] text, bool[] visible)
	{
		if (IsEmpty)
		{
			return 0;
		}

		var seen = new Dictionary<uint, int>();
		int touched = 0;

		for (int i = 0; i < nameHashes.Count && i < text.Length && i < visible.Length; ++i)
		{
			uint hash = nameHashes[i];
			seen.TryGetValue(hash, out int occurrence);
			seen[hash] = occurrence + 1;

			if (Find(hash, occurrence) is not { } rule)
			{
				continue;
			}

			bool changed = false;
			if (rule.Text is not null)
			{
				text[i] = rule.Text;
				changed = true;
			}

			if (rule.Visible is { } forced)
			{
				visible[i] = forced;
				changed = true;
			}

			if (changed)
			{
				++touched;
			}
		}

		return touched;
	}
}
