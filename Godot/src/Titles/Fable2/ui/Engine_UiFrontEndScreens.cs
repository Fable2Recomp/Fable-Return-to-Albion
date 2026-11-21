namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The presentation each of the guest's two front-end screens is authored as: the guest ctor
/// hands `"FrontEndStartupScreen.bgf"` to GUI+180 and `"FrontEndMainMenu.bgf"` to GUI+184
/// (static init at 0x8324A2B8 / 0x8324A2F8). `Is` consults the same table.
/// </summary>
public static class Engine_UiFrontEndScreens
{
	public static string? PresentationFor(Engine_UiModel.FrontEndScreen screen) => screen switch
	{
		Engine_UiModel.FrontEndScreen.Title => "frontendstartupscreen",
		Engine_UiModel.FrontEndScreen.MainMenu => "frontendmainmenu",
		_ => null,
	};

	/// <summary>True when a bank name is one of the front end's own screens.</summary>
	public static bool Is(string bank)
	{
		foreach (Engine_UiModel.FrontEndScreen screen in
			System.Enum.GetValues<Engine_UiModel.FrontEndScreen>())
		{
			if (PresentationFor(screen) is { } name &&
				string.Equals(bank, name, System.StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}
}
