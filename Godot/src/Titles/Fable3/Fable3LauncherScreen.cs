namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Refii.Core;
using Refii.Launcher.Core;

/// <summary>
/// Fable 3's launcher screen. The repository ships no Fable 3 art (and converted game art may not
/// be shipped), so the page has the plain neutral background and no credit; its info drawer says
/// what Fable 3 is in this app today. There is no Fable 3 project page to link.
/// </summary>
public sealed class Fable3LauncherScreen : LauncherTitleScreen
{
	[ModuleInitializer]
	internal static void RegisterScreen() => Register(new Fable3LauncherScreen());

	public override Engine_Title Title => Engine_Title.Fable3;

	public override string Wordmark =>
		"[color=#e8dcc2]FABLE [/color][color=#daa520]3[/color][color=#e8dcc2] RECOMPILED[/color]";

	public override string WarningText => "Fable 3 support is in early development and is not playable yet.";

	public override IReadOnlyList<LauncherInfoTab> InfoTabs => new[]
	{
		new LauncherInfoTab("About", LauncherInfoSource.Text,
			"[color=#daa520][font_size=16]Fable 3[/font_size][/color]\n\n" +
			"[color=#2b221b]Fable 3 is installed and built from your own disc, beside Fable 2. " +
			"Support is in early development: the game is not playable yet.[/color]\n\n" +
			"[color=#8c7b6b]Its settings are kept in " + Title.ConfigFileName + ", apart from Fable 2's, " +
			"and only the settings Fable 3 reads are listed. Its mods are " + Title.ModExtension +
			" packages, in its own mods folder.[/color]"),
	};
}
