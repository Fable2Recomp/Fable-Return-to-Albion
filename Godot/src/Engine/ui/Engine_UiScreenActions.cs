namespace Refii.Ui;

/// <summary>
/// The actions a screen template can name for a button, and the one place that runs them.
///
/// THE ONE-OFF PANELS ANSWER A DIFFERENT VOCABULARY, and that used to be a chain of screen-id
/// comparisons in `GameUi`: the query panel closes on Back, the repeatable-quest card dismisses
/// its box on Accept, minigame-completed and quest-completion dismiss the screen. Each was a
/// branch, so each could be wrong on its own. Naming the action in the template puts them on one
/// path — a screen states which button does what, and this runs it.
/// </summary>
public static class Engine_UiScreenActions
{
	public const string SettingToggle = "setting:toggle:";

	public const string SettingSet = "setting:set:";

	public const string SettingCycle = "setting:cycle:";

	public const string WeatherOverrideKey = "dynamic_sky_weather";

	public const string WeatherOverrideValues =
		"theme,clear,partlycloudy,cloudy,overcast,rain,storm,snow,fog,lightrain,lightsnow,blizzard";

	private static readonly System.Collections.Generic.Dictionary<string, string> SettingTitles =
		new(System.StringComparer.Ordinal)
		{
			[Refii.Level.Engine_DynamicSky.SwitchKey] = "Enable/Disable",
			[WeatherOverrideKey] = "Weather Override",
			[Refii.Level.Engine_DynamicNightSky.TimeKey] = "Time of Day Override",
			[Refii.Level.Engine_DynamicNightSky.AuroraKey] = "Aurora",
			[Refii.Level.Engine_DynamicSurfaceWeather.SwitchKey] = "Surface Weather",
			[Refii.Level.Engine_DynamicSky.VolumetricCloudsKey] = "Volumetric Clouds",
			[Refii.Level.Engine_DynamicSky.CloudShadowsKey] = "Cloud Shadows",
		};

	private static readonly System.Collections.Generic.Dictionary<string, string> ValueTitles =
		new(System.StringComparer.Ordinal)
		{
			["theme"] = "Auto",
			["clear"] = "Clear",
			["partlycloudy"] = "Partly Cloudy",
			["cloudy"] = "Cloudy",
			["overcast"] = "Overcast",
			["rain"] = "Rain",
			["storm"] = "Storm",
			["snow"] = "Snow",
			["fog"] = "Fog",
		};

	public static void RefreshSettingLabel(Engine_UiExpandableMenu.Item item)
	{
		if (item.Command is not { } command)
		{
			return;
		}

		if (command.StartsWith(SettingToggle, System.StringComparison.Ordinal))
		{
			string key = command[SettingToggle.Length..];
			string title = SettingTitles.TryGetValue(key, out string? known) ? known : key;
			bool on = Refii.Core.Engine_Switches.Flag(key, false);
			item.StatedLabel = $"{title}: {(on ? "On" : "Off")}";
			return;
		}

		if (command.StartsWith(SettingCycle, System.StringComparison.Ordinal) &&
			SplitCycle(command, out string cycleKey, out string[] values))
		{
			string title = SettingTitles.TryGetValue(cycleKey, out string? known) ? known : cycleKey;
			string current = Refii.Core.Engine_Switches.String(cycleKey, values[0]).Trim().ToLowerInvariant();
			string shown = ValueTitles.TryGetValue(current, out string? pretty) ? pretty : current;
			item.StatedLabel = $"{title}: {shown}";
		}
	}

	private static bool SplitCycle(string command, out string key, out string[] values)
	{
		string body = command[SettingCycle.Length..];
		int colon = body.IndexOf(':');
		key = colon > 0 ? body[..colon] : string.Empty;
		values = colon > 0
			? body[(colon + 1)..].Split(',', System.StringSplitOptions.RemoveEmptyEntries)
			: System.Array.Empty<string>();
		return key.Length != 0 && values.Length != 0;
	}

