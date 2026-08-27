namespace Refii.Ui;

/// <summary>
/// `qbeventtranslator.lua` and `uicontracteventslib.lua`, translated. `Scene.UIContract` is an
/// authored element every screen carries, and everything crossing between the game and a
/// presentation goes through it in two directions:
///
/// <list type="bullet">
/// <item>Inward: `qbeventtranslator.lua` registers for the four abstract events and fires the
/// concrete press on the same contract — `onSelect`→`OnPressA`, `onBack`→`OnPressB`,
/// `onUp`/`onDown`→`OnPressUp`/`OnPressDown`.</item>
/// <item>Outward: `uicontracteventslib.lua` publishes six global functions C++ fires by name,
/// each reading its arguments off `g_Param`.</item>
/// </list>
///
/// `OnPressA` is code 36 (`sub_826D58F0`) and `OnPressB` 37 — see
/// <see cref="Engine_UiScreenInput"/> — so `ScreenAction(36)` enters at the concrete end.
/// </summary>
public static class Engine_UiContract
{
	/// <summary>The authored element every screen carries.</summary>
	public const string ElementPath = "Scene.UIContract";

	/// <summary>
	/// The abstract events and the concrete press each translates into — from
	/// `qbeventtranslator.lua`'s `onInitialize`, four `registerForEvent` calls each firing a
	/// single `fireEvent('OnPressX', self.UIContract)`.
	/// </summary>
	public static readonly (string Abstract, string Concrete)[] PressTranslation =
	{
		("onSelect", "OnPressA"),
		("onBack", "OnPressB"),
		("onUp", "OnPressUp"),
		("onDown", "OnPressDown"),
	};

	/// <summary>The concrete press an abstract contract event fires, or null.</summary>
	public static string? ConcreteFor(string abstractEvent)
	{
		foreach ((string a, string concrete) in PressTranslation)
		{
			if (string.Equals(a, abstractEvent, System.StringComparison.Ordinal))
			{
				return concrete;
			}
		}

		return null;
	}

	/// <summary>
	/// The six global entry points `uicontracteventslib.lua` publishes for C++ to fire by name.
	/// Each reads its arguments off `g_Param` (`String1`/`String2`) and asserts on a missing
	/// manager or book rather than failing quietly.
	/// </summary>
	public static readonly string[] NamedEvents =
	{
		// The item-info money row, shown and hidden around a purchase.
		"ItemInfoShowMoney",
		"ItemInfoHideMoney",

		// The quest book's current-quest list: a full rebuild, a clear, and one add.
		"QuestsSingleQuestSetup",
		"Quests_RemoveAllCurrent",
		"Quests_AddCurrent",

		// The HUD's safe-frame overlay — a development aid, not a game surface.
		"HUD_ToggleSafeFrame",
	};

	/// <summary>True when a name is one the contract publishes.</summary>
	public static bool IsNamedEvent(string name)
	{
		foreach (string known in NamedEvents)
		{
			if (string.Equals(known, name, System.StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}
}
