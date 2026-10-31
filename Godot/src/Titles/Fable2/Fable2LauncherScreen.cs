namespace Refii.Titles.Fable2;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Refii.Core;
using Refii.Launcher.Core;

/// <summary>
/// Fable 2's launcher screen: the page as it has always been — its wordmark, the launcher ini's
/// window title and notice, the bundled art with its credit, the project's change log and
/// contributors, and the project page.
/// </summary>
public sealed class Fable2LauncherScreen : LauncherTitleScreen
{
	[ModuleInitializer]
	internal static void RegisterScreen() => Register(new Fable2LauncherScreen());

	public override Engine_Title Title => Engine_Title.Fable2;

	public override string Wordmark =>
		"[color=#e8dcc2]FABLE [/color][color=#daa520]2[/color][color=#e8dcc2]: RETURN TO ALBION[/color]";

	/// <summary>The launcher ini's <c>[Window] Title</c>, which ships as Fable 2's.</summary>
	public override string WindowTitle => LauncherTheme.WindowTitle;

	/// <summary>The launcher ini's <c>[Window] WarningText</c>, which ships as Fable 2's.</summary>
	public override string WarningText => LauncherTheme.WarningText;

	private static readonly IReadOnlyDictionary<string, string> ArtCredits =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["02"] = "Art by Mirai-Miki",
			["03"] = "Art by ♡ 𝐼𝓂𝓎 ♡",
		};

	public override LauncherBackgrounds Backgrounds => new(
		"res://assets/launcher/backgrounds",
		LauncherPaths.GetLauncherAssetPath("backgrounds"),
		ArtCredits);

	private static readonly LauncherInfoTab[] Tabs =
	{
		new("Change Log", LauncherInfoSource.ProjectChangeLog),
		new("Credits", LauncherInfoSource.ProjectContributors),
	};

	public override IReadOnlyList<LauncherInfoTab> InfoTabs => Tabs;

	private static readonly LauncherLink[] Links =
	{
		new("Fable 2: Return to Albion", "https://github.com/Fable2Recomp/Fable2Remake"),
	};

	public override IReadOnlyList<LauncherLink> AboutLinks => Links;
}
