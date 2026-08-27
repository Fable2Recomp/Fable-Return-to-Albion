using System.Collections.Generic;

using Godot;

namespace Refii.Ui;

/// <summary>
/// Building a screen's menu tree FROM THE SPAWN CAPTURE — no Gameface Lua read at all.
/// </summary>
/// <remarks>
/// The C++ builder calls `g_Menu:SpawnButton()` once per row and the Lua table only KEEPS them;
/// the tree, fold and layout are computed afterwards by Lua already translated to C#
/// (`Engine_UiExpandableMenu`, `Engine_UiMenuFormatter`). See docs/ui_native_cut.md.
///
/// The parent is STATED: the capture carries `String4`, the parent's name, per row — unlike
/// `RebuildFromLua`, which infers parentage from depth ordering and is exact only because
/// `VisibleElements` is in depth-first display order.
///
/// The capture is taken AT `SpawnButton`, so two fields written onto the Lua item afterwards are
/// absent: ghosting (a ghosted row must not commit, so an unstated one would become committable)
/// and emphasis (the quest mark on a tab face). <see cref="Engine_UiScreenMenuSpawn.Rows"/>
/// reports both as UNSTATED and the caller decides. A screen whose ghosting comes from somewhere
/// the host owns — the pause menu's, from `RefiiHostPauseTabsAvailable` — can use this today.
/// </remarks>
public static class Engine_UiScreenMenuSpawn
{
	/// <summary>One row, as the capture stated it, plus what it could not state.</summary>
	/// <param name="Ghosted">Null means UNSTATED, not "not ghosted" — reading null as false
	/// makes disabled rows committable.</param>
	/// <param name="Emphasis">Null means UNSTATED, for the same reason.</param>
	public readonly record struct Row(
		string Name,
		string Parent,
		string Label,
		int EventType,
		uint Id,
		int Rating,
		bool? Ghosted,
		Engine_UiMenuConstants.Emphasis? Emphasis);

	/// <summary>
	/// The capture's rows for one presentation, in spawn order — the order the population made
	/// them. The tree below reparents; it never sorts.
	/// </summary>
	public static List<Row> Rows()
	{
		var rows = new List<Row>();
		foreach (Engine_UiModel.MenuRow captured in Engine_UiMenuSources.SpawnedRows())
		{
			if (captured.Name.Length == 0)
			{
				continue;
			}

			rows.Add(new Row(
				captured.Name,
				captured.Parent,
				captured.Label.Length != 0 ? captured.Label : captured.Name,
				captured.EventType,
				captured.Id,
				captured.Rating,
				Ghosted: null,
				Emphasis: null));
		}

		return rows;
	}

	/// <summary>
	/// Builds <paramref name="script"/>'s tree from <paramref name="rows"/>, parenting by NAME.
	/// </summary>
	/// <remarks>
	/// `"Root"` is the guest's own word for the top — `sub_82430468` passes the literal into the
	/// parent position. A row whose parent is `Root`, empty, or a name nothing spawned is
	/// top-level.
	///
	/// A row may arrive before its parent, since spawn order is population order and `Add` fixes
	/// a parent at construction with no reparent. Rows therefore go in waves; spawn order
	/// survives within each wave.
	///
	/// The fold comes from <paramref name="open"/> — ours, keyed by path, as in the Lua rebuild.
	/// The guest's own `IsOpen` is not consulted on either path.
	/// </remarks>
	public static void Build(
		Engine_UiExpandableMenu script,
		IReadOnlyList<Row> rows,
		ICollection<string> open)
	{
		script.Clear();

		// Parents before children, resolved rather than assumed: rows go in waves, everything
		// whose parent is known (or is the root), until nothing new resolves. Spawn order is
		// preserved within each wave. A cycle or dangling parent stops the loop — a wave that
		// resolves nothing ends it and the remainder attaches at the root.
		var byName = new Dictionary<string, Engine_UiExpandableMenu.Item>(
			System.StringComparer.Ordinal);
		var pending = new List<Row>(rows);
		var placed = new List<(Row Row, Engine_UiExpandableMenu.Item Item)>(rows.Count);

		bool IsRoot(Row row) =>
			row.Parent.Length == 0 ||
			string.Equals(row.Parent, "Root", System.StringComparison.Ordinal);

		while (pending.Count != 0)
		{
			var next = new List<Row>();
			int placedThisWave = 0;

			foreach (Row row in pending)
			{
				Engine_UiExpandableMenu.Item? parent = null;
				if (!IsRoot(row) && !byName.TryGetValue(row.Parent, out parent))
				{
					next.Add(row);
					continue;
				}

				Engine_UiExpandableMenu.Item item = script.Add(row.Name, parent);
				byName[row.Name] = item;
				placed.Add((row, item));
				++placedThisWave;
			}

			if (placedThisWave == 0)
			{
				// Nothing resolved — the remainder name parents that were never spawned, or each
				// other. Attach them at the root and stop.
				foreach (Row row in next)
				{
					Engine_UiExpandableMenu.Item item = script.Add(row.Name, parent: null);
					byName[row.Name] = item;
					placed.Add((row, item));
				}

				break;
			}

			pending = next;
		}

		foreach ((Row row, Engine_UiExpandableMenu.Item item) in placed)
		{
			item.StatedLabel = row.Label;
			item.TextTag = row.Label;
			item.EventType = row.EventType;
			item.IdLow = (int)(row.Id & 0xFFFFu);
			item.IdHigh = (int)((row.Id >> 16) & 0xFFFFu);
			item.RatingSlide = row.Rating;

			// Committable because the capture named it — the same test the Lua path uses.
			item.Committable = true;

			// Unstated stays unstated; defaulting `Ghosted` to false here would be unsafe.
			item.Ghosted = row.Ghosted ?? false;
			item.Emphasis = row.Emphasis ?? Engine_UiMenuConstants.Emphasis.None;

			// The fold key is the path, not the name: a name is unique only among siblings.
			item.IsOpen = open.Contains(PathOf(item));
		}

		script.UpdateAfterSpawning();
		script.UpdateAllGhosting();
	}

	/// <summary>A row's fold path — the same `parent/child` key the Lua rebuild uses.</summary>
	public static string PathOf(Engine_UiExpandableMenu.Item item)
	{
		string path = item.Name;
		for (Engine_UiExpandableMenu.Item? at = item.Parent;
			at is not null && at.Parent is not null; at = at.Parent)
		{
			path = at.Name + "/" + path;
		}

		return path;
	}

	/// <summary>True when any row still lacks the two channels the capture cannot state. A screen
	/// may run off the capture alone only once this is false, or once its ghosting is known to
	/// come from somewhere the host owns.</summary>
	public static bool Unstated(IReadOnlyList<Row> rows)
	{
		foreach (Row row in rows)
		{
			if (row.Ghosted is null || row.Emphasis is null)
			{
				return true;
			}
		}

		return false;
	}
}
