using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE ELEMENTS OF A HEADLESS PRESENTATION, keyed the way the books key them. The guest builds no
/// <c>.bgf</c> tree (hooks/gui_headless_presentation.cpp): it makes each element as it names it —
/// child-by-name, element service vf +84 (<c>sub_82BB5658</c>: r4 the name hash, r5 the parent or
/// 0 for the root level) — and states each one's id, parent id and name hash. Its statements carry
/// <c>(presentation, serial, 0x80000000 | id)</c>. This resolves an id to the FILE's root name hash
/// and depth-first ordinal by walking the id's name path down the file's own element table
/// (<see cref="Engine_BgfDocument.Elements"/>, the order <see cref="Engine_UiScreen.Nodes"/> keeps):
/// each step is the parent's first child of that name, as <c>sub_82BB5658</c> walks first child
/// (+20) then siblings (+16); a root-level name that is the root's own answers the root
/// (0x82bb5660..0x82bb5670). A name the file repeats under one parent is made once by name and
/// the rest by the child walk (service vf +92, which makes a headless parent's file children in
/// the file's order — hooks/gui_headless_presentation.cpp, ExpandLocked), so the k-th id made under
/// a parent with a name is the parent's k-th file child of that name.
///
/// A slide entry or a pause is told to the element that owns the timeline: the guest's walk
/// (<c>sub_82BB9AB0</c>) climbs to the nearest element whose flags carry 0x0001, which the element
/// builder sizes as the 68-byte component record (<c>sub_82BB3040</c>, 32 + 36) — the file's
/// component records (<see cref="BgfElement.IsComponent"/>). A headless element owns its own
/// timeline, so the climb is made here, in the file.
/// </summary>
public sealed class Engine_UiHeadlessElements
{
	/// <summary>The flag a headless statement's ordinal word carries; the rest is the element id.</summary>
	public const uint HeadlessOrdinal = 0x80000000u;

	public static bool IsHeadless(uint ordinalWord) => (ordinalWord & HeadlessOrdinal) != 0;

	/// <summary>One stated element: its parent's id (0 the root) and name hash; false when unknown.</summary>
	public delegate bool ElementSource(uint presentation, uint serial, uint id, out uint parentId, out uint nameHash);

	private readonly Func<uint, uint, string?> _fileOf;
	private readonly ElementSource _elementOf;
	private readonly Func<string, Engine_BgfDocument?> _documentOf;
	private readonly Action<string> _log;

	private sealed class Presentation
	{
		public uint Serial;
		public string File = string.Empty;
		public Engine_BgfDocument? Document;
		public List<int>[] Children = Array.Empty<List<int>>();
		public readonly Dictionary<uint, int> Ordinals = new();
		public readonly HashSet<uint> Unresolved = new();

		/// <summary>Ids 1.. as stated, in order: parent id, name hash, and the id's rank among the
		/// ids made before it under that parent with that name (-1 when the id was not stated).</summary>
		public readonly List<(uint Parent, uint Name, int Rank)> Made = new();
		public readonly Dictionary<(uint Parent, uint Name), int> SameName = new();
	}

	private readonly Dictionary<uint, Presentation> _byAddress = new();

	public Engine_UiHeadlessElements(Func<uint, uint, string?> fileOf, ElementSource elementOf,
		Func<string, Engine_BgfDocument?> documentOf, Action<string> log)
	{
		_fileOf = fileOf;
		_elementOf = elementOf;
		_documentOf = documentOf;
		_log = log;
	}

	/// <summary>
	/// The file key of headless element <paramref name="ordinalWord"/> of <paramref name="presentation"/>
	/// made under <paramref name="serial"/>: the file's root name hash and the element's ordinal, or
	/// that of the timeline owner it climbs to when <paramref name="timelineOwner"/>. False when the
	/// file or the path cannot be resolved (logged once per id).
	/// </summary>
	public bool TryResolve(uint presentation, uint serial, uint ordinalWord, bool timelineOwner,
		out uint rootHash, out int ordinal)
	{
		rootHash = 0;
		ordinal = -1;
		Presentation? held = Held(presentation, serial);
		if (held?.Document is not { Elements.Length: > 0 } document)
		{
			return false;
		}

		uint id = ordinalWord & ~HeadlessOrdinal;
		int found = OrdinalOf(presentation, held, document, id, 0);
		if (found >= 0 && timelineOwner)
		{
			while (found >= 0 && !document.Elements[found].IsComponent)
			{
				found = document.Elements[found].ParentOrdinal;
			}
		}

		if (found < 0)
		{
			if (held.Unresolved.Add(id | (timelineOwner ? HeadlessOrdinal : 0u)))
			{
				_log($"Refii headless elements: id {id} of 0x{presentation:X8} ({held.File}) is not in the " +
					(timelineOwner ? "file under a component" : "file") + $" — {WhyUnresolved(presentation, held, document, id)}");
			}

			return false;
		}

		rootHash = document.Elements[0].NameHash;
		ordinal = found;
		return true;
	}