	private static bool CycleSetting(string command)
	{
		if (!SplitCycle(command, out string key, out string[] values))
		{
			return false;
		}

		string current = Refii.Core.Engine_Switches.String(key, values[0]).Trim().ToLowerInvariant();
		int at = System.Array.IndexOf(values, current);
		string next = values[(at + 1) % values.Length];
		return WriteSetting(key, next);
	}

	private static bool ToggleSetting(string key)
	{
		string path = Refii.Core.Engine_AppRoot.ConfigPath;
		System.Collections.Generic.SortedDictionary<string, string> values =
			Refii.Launcher.Core.RuntimeConfig.ReadValues(path);
		bool on = values.TryGetValue(key, out string? current) &&
			current.Trim().Trim('"') is "true" or "1";
		return WriteSetting(key, on ? "false" : "true");
	}

	private static bool WriteSetting(string key, string value)
		=> WriteSettings(new System.Collections.Generic.Dictionary<string, string> { [key] = value });

	public static bool WriteSettings(System.Collections.Generic.IReadOnlyDictionary<string, string> settings)
	{
		string path = Refii.Core.Engine_AppRoot.ConfigPath;
		System.Collections.Generic.SortedDictionary<string, string> values =
			Refii.Launcher.Core.RuntimeConfig.ReadValues(path);
		foreach (var pair in settings) values[pair.Key] = pair.Value;
		if (!Refii.Launcher.Core.RuntimeConfig.WriteValues(path, values, out string error))
		{
			Godot.GD.Print($"[screen-actions] could not write settings to {path}: {error}");
			return false;
		}

		Refii.Core.Engine_Switches.Invalidate();
		Godot.GD.Print($"[screen-actions] settings written to {path}");
		return true;
	}

	/// <summary>Closes the screen through the guest's own back lever.</summary>
	public const string ScreenBack = "screen:back";

	/// <summary>Dismisses the screen — the one-off panels' own close.</summary>
	public const string ScreenDismiss = "screen:dismiss";

	/// <summary>Unlearns the highlighted ability, then commits the row.</summary>
	public const string ScreenUnlearn = "screen:unlearn";

	/// <summary>
	/// A on the screen itself — for a list whose rows the screen does NOT answer by identity.
	///
	/// The furniture screens are the case: their rows carry event type 0, and the press handler
	/// at `vtable+20` (`sub_826B65F0`) has cases for 140 and 141 and falls out of everything
	/// else, so an identity push does nothing at all. The commit lives in the INPUT handler on
	/// code 36, which reads the highlighted row out of Lua
	/// (`sub_826C1B50` -&gt; `g_Menu:GetHighlightedMenuItemData()`) rather than off the event —
	/// so the press has to be the press.
	/// </summary>
	public const string ScreenSelect = "screen:select";

	/// <summary>
	/// Dismisses the display box rather than the screen. The repeatable-quest card has no screen
	/// class at all: it is the quest box (`sub_823D0090`), which answers 36 through the box
	/// queue's own lever.
	/// </summary>
	public const string BoxDismiss = "box:dismiss";

