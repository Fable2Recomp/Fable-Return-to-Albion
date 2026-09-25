using System.Collections.Generic;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// Every phase <see cref="Engine_FramePhases"/> measures — the same set Tracy's zones carry —
/// with its last, rolling-average and decaying-peak cost and its average allocation. Sorted by
/// average cost; click a column to sort by it.
/// </summary>
public partial class GameDevTabPhases : GameDevTab
{
	public override string Title => "Phases";

	private readonly GameDevTable _table =
		new(new[] { "phase", "last ms", "avg ms", "peak ms", "alloc KB" }, sortColumn: 2);

	public GameDevTabPhases()
	{
		Section("Measured phases (nested phases overlap — they do not sum)");
		AddChild(_table);
	}

	public override void Refresh()
	{
		var rows = new List<string[]>();
		foreach ((string name, object? value) in Engine_FramePhases.Report())
		{
			if (value is not Dictionary<string, object?> phase)
			{
				continue;
			}

			rows.Add(new[]
			{
				name,
				$"{phase["lastMs"]:0.00}",
				$"{phase["avgMs"]:0.00}",
				$"{phase["peakMs"]:0.00}",
				$"{phase["allocKB"]:0.0}",
			});
		}

		_table.Show(rows);
	}
}
