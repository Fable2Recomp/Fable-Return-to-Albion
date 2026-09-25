using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// Godot's errors and warnings, as the editor debugger's Errors panel shows them: each distinct
/// error (by where it came from and what it said) with how often and when it last fired; pick
/// one for its source, code and script backtrace. Captured from startup by
/// <see cref="GameDevErrorLog"/>, whether or not the menu is open.
/// </summary>
public partial class GameDevTabErrors : GameDevTab
{
	public override string Title => "Errors";

	public override double RefreshSeconds => 1.0;

	private readonly GameDevTable _table =
		new(new[] { "message", "kind", "count", "last s", "where" }, sortColumn: 3);

	private readonly Label _summary = new();
	private readonly TextEdit _detail = new()
	{
		Editable = false,
		CustomMinimumSize = new Vector2(0, 170),
		WrapMode = TextEdit.LineWrappingMode.Boundary,
	};

	private string _picked = string.Empty;

	public GameDevTabErrors()
	{
		var bar = new HBoxContainer();
		_summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		bar.AddChild(_summary);
		var clear = new Button { Text = "Clear" };
		clear.Pressed += () =>
		{
			GameDevErrorLog.Clear();
			_picked = string.Empty;
			_detail.Text = string.Empty;
			Refresh();
		};
		bar.AddChild(clear);
		AddChild(bar);
		AddChild(_table);
		AddChild(_detail);

		_table.Picked += row =>
		{
			_picked = row[0];
			ShowDetail();
		};
	}

	public override void Refresh()
	{
		var rows = new List<string[]>();
		double now = Time.GetTicksMsec() / 1000.0;
		int total = 0;
		foreach (GameDevErrorLog.Entry entry in GameDevErrorLog.Snapshot())
		{
			total += entry.Count;
			rows.Add(new[]
			{
				entry.Message, entry.Kind, entry.Count.ToString(),
				$"{now - entry.LastSeconds:0}", entry.Where,
			});
		}

		_summary.Text = $"{rows.Count} distinct, {total} in all, since startup   (last s = seconds ago)";
		_table.Show(rows);
		if (_picked.Length != 0)
		{
			ShowDetail();
		}
	}

	private void ShowDetail()
	{
		foreach (GameDevErrorLog.Entry entry in GameDevErrorLog.Snapshot())
		{
			if (entry.Message == _picked)
			{
				_detail.Text = $"{entry.Kind}  x{entry.Count}\n{entry.Where}\n{entry.Code}\n\n{entry.Backtrace}";
				return;
			}
		}
	}
}

/// <summary>
/// Collects Godot's errors and warnings from startup (<see cref="OS.AddLogger"/>). The engine
/// calls a logger from whichever thread raised the error, so every access is locked; entries are
/// grouped by source and message and capped.
/// </summary>
public partial class GameDevErrorLog : Logger
{
	public sealed class Entry
	{
		public required string Message { get; init; }
		public required string Kind { get; init; }
		public required string Where { get; init; }
		public required string Code { get; init; }
		public string Backtrace { get; set; } = string.Empty;
		public int Count { get; set; }
		public double LastSeconds { get; set; }
	}

	private const int MaxEntries = 500;

	private static readonly object Gate = new();
	private static readonly Dictionary<string, Entry> Entries = new();
	private static GameDevErrorLog? _installed;

	/// <summary>Registers the logger once for the process.</summary>
	public static void Install()
	{
		if (_installed is not null)
		{
			return;
		}

		_installed = new GameDevErrorLog();
		OS.AddLogger(_installed);
	}

	public static List<Entry> Snapshot()
	{
		lock (Gate)
		{
			return new List<Entry>(Entries.Values);
		}
	}

	public static void Clear()
	{
		lock (Gate)
		{
			Entries.Clear();
		}
	}

	public override void _LogError(string function, string file, int line, string code,
		string rationale, bool editorNotify, int errorType,
		Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
	{
		string kind = (ErrorType)errorType switch
		{
			ErrorType.Warning => "warning",
			ErrorType.Script => "script",
			ErrorType.Shader => "shader",
			_ => "error",
		};
		string message = rationale.Length != 0 ? rationale : code;
		string where = $"{file}:{line} {function}";
		string key = $"{where}|{message}";
		double now = Time.GetTicksMsec() / 1000.0;

		lock (Gate)
		{
			if (!Entries.TryGetValue(key, out Entry? entry))
			{
				if (Entries.Count >= MaxEntries)
				{
					return;
				}

				var trace = new System.Text.StringBuilder();
				foreach (ScriptBacktrace backtrace in scriptBacktraces)
				{
					trace.Append(backtrace.Format());
				}

				entry = new Entry
				{
					Message = message, Kind = kind, Where = where, Code = code,
					Backtrace = trace.ToString(),
				};
				Entries[key] = entry;
			}

			entry.Count++;
			entry.LastSeconds = now;
		}
	}
}
