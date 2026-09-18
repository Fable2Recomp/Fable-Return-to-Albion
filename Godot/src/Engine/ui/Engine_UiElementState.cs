using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The guest's element state: a presentation is walked in full once, then kept current by the
/// guest's own writes — see <see cref="Engine_UiSource.TakeUiElementChanges"/> (ABI 83/84).
///
/// The join is the ELEMENT ADDRESS: the host indexes by walk ordinal, the feed states changes by
/// element pointer, and ABI 84 puts the address on each walked element.
///
/// A reloaded tree cannot be detected from the address: elements are bump-allocated from one flat
/// POD arena (<c>"Mgr:Elements"</c>, sized in <c>sub_82BC7C30</c> at 0x82bc7d44), so a reload
/// hands the SAME addresses to different elements. Two cheap checks say it instead: the element
/// count, probed each frame with no buffers; and the name hash on every change — <c>element+0</c>
/// is the sdbm hash of the element's own name (<c>sub_82189140</c>, compared at <c>82bb5664</c>).
///
/// Element colour set through <c>sub_82425C58</c> writes the text record rather than an
/// attribute, so it is invisible here — as it is to <c>GetUiAttributes</c>.
/// </summary>
public sealed class Engine_UiElementState
{
	/// <remarks>
	/// Presentation 0 is never cached: it means "whichever screen the front end has active", a
	/// different object frame to frame. Those reads pass straight through to the walk.
	/// </remarks>
	/// <summary>The one instance. Shared with the editor, like everything under Engine/.</summary>
	public static Engine_UiElementState Current { get; } = new();

	/// <summary>One id set's matrix, rebuilt only when a change touched one of its ids.</summary>
	private sealed class Matrix
	{
		public uint[] Ids = System.Array.Empty<uint>();

		public Engine_UiModel.LiveAttribute[,]? Table;

		public bool Dirty = true;
	}

	/// <summary>One presentation's cached state.</summary>
	private sealed class Screen
	{
		public List<Engine_UiModel.UiElement> Elements = new();

		/// <summary>Element address to walk ordinal — the join the feed needs.</summary>
		public readonly Dictionary<uint, int> Ordinal = new();

		/// <summary>Live attribute values by id, one column per id, indexed by ordinal.</summary>
		public readonly Dictionary<uint, uint[]> Values = new();

		/// <summary>Whether each id column has a value on each element.</summary>
		public readonly Dictionary<uint, bool[]> Present = new();

		public readonly Dictionary<long, Matrix> Matrices = new();

		/// <summary>The element list needs re-walking: never seeded, or the tree was rebuilt.</summary>
		public bool ElementsStale = true;

		/// <summary>Every attribute column needs re-seeding, for the same reasons.</summary>
		public bool AttributesStale = true;

		public int Walks;
	}

	private readonly Dictionary<uint, Screen> _screens = new();

	private ulong _drainedOn = ulong.MaxValue;

	private ulong _seenDrops;

	private long _served;

	private long _walked;

	private double _sinceReport;

	private bool _said;

	/// <summary>
	/// The live element list for one presentation, seeded once and kept current by the feed.
	/// Null exactly where the walk was: an unresolvable or empty presentation.
	/// </summary>
	public List<Engine_UiModel.UiElement>? Elements(uint presentation)
	{
		if (presentation == 0)
		{
			// Drained anyway: a frame whose only UI read is the front end's would otherwise leave
			// the feed accumulating.
			Drain();
			return Engine_UiSource.Current.GetUiElements(0);
		}

		Screen screen = Seed(presentation);
		++_served;
		return screen.Elements.Count == 0 ? null : screen.Elements;
	}

	/// <summary>
	/// The live values of <paramref name="ids"/> on every element of one presentation, in the
	/// same <c>[ordinal, id]</c> shape <c>GetUiAttributes</c> returns.
	/// </summary>
	public Engine_UiModel.LiveAttribute[,]? Attributes(uint[] ids, uint presentation)
	{
		if (ids.Length == 0)
		{
			return null;
		}

		if (presentation == 0)
		{
			Drain();
			return Engine_UiSource.Current.GetUiAttributes(ids, 0);
		}

		Screen screen = Seed(presentation);
		if (screen.Elements.Count == 0)
		{
			return null;
		}

		long key = KeyOf(ids);
		if (!screen.Matrices.TryGetValue(key, out Matrix? matrix))
		{
			matrix = new Matrix { Ids = ids };
			screen.Matrices[key] = matrix;
		}

		// A column not yet seeded is not an absent one: the feed states only changes, so an id
		// nothing has written since the seed has no entry and must be read from the guest.
		bool missing = false;
		foreach (uint id in ids)
		{
			if (!screen.Values.ContainsKey(id))
			{
				missing = true;
				break;
			}
		}

		if (missing)
		{
			SeedAttributes(presentation, screen, ids);
			matrix.Dirty = true;
		}

		if (matrix.Table is null || matrix.Dirty ||
			matrix.Table.GetLength(0) != screen.Elements.Count)
		{
			matrix.Table = Build(screen, ids, matrix.Table);
			matrix.Dirty = false;
		}

		++_served;
		return matrix.Table;
	}

