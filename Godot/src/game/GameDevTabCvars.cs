using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The runtime cvars: every one the registry holds, filtered by name or category; pick one to
/// set its value. The registry's own validator answers — lifecycle, range, allowed values — so a
/// refused value says so rather than half-applying.
/// </summary>
public partial class GameDevTabCvars : GameDevTab
{
	public override string Title => "CVars";

	public override double RefreshSeconds => 1.0;

	/// <summary>The registry's type codes, as the debug channel names them.</summary>
	private static readonly string[] Types =
	{
		"bool", "int32", "int64", "uint32", "uint64", "double", "string", "command",
	};
	private static readonly string[] Lifecycles = { "init-only", "hot-reload", "requires-restart" };

	private readonly LineEdit _filter = new()
	{
		PlaceholderText = "filter by name or category…",
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};

	private readonly GameDevTable _table = new(
		new[] { "cvar", "value", "default", "category", "type", "lifecycle", "modified" });

	private readonly Label _picked = new() { Text = "pick a cvar to edit it" };
	private readonly LineEdit _value = new() { CustomMinimumSize = new Vector2(260, 0) };
	private readonly Button _set = new() { Text = "Set" };
	private readonly Label _result = new();
	private string _name = string.Empty;

	public GameDevTabCvars()
	{
		AddChild(_filter);
		AddChild(_table);

		var edit = new HBoxContainer();
		edit.AddChild(_picked);
		edit.AddChild(_value);
		edit.AddChild(_set);
		edit.AddChild(_result);
		AddChild(edit);

		_table.Picked += row =>
		{
			_name = row[0];
			_picked.Text = $"{_name} =";
			_value.Text = row[1];
			_result.Text = $"{row[5]}, {row[4]}";
		};
		_set.Pressed += Apply;
		_value.TextSubmitted += _ => Apply();
	}

	private void Apply()
	{
		if (_name.Length == 0)
		{
			return;
		}

		bool nativeAccepted = HostBridge.SetCvar(_name, _value.Text);
		bool godotAccepted = Refii.Core.Engine_RefiiConfig.TrySetGodotCvar(_name, _value.Text);
		bool accepted = nativeAccepted || godotAccepted;
		_result.Text = accepted ? "accepted" : "REFUSED by the registry";
		GD.Print($"[dev-menu] cvar {_name} = '{_value.Text}' — {(accepted ? "accepted" : "refused")}");
		Refresh();
	}

	public override void Refresh()
	{
		string filter = _filter.Text;
		var rows = new List<string[]>();
		int count = HostBridge.GetCvarCount();
		for (int i = 0; i < count; ++i)
		{
			if (HostBridge.GetCvar(i) is not { } info)
			{
				continue;
			}

			if (filter.Length != 0 &&
				!info.Name.Contains(filter, System.StringComparison.OrdinalIgnoreCase) &&
				!info.Category.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			rows.Add(new[]
			{
				info.Name, info.Value, info.Default, info.Category,
				info.Type < (uint)Types.Length ? Types[info.Type] : info.Type.ToString(),
				info.Lifecycle < (uint)Lifecycles.Length ? Lifecycles[info.Lifecycle] : info.Lifecycle.ToString(),
				info.Modified ? "yes" : string.Empty,
			});
		}

		foreach ((Refii.Launcher.Core.RuntimeCVarDefinition def, string val, bool modified) in
			Refii.Core.Engine_RefiiConfig.GetGodotCvars())
		{
			if (filter.Length != 0 &&
				!def.Name.Contains(filter, System.StringComparison.OrdinalIgnoreCase) &&
				!def.Category.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string typeName = def.Type switch
			{
				Refii.Launcher.Core.RuntimeCVarType.Bool => "bool",
				Refii.Launcher.Core.RuntimeCVarType.Int => "int32",
				Refii.Launcher.Core.RuntimeCVarType.Double => "double",
				_ => "string",
			};
			rows.Add(new[]
			{
				def.Name, val, def.DefaultValue, def.Category,
				typeName, "hot-reload", modified ? "yes" : string.Empty,
			});
		}

		_table.Show(rows);
	}
}