	/// <summary>
	/// Why an id has no file element. The usual answer is a name the file does not carry under that
	/// parent: the guest resolves HUD groups by name (`sub_827F1E18`, disc `sub_827CE7D0`:
	/// `SafeFrame`, `GolfMeter`, `NPCHighlights`, `ABXY` under `Layer`, none of them in the shipped
	/// hud.bgf) and child-by-name (`sub_82BB5658`, disc `sub_82BACC90`) answers 0 for them on a
	/// loaded tree, so the original's writes to them reached no element. The headless core makes
	/// them fileless instead; their statements have nothing to draw and are dropped here.
	/// </summary>
	private string WhyUnresolved(uint presentation, Presentation held, Engine_BgfDocument document, uint id)
	{
		if (!Made(presentation, held, id, out uint parentId, out uint nameHash, out _))
		{
			return "the guest never stated it; its statements are dropped";
		}

		int parent = OrdinalOf(presentation, held, document, parentId, 0);
		if (parent < 0)
		{
			return $"named 0x{nameHash:X8} under id {parentId}, which is not in the file either; its statements are dropped";
		}

		return $"named 0x{nameHash:X8} under file element #{parent} (0x{document.Elements[parent].NameHash:X8}), " +
			"which has no child of that name: child-by-name answers 0 on a loaded tree, so the original's " +
			"writes reached no element; its statements are dropped";
	}

	private Presentation? Held(uint presentation, uint serial)
	{
		if (_byAddress.TryGetValue(presentation, out Presentation? held) && held.Serial == serial)
		{
			return held;
		}

		held = new Presentation { Serial = serial };
		string? file = _fileOf(presentation, serial);
		if (file is null)
		{
			return null;
		}

		held.File = file;
		held.Document = _documentOf(file);
		if (held.Document is null)
		{
			_log($"Refii headless elements: 0x{presentation:X8} serial {serial} names '{file}', which does not read");
		}
		else
		{
			BgfElement[] elements = held.Document.Elements;
			held.Children = new List<int>[elements.Length];
			for (int i = 0; i < elements.Length; ++i)
			{
				held.Children[i] = new List<int>();
			}

			for (int i = 0; i < elements.Length; ++i)
			{
				int parent = elements[i].ParentOrdinal;
				if (parent >= 0 && parent < elements.Length)
				{
					held.Children[parent].Add(i);
				}
			}
		}

		_byAddress[presentation] = held;
		return held;
	}

	/// <summary>
	/// What the guest stated of id <paramref name="id"/>, the ids before it read in order so its
	/// rank among same-named siblings is known.
	/// </summary>
	private bool Made(uint presentation, Presentation held, uint id, out uint parentId, out uint nameHash, out int rank)
	{
		const int MostIds = 1 << 20;
		while (held.Made.Count < id && held.Made.Count < MostIds)
		{
			uint next = (uint)held.Made.Count + 1;
			if (_elementOf(presentation, held.Serial, next, out uint parent, out uint name))
			{
				held.SameName.TryGetValue((parent, name), out int before);
				held.SameName[(parent, name)] = before + 1;
				held.Made.Add((parent, name, before));
			}
			else
			{
				held.Made.Add((0u, 0u, -1));
			}
		}

		parentId = 0;
		nameHash = 0;
		rank = -1;
		if (id == 0 || id > held.Made.Count)
		{
			return false;
		}

		(parentId, nameHash, rank) = held.Made[(int)id - 1];
		return rank >= 0;
	}

	private int OrdinalOf(uint presentation, Presentation held, Engine_BgfDocument document, uint id, int depth)
	{
		if (id == 0)
		{
			return 0;
		}

		if (held.Ordinals.TryGetValue(id, out int known))
		{
			return known;
		}

		if (depth > 256 || !Made(presentation, held, id, out uint parentId, out uint nameHash, out int rank))
		{
			return -1;
		}

		int parent = OrdinalOf(presentation, held, document, parentId, depth + 1);
		int found = -1;
		if (parent >= 0)
		{
			if (parentId == 0 && document.Elements[0].NameHash == nameHash)
			{
				found = 0;
			}
			else
			{
				int seen = 0;
				foreach (int child in held.Children[parent])
				{
					if (document.Elements[child].NameHash == nameHash && seen++ == rank)
					{
						found = child;
						break;
					}
				}
			}
		}

		if (found >= 0)
		{
			held.Ordinals[id] = found;
		}

		return found;
	}
}
