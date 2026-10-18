#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Refii.EditorTools;

namespace Refii.Assets.UI;

/// <summary>
/// The Mod Studio's view of the RUNNING game, through its debug channel
/// (<see cref="LiveGameChannel"/>): whether a game is up, the two reloads (the mods folded in
/// again, and the same with the mods' Lua run again), and the mods' Lua errors as the game
/// reports them, newest first. Every call to the game runs off the editor's main thread.
/// </summary>
public partial class LiveGameView : VBoxContainer
{
	/// <summary>How often the handshake is checked.</summary>
	private const double StatusSeconds = 2.0;

	/// <summary>How often a running game is asked for new errors.</summary>
	private const double ErrorSeconds = 1.0;

	/// <summary>The most errors the list shows.</summary>
	private const int MaxShown = 500;

	private const string NoGameTooltip =
		"No game is running with its debug channel. Launch the game from the launcher with " +
		"debug_channel = true in the package config.";

	private readonly Label _status = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
	private readonly Button _reload = new() { Text = "↻ Reload mods in game" };
	private readonly Button _reloadLua = new() { Text = "↻ Reload mods + Lua" };
	private readonly Label _result = new()
	{
		AutowrapMode = TextServer.AutowrapMode.WordSmart,
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};
	private readonly Label _errorsTitle = new() { Text = "Lua errors (newest first)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
	private readonly ItemList _errors = new()
	{
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
		SizeFlagsVertical = SizeFlags.ExpandFill,
		CustomMinimumSize = new Vector2(0, 160),
		SelectMode = ItemList.SelectModeEnum.Single,
	};

	/// <summary>The errors on show, oldest first.</summary>
	private readonly List<LiveGameChannel.ModError> _shown = new();

	private bool _running;
	private string _boot = string.Empty;
	private long _since;
	private bool _polling;
	private bool _reloading;

	public LiveGameView()
	{
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		SizeFlagsVertical = SizeFlags.ExpandFill;
		AddThemeConstantOverride("separation", 8);

		var statusRow = new HBoxContainer();
		statusRow.AddChild(_status);
		AddChild(statusRow);

		var buttons = new HBoxContainer();
		buttons.AddThemeConstantOverride("separation", 8);
		_reload.Pressed += () => Reload(lua: false);
		_reloadLua.Pressed += () => Reload(lua: true);
		buttons.AddChild(_reload);
		buttons.AddChild(_reloadLua);
		AddChild(buttons);

		AddChild(_result);
		AddChild(new HSeparator());

		var errorsRow = new HBoxContainer();
		errorsRow.AddChild(_errorsTitle);
		var clear = new Button
		{
			Text = "Clear",
			TooltipText = "Empties this list; the game keeps its own record",
		};
		clear.Pressed += () =>
		{
			_shown.Clear();
			ShowErrors();
		};
		errorsRow.AddChild(clear);
		AddChild(errorsRow);
		AddChild(_errors);

		var statusTimer = new Timer { WaitTime = StatusSeconds, Autostart = true };
		statusTimer.Timeout += PollStatus;
		AddChild(statusTimer);

		var errorTimer = new Timer { WaitTime = ErrorSeconds, Autostart = true };
		errorTimer.Timeout += PollErrors;
		AddChild(errorTimer);

		PollStatus();
	}

	/// <summary>Whether a game publishes its channel; a new run starts the error list over.</summary>
	private void PollStatus()
	{
		LiveGameChannel.Handshake? handshake = LiveGameChannel.ReadHandshake();
		_running = handshake is not null && LiveGameChannel.IsAlive(handshake.Pid);
		if (_running && handshake!.Boot != _boot)
		{
			_boot = handshake.Boot;
			_since = 0;
			_shown.Clear();
			ShowErrors();
		}

		_status.Text = _running
			? $"● Game running (pid {handshake!.Pid})"
			: "○ No game running";
		_status.Modulate = _running ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.70f, 0.72f, 0.76f);
		UpdateButtons();
	}

	private void UpdateButtons()
	{
		bool usable = _running && !_reloading;
		foreach (Button button in new[] { _reload, _reloadLua })
		{
			button.Disabled = !usable;
		}

		_reload.TooltipText = !_running
			? NoGameTooltip
			: "Folds the installed mods into the running game again: textures, text, HUD pieces, " +
			  "menus and templates, FX recipes, spells, settings, input bindings and Godot scripts";
		_reloadLua.TooltipText = !_running
			? NoGameTooltip
			: "Reloads the mods, then runs every mod's init.lua again in the game, its earlier event " +
			  "handlers dropped first";
	}

