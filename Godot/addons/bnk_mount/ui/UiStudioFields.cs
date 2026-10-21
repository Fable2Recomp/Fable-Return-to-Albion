#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// A set of field rows bound to one JSON record the UI tab is editing: each row reads its key (a
/// dotted path for a nested object) when a record is shown and writes it back as it is edited,
/// removing the key when the row is emptied, so a field left alone stays exactly as the file had
/// it. Rows can be limited to the record kinds they apply to.
/// </summary>
public sealed class UiStudioFields
{
	public enum Type
	{
		Text,
		Number,
		Pair,
		Colour,
		Choice,
		List,
		Map,
	}

	private sealed record Row(string Key, Type Type, Control Line, string[]? Kinds, Action<string> Fill);

	private readonly Container _into;
	private readonly List<Row> _rows = new();
	private JsonObject? _record;
	private bool _filling;

	/// <summary>A row wrote the record.</summary>
	public event Action<string>? Changed;

	public UiStudioFields(Container into) => _into = into;

	public JsonObject? Record => _record;

	/// <summary>Adds a row for <paramref name="key"/>; <paramref name="kinds"/> limits it to those record kinds.</summary>
	public void Add(string key, string caption, Type type, string tip, string[]? kinds = null, string[]? choices = null)
	{
		Control editor;
		Action<string> fill;
		switch (type)
		{
			case Type.Pair:
			{
				var x = new LineEdit { PlaceholderText = "x", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				var y = new LineEdit { PlaceholderText = "y", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				var box = new HBoxContainer();
				box.AddChild(x);
				box.AddChild(y);
				x.TextChanged += _ => Wrote(key, type, $"{x.Text},{y.Text}");
				y.TextChanged += _ => Wrote(key, type, $"{x.Text},{y.Text}");
				editor = box;
				fill = text =>
				{
					string[] parts = text.Split(',');
					x.Text = parts.Length > 0 ? parts[0] : string.Empty;
					y.Text = parts.Length > 1 ? parts[1] : string.Empty;
				};
				break;
			}

			case Type.Colour:
			{
				var line = new LineEdit { PlaceholderText = "#rrggbbaa (empty: the default)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				var picker = new ColorPickerButton { CustomMinimumSize = new Vector2(36, 0), EditAlpha = true };
				line.TextChanged += text =>
				{
					if (Color.HtmlIsValid(text))
					{
						picker.Color = Color.FromHtml(text);
					}

					Wrote(key, type, text);
				};
				picker.ColorChanged += colour => line.Text = "#" + colour.ToHtml(true);
				picker.PopupClosed += () => Wrote(key, type, line.Text);
				var box = new HBoxContainer();
				box.AddChild(line);
				box.AddChild(picker);
				editor = box;
				fill = text =>
				{
					line.Text = text;
					if (Color.HtmlIsValid(text))
					{
						picker.Color = Color.FromHtml(text);
					}
				};
				break;
			}

			case Type.Choice:
			{
				var option = new OptionButton();
				option.AddItem("(default)");
				foreach (string choice in choices ?? Array.Empty<string>())
				{
					option.AddItem(choice);
				}

				option.ItemSelected += index => Wrote(key, type, index == 0 ? string.Empty : option.GetItemText((int)index));
				editor = option;
				fill = text =>
				{
					int at = 0;
					for (int i = 1; i < option.ItemCount; i++)
					{
						if (option.GetItemText(i) == text)
						{
							at = i;
						}
					}

					option.Select(at);
				};
				break;
			}

			default:
			{
				var line = new LineEdit
				{
					SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
					PlaceholderText = type switch
					{
						Type.List => "a, b, c",
						Type.Map => "name=source; name=source",
						Type.Number => "number (empty: the default)",
						_ => string.Empty,
					},
				};
				line.TextChanged += text => Wrote(key, type, text);
				editor = line;
				fill = text => line.Text = text;
				break;
			}
		}

		HBoxContainer row = ModStudioUiStyles.Field(caption, editor, tip);
		_into.AddChild(row);
		_rows.Add(new Row(key, type, row, kinds, fill));
	}

	/// <summary>Shows <paramref name="record"/> (null hides every row) for a record of <paramref name="kind"/>.</summary>
	public void Bind(JsonObject? record, string kind = "")
	{
		_record = record;
		_filling = true;
		foreach (Row row in _rows)
		{
			row.Line.Visible = record is not null && (row.Kinds is null || row.Kinds.Contains(kind));
			row.Fill(record is null ? string.Empty : Format(Get(record, row.Key), row.Type));
		}

		_filling = false;
	}

	private void Wrote(string key, Type type, string text)
	{
		if (_filling || _record is null)
		{
			return;
		}

		Set(_record, key, Parse(text, type));
		Changed?.Invoke(key);
	}

	// ---- the JSON side ------------------------------------------------------------------------

	private static JsonNode? Get(JsonObject record, string path)
	{
		JsonNode? node = record;
		foreach (string part in path.Split('.'))
		{
			node = node is JsonObject obj ? obj[part] : null;
		}

		return node;
	}

	/// <summary>Sets a dotted path; null removes it, and an object the removal empties goes too.</summary>
	public static void Set(JsonObject record, string path, JsonNode? value)
	{
		string[] parts = path.Split('.');
		JsonObject parent = record;
		for (int i = 0; i < parts.Length - 1; i++)
		{
			if (parent[parts[i]] is not JsonObject child)
			{
				if (value is null)
				{
					return;
				}

				child = new JsonObject();
				parent[parts[i]] = child;
			}

			parent = child;
		}

		if (value is null)
		{
			parent.Remove(parts[^1]);
			if (parts.Length > 1 && parent.Count == 0)
			{
				Set(record, string.Join('.', parts[..^1]), null);
			}
		}
		else
		{
			parent[parts[^1]] = value;
		}
	}

	private static string Format(JsonNode? node, Type type)
	{
		switch (node)
		{
			case null:
				return string.Empty;
			case JsonArray array when type == Type.Pair:
				return string.Join(",", array.Select(n => Number(n)));
			case JsonArray array:
				return string.Join(", ", array.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : n?.ToJsonString() ?? string.Empty));
			case JsonObject map when type == Type.Map:
				return string.Join("; ", map.Select(pair => $"{pair.Key}={(pair.Value is JsonValue v && v.TryGetValue(out string? s) ? s : pair.Value?.ToJsonString())}"));
			case JsonValue value when value.TryGetValue(out string? text):
				return text;
			default:
				return type == Type.Number ? Number(node) : node.ToJsonString();
		}
	}

	private static string Number(JsonNode? node) =>
		Engine_UiMenuEdit.NumberOf(node) is { } n ? n.ToString("0.###", CultureInfo.InvariantCulture) : node?.ToJsonString() ?? string.Empty;

	private static JsonNode? Parse(string text, Type type)
	{
		string trimmed = text.Trim();
		switch (type)
		{
			case Type.Number:
				return trimmed.Length == 0 ? null :
					double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? JsonValue.Create(n) :
					JsonValue.Create(trimmed);

			case Type.Pair:
				string[] parts = trimmed.Split(',');
				if (parts.All(p => p.Trim().Length == 0))
				{
					return null;
				}

				var pair = new JsonArray();
				foreach (string part in parts.Take(2))
				{
					pair.Add(double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
						? JsonValue.Create(v)
						: JsonValue.Create(0.0));
				}

				return pair;

			case Type.List:
				if (trimmed.Length == 0)
				{
					return null;
				}

				var list = new JsonArray();
				foreach (string item in trimmed.Split(',').Select(s => s.Trim()).Where(s => s.Length != 0))
				{
					list.Add(item);
				}

				return list;

			case Type.Map:
				if (trimmed.Length == 0)
				{
					return null;
				}

				var map = new JsonObject();
				foreach (string item in trimmed.Split(';').Select(s => s.Trim()).Where(s => s.Length != 0))
				{
					int eq = item.IndexOf('=');
					if (eq > 0)
					{
						map[item[..eq].Trim()] = item[(eq + 1)..].Trim();
					}
				}

				return map;

			default:
				return trimmed.Length == 0 ? null : JsonValue.Create(text);
		}
	}
}
#endif
