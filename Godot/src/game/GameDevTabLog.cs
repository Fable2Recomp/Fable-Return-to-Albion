using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// The live logs, tailed in place: Godot's own (`user://logs/godot.log`) and the embedded guest's
/// newest segment beside the executable (`exe_dir/logs/refii_*.log*` — NOT the usual location,
/// see CLAUDE.md). Read incrementally from the last position, so an open tab costs only what was
/// written since; a filter keeps the lines containing it.
/// </summary>
public partial class GameDevTabLog : GameDevTab
{
	public override string Title => "Log";

	private const int MaxLines = 600;

	private readonly OptionButton _source = new();
	private readonly LineEdit _filter = new()
	{
		PlaceholderText = "filter (case-insensitive)…",
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};
	private readonly CheckBox _follow = new() { Text = "follow", ButtonPressed = true };
	private readonly TextEdit _view = new()
	{
		Editable = false,
		SizeFlagsVertical = SizeFlags.ExpandFill,
		WrapMode = TextEdit.LineWrappingMode.None,
	};

	private readonly List<string> _lines = new();
	private string _path = string.Empty;
	private long _position;
	private string _shownFilter = string.Empty;
	private bool _dirty;

	public GameDevTabLog()
	{
		_source.AddItem("Godot log");
		_source.AddItem("Guest log");
		_source.ItemSelected += _ => Reset();
		_view.AddThemeFontOverride("font", new SystemFont { FontNames = new[] { "monospace" } });
		_view.AddThemeFontSizeOverride("font_size", 13);

		var bar = new HBoxContainer();
		bar.AddChild(_source);
		bar.AddChild(_filter);
		bar.AddChild(_follow);
		AddChild(bar);
		AddChild(_view);
	}

	private void Reset()
	{
		_path = string.Empty;
		_position = 0;
		_lines.Clear();
		_dirty = true;
	}

	public override void Refresh()
	{
		string path = _source.Selected == 0
			? ProjectSettings.GlobalizePath("user://logs/godot.log")
			: NewestGuestLog();
		if (path != _path)
		{
			Reset();
			_path = path;
			// Start near the end: a log is often megabytes, and only its tail is wanted.
			try
			{
				long length = new System.IO.FileInfo(path).Length;
				_position = System.Math.Max(0, length - (256 * 1024));
			}
			catch (System.Exception)
			{
				_position = 0;
			}
		}

		ReadNew();
		if (_dirty || _filter.Text != _shownFilter)
		{
			Render();
		}
	}

	private void ReadNew()
	{
		if (_path.Length == 0)
		{
			return;
		}

		try
		{
			using var stream = new System.IO.FileStream(_path, System.IO.FileMode.Open,
				System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
			if (stream.Length < _position)
			{
				// Rotated or truncated: start again from its head.
				_position = 0;
				_lines.Clear();
			}

			if (stream.Length == _position)
			{
				return;
			}

			stream.Seek(_position, System.IO.SeekOrigin.Begin);
			using var reader = new System.IO.StreamReader(stream);
			bool first = _position != 0 && _lines.Count == 0;
			while (reader.ReadLine() is { } line)
			{
				// The first line after a mid-file seek is usually cut; drop it.
				if (first)
				{
					first = false;
					continue;
				}

				_lines.Add(line);
			}

			_position = stream.Length;
			if (_lines.Count > MaxLines * 4)
			{
				_lines.RemoveRange(0, _lines.Count - (MaxLines * 2));
			}

			_dirty = true;
		}
		catch (System.Exception error)
		{
			_lines.Add($"[dev-menu] cannot read {_path}: {error.Message}");
			_dirty = true;
		}
	}

	private void Render()
	{
		_dirty = false;
		_shownFilter = _filter.Text;
		var shown = new List<string>(MaxLines);
		for (int i = _lines.Count - 1; i >= 0 && shown.Count < MaxLines; --i)
		{
			if (_shownFilter.Length == 0 ||
				_lines[i].Contains(_shownFilter, System.StringComparison.OrdinalIgnoreCase))
			{
				shown.Add(_lines[i]);
			}
		}

		shown.Reverse();
		int scroll = _view.GetVScrollBar() is { } bar ? (int)bar.Value : 0;
		_view.Text = string.Join('\n', shown);
		if (_follow.ButtonPressed)
		{
			_view.ScrollVertical = _view.GetLineCount();
		}
		else
		{
			_view.ScrollVertical = scroll;
		}
	}

	/// <summary>The newest guest log segment beside the executable, or empty.</summary>
	private static string NewestGuestLog()
	{
		try
		{
			string logs = System.IO.Path.Combine(
				System.IO.Path.GetDirectoryName(OS.GetExecutablePath())!, "logs");
			string newest = string.Empty;
			System.DateTime when = System.DateTime.MinValue;
			foreach (string file in System.IO.Directory.GetFiles(logs, "refii_*.log*"))
			{
				System.DateTime written = System.IO.File.GetLastWriteTimeUtc(file);
				if (written > when)
				{
					when = written;
					newest = file;
				}
			}

			return newest;
		}
		catch (System.Exception)
		{
			return string.Empty;
		}
	}
}