	/// <summary>
	/// Drains one frame's changes and applies them. Idempotent within a frame, so no caller has
	/// to own the frame boundary — the first read of the frame does it.
	/// </summary>
	public void Drain()
	{
		ulong frame = Engine.GetProcessFrames();
		if (_drainedOn == frame)
		{
			return;
		}

		_drainedOn = frame;

		// A drop invalidates everything: this is a change log, so a lost entry leaves the host
		// permanently disagreeing about one element. Re-seed the lot.
		(ulong _, uint _, ulong dropped) = Engine_UiSource.Current.UiElementCounters();
		if (dropped != _seenDrops)
		{
			_seenDrops = dropped;
			foreach (Screen screen in _screens.Values)
			{
				screen.ElementsStale = true;
				screen.AttributesStale = true;
			}

			GD.Print($"[ui-state] the element feed DROPPED {dropped} change(s) — every cached " +
				"presentation re-seeded, because a change log with a hole in it is worse than " +
				"no log");
		}

		List<Engine_UiModel.UiElementChange> changes =
			Engine_UiSource.Current.TakeUiElementChanges();
		foreach (Engine_UiModel.UiElementChange change in changes)
		{
			Apply(change);
		}

		Report(changes.Count);
	}

	/// <summary>Forgets everything. For a host leaving the tree, or a level change.</summary>
	public void Reset()
	{
		_screens.Clear();
		_drainedOn = ulong.MaxValue;
	}

	private void Apply(Engine_UiModel.UiElementChange change)
	{
		// Text carries no presentation: `sub_82425DD0` takes the element itself and an element
		// holds no back-pointer, so join by address against what is already cached.
		if (change.Kind == Engine_UiModel.UiElementChangeKind.Text)
		{
			foreach (Screen candidate in _screens.Values)
			{
				if (candidate.Ordinal.ContainsKey(change.Element))
				{
					candidate.ElementsStale = true;
					return;
				}
			}

			return;
		}

		if (!_screens.TryGetValue(change.Presentation, out Screen? screen))
		{
			return;
		}

		if (!screen.Ordinal.TryGetValue(change.Element, out int ordinal))
		{
			// An address outside the seed. The arena is contiguous and the seed covered the
			// whole tree, so this is not one of this presentation's elements.
			return;
		}

		// The name hash is the identity check — the arena reuses addresses, so a resolving
		// ordinal proves nothing. `elem+0` is the element's name hash, carried on every change;
		// a disagreement means the tree was reloaded.
		if (screen.Elements[ordinal].NameHash != change.NameHash)
		{
			screen.ElementsStale = true;
			screen.AttributesStale = true;
			return;
		}

		switch (change.Kind)
		{
			case Engine_UiModel.UiElementChangeKind.Slide:
				screen.Elements[ordinal] = screen.Elements[ordinal] with
				{
					Slide = (int)change.Value,
				};
				break;

			case Engine_UiModel.UiElementChangeKind.Flags:
			{
				int flags = screen.Elements[ordinal].Flags;
				int mask = (int)change.Id;
				screen.Elements[ordinal] = screen.Elements[ordinal] with
				{
					Flags = change.Value != 0 ? flags | mask : flags & ~mask,
				};
				break;
			}

			case Engine_UiModel.UiElementChangeKind.Attribute:
			{
				if (!screen.Values.TryGetValue(change.Id, out uint[]? column))
				{
					// Not a column anyone has asked for.
					break;
				}

				column[ordinal] = change.Value;
				screen.Present[change.Id][ordinal] = true;
				MarkMatricesDirty(screen, change.Id);
				break;
			}
		}
	}

	private static void MarkMatricesDirty(Screen screen, uint id)
	{
		foreach (Matrix matrix in screen.Matrices.Values)
		{
			foreach (uint candidate in matrix.Ids)
			{
				if (candidate == id)
				{
					matrix.Dirty = true;
					break;
				}
			}
		}
	}

