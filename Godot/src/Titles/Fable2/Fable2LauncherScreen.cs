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

	public override string Wordmark => UnifiedWordmark;

	/// <summary>The launcher ini's <c>[Window] Title</c>.</summary>
	public override string WindowTitle => LauncherTheme.WindowTitle;

	/// <summary>The launcher ini's <c>[Window] WarningText</c>.</summary>
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
		new("About", LauncherInfoSource.Text,
			"[color=#daa520][font_size=16]Fable: Return to Albion[/font_size][/color]\n\n" +
			"[color=#2b221b]A unified native PC remaster and recompilation hub for Fable 2 and Fable 3, " +
			"built from your own Xbox 360 discs.[/color]\n\n" +
			"[color=#8c7b6b]• [b]Fable 2[/b]: Install, mod (.f2mod), and play directly from the launch dock.\n" +
			"• [b]Fable 3[/b] (Experimental): Early development support — install from disc and manage .f3mod packages alongside Fable 2.[/color]"),
	};

	public override IReadOnlyList<LauncherInfoTab> InfoTabs => Tabs;

	private static readonly LauncherLink[] Links =
	{
		new("Fable: Return to Albion", "https://github.com/Fable2Recomp/Fable2Remake"),
	};

	public override IReadOnlyList<LauncherLink> AboutLinks => Links;
}