	/// <summary>Runs a named action. An empty or unknown name does nothing.</summary>
	public static bool Run(string action)
	{
		// A HOST OPTIONS PAGE — the dynamic sky's switches, a mod's settings: the row opens the
		// page the registry maps its command to, on the options screen's own furniture.
		if (Engine_UiHostOptionsPage.Run(action))
		{
			return true;
		}

		if (action.StartsWith(SettingToggle, System.StringComparison.Ordinal))
		{
			return ToggleSetting(action[SettingToggle.Length..]);
		}

		if (action.StartsWith(SettingCycle, System.StringComparison.Ordinal))
		{
			return CycleSetting(action);
		}

		if (action.StartsWith(SettingSet, System.StringComparison.Ordinal))
		{
			string assignment = action[SettingSet.Length..];
			int equals = assignment.IndexOf('=');
			return equals > 0 && WriteSetting(assignment[..equals], assignment[(equals + 1)..]);
		}

		// `Inventory:<op>:<record hash>` — an operation on the SIMULATION, posted to the guest's
		// tick pump because guest code must run on the guest's thread. It reaches
		// `AddAndReplaceItemOfRecordID` / `Player:GiveGift` directly: no GUI event queue, no
		// screen class, no populated Gameface menu. See `DispatchInventoryAction`.
		if (action.StartsWith(Engine_UiInventorySource.InventoryAction,
				System.StringComparison.Ordinal))
		{
			// A dye is CHOSEN, not used: the press opens the dye list and the simulation is
			// untouched until a target is picked.
			if (action.StartsWith(Engine_UiDyeFlow.SelectAction, System.StringComparison.Ordinal))
			{
				if (uint.TryParse(action[Engine_UiDyeFlow.SelectAction.Length..], out uint dye))
				{
					Engine_UiDyeFlow.Begin(dye);
				}

				return true;
			}

			// A joiner's weapon pick changes the record; the host equips it. The id is decimal.
			const string equip = Engine_UiInventorySource.InventoryAction + "equip:";
			if (Refii.Host.CoopSession.IsClient && action.StartsWith(equip, System.StringComparison.Ordinal) &&
				uint.TryParse(action[equip.Length..], out uint record) &&
				Refii.Host.CoopHeroKit.SetLocalWeapon(record, Engine_UiInventorySource.WeaponClassOf(record)))
			{
				return true;
			}

			Refii.Host.HostBridge.PostFrontEndAction(action);
			return true;
		}

		// `Quest:select:<record>` — the world map's quest rows. The pump turns the record into
		// the guest's own row ordinal and delivers event 42 to screen 23, whose handler asks
		// "make this your primary quest?" and commits through `QuestTracker.SetAsPrimary`.
		if (action.StartsWith(Engine_UiQuestSource.QuestAction, System.StringComparison.Ordinal) ||
			action.StartsWith(Engine_UiQuestSource.SaleAction, System.StringComparison.Ordinal))
		{
			Refii.Host.HostBridge.PostFrontEndAction(action);
			return true;
		}

		// `Ability:learn:<1..14>` — the same arrangement for the abilities screen. The guest's
		// own answer to one of those rows is a single two-argument call that reads nothing from
		// the screen or the menu (`sub_8246C970` / `sub_8246CF48`), so the row performs it
		// directly rather than borrowing an identity and hoping the guest's cursor agrees.
		if (action.StartsWith(Engine_UiAbilitySource.AbilityAction,
				System.StringComparison.Ordinal))
		{
			Refii.Host.HostBridge.PostFrontEndAction(action);
			return true;
		}

		// `PropertyRepair:<uid>` — the world map's property rows: confirmed, then repaired for the
		// player whose menus are open (Engine_UiPropertySource).
		if (Engine_UiPropertySource.Run(action))
		{
			return true;
		}

		// `Mod:buy:<setting>` — a mod ability's row: the purchase is the mod's (Engine_UiModAbilities).
		if (Engine_UiModAbilities.Buy(action))
		{
			return true;
		}

		switch (action)
		{
			case ScreenBack:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Back);
				return true;

			case ScreenDismiss:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Dismiss);
				return true;

			case ScreenUnlearn:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Unlearn);
				return true;

			case ScreenSelect:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Select);
				return true;

			case BoxDismiss:
				Engine_UiSource.Current.DisplayBoxAction(Engine_UiSource.BoxAction.Dismiss);
				return true;

			default:
				return false;
		}
	}

	/// <summary>Whether this screen takes input directly once revealed.</summary>
	public static bool OwnsRevealedInput(uint screenId) =>
		Engine_UiTemplates.Screen(screenId)?.OwnsRevealedInput ?? false;

	/// <summary>
	/// Runs whichever of this screen's revealed-input actions the pressed button names.
	///
	/// Accept is checked before Back because no shipped screen states both, and a screen that
	/// stated both would want the affirmative to win.
	/// </summary>
	public static void RunRevealed(uint screenId, bool accept, bool back)
	{
		if (Engine_UiTemplates.Screen(screenId) is not { } screen)
		{
			return;
		}

		if (accept && screen.RevealedAccept.Length != 0)
		{
			Run(screen.RevealedAccept);
			return;
		}

		if (back && screen.RevealedBack.Length != 0)
		{
			Run(screen.RevealedBack);
		}
	}
}
