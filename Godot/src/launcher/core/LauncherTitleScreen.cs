using System;
using System.Collections.Generic;
using Refii.Core;

namespace Refii.Launcher.Core;

/// <summary>Where a launcher info tab's body comes from.</summary>
public enum LauncherInfoSource
{
	/// <summary>The project repository's recent commits, fetched by the launcher.</summary>
	ProjectChangeLog,

	/// <summary>The project repository's contributors, fetched by the launcher.</summary>
	ProjectContributors,

	/// <summary>The tab's own <see cref="LauncherInfoTab.Text"/>, as BBCode.</summary>
	Text,
}

/// <summary>One tab of the info drawer: its name and what fills it.</summary>
public sealed record LauncherInfoTab(string Name, LauncherInfoSource Source, string Text = "");

/// <summary>
/// A title's background art: the images in <see cref="BundledDirectory"/> (a <c>res://</c> folder),
/// else those in <see cref="ExternalDirectory"/> beside the launcher, with a credit per file stem.
/// </summary>
public sealed record LauncherBackgrounds(
	string BundledDirectory,
	string ExternalDirectory,
	IReadOnlyDictionary<string, string> Credits);

/// <summary>A link in the About menu: the text shown and the page it opens.</summary>
public sealed record LauncherLink(string Label, string Url);

/// <summary>
/// The launcher's screen for one title: what the page shows when that title is selected — its
/// wordmark, window title and notice, its background art and credit, its info drawer and its
/// About links. Everything else on the page follows <see cref="Engine_Title.Active"/> by itself:
/// the settings list and the file it edits (<see cref="RuntimeCVarSet.Active"/>,
/// <see cref="Engine_Title.ConfigPath"/>), the mods folder and package extension, the install and
/// build steps.
/// </summary>
/// <remarks>
/// THE SHELL NAMES NO TITLE. Each title's screen lives under <c>Godot/src/Titles/&lt;Folder&gt;/</c>
/// and registers itself from a module initializer, as <see cref="Engine_TitleGame"/> does; the
/// launcher asks for <see cref="Active"/> and rebuilds the page from it whenever the title switches.
/// A title that registers none gets a plain page: its name, no art, no info tabs.
/// </remarks>
public abstract class LauncherTitleScreen
{
	private static readonly Dictionary<Engine_Title, LauncherTitleScreen> Registered = new();

	/// <summary>Called by a title's module initializer.</summary>
	public static void Register(LauncherTitleScreen screen)
	{
		lock (Registered)
		{
			Registered[screen.Title] = screen;
		}
	}

	/// <summary>The active title's screen, or a plain one when it registered none.</summary>
	public static LauncherTitleScreen Active => For(Engine_Title.Active);

	/// <summary><paramref name="title"/>'s screen, or a plain one when it registered none.</summary>
	public static LauncherTitleScreen For(Engine_Title title)
	{
		lock (Registered)
		{
			return Registered.TryGetValue(title, out LauncherTitleScreen? screen) ? screen : new Plain(title);
		}
	}

	/// <summary>The title this screen is for.</summary>
	public abstract Engine_Title Title { get; }

	/// <summary>The wordmark over the art, as BBCode.</summary>
	public virtual string Wordmark =>
		"[color=#e8dcc2]" + Title.Name.ToUpperInvariant() + " RECOMPILED[/color]";

	/// <summary>The launcher window's title while this title is selected.</summary>
	public virtual string WindowTitle => Title.Name + " Launcher";

	/// <summary>The notice floated over the art; empty shows none.</summary>
	public virtual string WarningText => string.Empty;

	/// <summary>The background art, or null for the plain neutral background.</summary>
	public virtual LauncherBackgrounds? Backgrounds => null;

	/// <summary>The info drawer's tabs, in order; none leaves the drawer empty of tabs.</summary>
	public virtual IReadOnlyList<LauncherInfoTab> InfoTabs => Array.Empty<LauncherInfoTab>();

	/// <summary>The title's own entries in the About menu, before the shared ones.</summary>
	public virtual IReadOnlyList<LauncherLink> AboutLinks => Array.Empty<LauncherLink>();

	private sealed class Plain : LauncherTitleScreen
	{
		public Plain(Engine_Title title) => Title = title;

		public override Engine_Title Title { get; }
	}
}