	private void PollErrors()
	{
		if (!_running || _polling)
		{
			return;
		}

		_polling = true;
		long since = _since;
		Task.Run(() =>
		{
			List<LiveGameChannel.ModError>? errors = LiveGameChannel.ModErrors(since, out long latest, out _);
			Callable.From(() => TakeErrors(errors, latest)).CallDeferred();
		});
	}

	private void TakeErrors(List<LiveGameChannel.ModError>? errors, long latest)
	{
		if (!GodotObject.IsInstanceValid(this))
		{
			return;
		}

		_polling = false;
		if (errors is null)
		{
			return;
		}

		_since = Math.Max(_since, latest);
		if (errors.Count == 0)
		{
			return;
		}

		_shown.AddRange(errors);
		if (_shown.Count > MaxShown)
		{
			_shown.RemoveRange(0, _shown.Count - MaxShown);
		}

		ShowErrors();
	}

	private void ShowErrors()
	{
		_errors.Clear();
		for (int i = _shown.Count - 1; i >= 0; --i)
		{
			LiveGameChannel.ModError error = _shown[i];
			string firstLine = error.Text.Split('\n')[0];
			int index = _errors.AddItem($"{error.Time.ToLocalTime():HH:mm:ss}  {error.Mod} — {firstLine}");
			_errors.SetItemTooltip(index, $"{error.Mod}\n{error.Text}");
			_errors.SetItemCustomFgColor(index, new Color(1.0f, 0.62f, 0.55f));
		}

		_errorsTitle.Text = _shown.Count == 0
			? "Lua errors (newest first)"
			: $"Lua errors (newest first) — {_shown.Count}";
	}

	private void Reload(bool lua)
	{
		if (!_running || _reloading)
		{
			return;
		}

		_reloading = true;
		UpdateButtons();
		_result.Text = lua ? "Reloading mods and Lua in the game…" : "Reloading mods in the game…";
		_result.Modulate = new Color(0.80f, 0.82f, 0.86f);
		Task.Run(() =>
		{
			LiveGameChannel.RemountResult result = LiveGameChannel.Remount(lua);
			Callable.From(() => TakeReload(result)).CallDeferred();
		});
	}

	private void TakeReload(LiveGameChannel.RemountResult result)
	{
		if (!GodotObject.IsInstanceValid(this))
		{
			return;
		}

		_reloading = false;
		UpdateButtons();
		if (!result.Ok)
		{
			_result.Text = "Reload failed: " + result.Error;
			_result.Modulate = new Color(1.0f, 0.62f, 0.55f);
			return;
		}

		var lines = new List<string>
		{
			result.Mods.Count == 0
				? "Reloaded — no mods are enabled in the game."
				: $"Reloaded {result.Mods.Count} mod(s): {string.Join(", ", result.Mods)} — {result.Changed} changed file(s).",
		};

		string studioRoot = EditorModValueService.ModsRootOverride ?? Engine_GameAssets.ModsRoot;
		if (result.ModsRoot.Length != 0 && !SamePath(result.ModsRoot, studioRoot))
		{
			lines.Add($"⚠ The game reads mods from {result.ModsRoot}, not the Studio's {studioRoot}: " +
				"changes saved here do not reach it.");
		}

		if (result.Warning.Length != 0)
		{
			lines.Add("⚠ " + result.Warning);
		}

		if (result.LuaRan is { } ran)
		{
			lines.Add((ran ? "Lua: ran. " : "Lua: ") + result.LuaNote);
		}

		foreach (string relaunch in result.Relaunch)
		{
			lines.Add("Needs a relaunch — " + relaunch);
		}

		lines.Add("Live now: " + string.Join("; ", result.Refreshed));
		_result.Text = string.Join("\n", lines);
		_result.Modulate = result.LuaRan == false || result.Relaunch.Count != 0
			? new Color(1.0f, 0.85f, 0.55f)
			: new Color(0.55f, 0.85f, 0.55f);
	}

	private static bool SamePath(string a, string b)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
				Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
				OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		}
		catch (Exception)
		{
			return string.Equals(a, b, StringComparison.Ordinal);
		}
	}
}
#endif