	private Screen Seed(uint presentation)
	{
		Drain();

		if (!_screens.TryGetValue(presentation, out Screen? screen))
		{
			screen = new Screen();
			_screens[presentation] = screen;
		}

		// The count probe, every frame per presentation: catches a screen reloaded under a cache
		// whose element addresses still look valid. A link walk with no copy or allocation.
		if (!screen.ElementsStale && screen.Elements.Count > 0 &&
			Engine_UiSource.Current.GetUiElementCount(presentation) != screen.Elements.Count)
		{
			screen.ElementsStale = true;
			screen.AttributesStale = true;
		}

		if (!screen.ElementsStale)
		{
			return screen;
		}

		screen.ElementsStale = false;

		List<Engine_UiModel.UiElement>? walked =
			Engine_UiSource.Current.GetUiElements(presentation);
		++screen.Walks;
		++_walked;

		if (walked is null)
		{
			screen.Elements = new List<Engine_UiModel.UiElement>();
			screen.Ordinal.Clear();
			screen.Values.Clear();
			screen.Present.Clear();
			screen.Matrices.Clear();
			return screen;
		}

		bool resized = screen.Elements.Count != walked.Count;
		screen.Elements = walked;
		screen.Ordinal.Clear();
		for (int i = 0; i < screen.Elements.Count; ++i)
		{
			// Zero is not an address: elements reported without one must not collide on ordinal 0.
			uint address = screen.Elements[i].Address;
			if (address != 0)
			{
				screen.Ordinal[address] = i;
			}
		}

		if (resized || screen.AttributesStale)
		{
			// The columns are indexed by ordinal, so a tree of a different size invalidates
			// every one of them regardless of what the feed said.
			var ids = new List<uint>(screen.Values.Keys);
			screen.Values.Clear();
			screen.Present.Clear();
			foreach (Matrix matrix in screen.Matrices.Values)
			{
				matrix.Dirty = true;
			}

			screen.AttributesStale = false;
			if (ids.Count > 0)
			{
				SeedAttributes(presentation, screen, ids.ToArray());
			}
		}

		return screen;
	}

	private static void SeedAttributes(uint presentation, Screen screen, uint[] ids)
	{
		Engine_UiModel.LiveAttribute[,]? table =
			Engine_UiSource.Current.GetUiAttributes(ids, presentation);

		int rows = screen.Elements.Count;
		for (int column = 0; column < ids.Length; ++column)
		{
			uint id = ids[column];
			if (!screen.Values.TryGetValue(id, out uint[]? values) || values.Length != rows)
			{
				values = new uint[rows];
				screen.Values[id] = values;
				screen.Present[id] = new bool[rows];
			}

			bool[] present = screen.Present[id];
			if (table is null)
			{
				continue;
			}

			int limit = System.Math.Min(rows, table.GetLength(0));
			for (int row = 0; row < limit; ++row)
			{
				values[row] = table[row, column].RawValue;
				present[row] = table[row, column].Present;
			}
		}
	}

	/// <summary>Fills the existing table in place when its shape still fits, so a per-tick rebuild does not allocate.</summary>
	private static Engine_UiModel.LiveAttribute[,] Build(Screen screen, uint[] ids, Engine_UiModel.LiveAttribute[,]? existing)
	{
		int rows = screen.Elements.Count;
		Engine_UiModel.LiveAttribute[,] table;
		if (existing is not null && existing.GetLength(0) == rows && existing.GetLength(1) == ids.Length)
		{
			table = existing;
			System.Array.Clear(table);
		}
		else
		{
			table = new Engine_UiModel.LiveAttribute[rows, ids.Length];
		}
		for (int column = 0; column < ids.Length; ++column)
		{
			if (!screen.Values.TryGetValue(ids[column], out uint[]? values))
			{
				continue;
			}

			bool[] present = screen.Present[ids[column]];
			int limit = System.Math.Min(rows, values.Length);
			for (int row = 0; row < limit; ++row)
			{
				table[row, column] = new Engine_UiModel.LiveAttribute(values[row], present[row]);
			}
		}

		return table;
	}

	private static long KeyOf(uint[] ids)
	{
		long key = 1469598103934665603L;
		foreach (uint id in ids)
		{
			key = (key ^ id) * 1099511628211L;
		}

		return key;
	}

	/// <summary>
	/// Reports walks per serve — a ratio near 1 means something is marking screens stale every
	/// frame and the seed is buying nothing.
	/// </summary>
	private void Report(int applied)
	{
		if (!Engine_UiSource.Current.IsLive)
		{
			return;
		}

		if (!_said && _served > 0)
		{
			_said = true;
			GD.Print("[ui-state] the element WALK is now a SEED — presentations are read once " +
				"and kept current by the guest's own writers (ABI 83/84)");
		}

		_sinceReport += 1.0;
		if (_sinceReport < 900.0 || _served == 0)
		{
			return;
		}

		_sinceReport = 0.0;
		GD.Print($"[ui-state] {_served} read(s) served from {_walked} walk(s) " +
			$"({(_served == 0 ? 0.0 : (double)_walked / _served):P1} of reads still walk), " +
			$"{_screens.Count} presentation(s) cached, {applied} change(s) applied this frame");
	}
}
