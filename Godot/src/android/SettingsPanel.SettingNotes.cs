using System;
using System.Collections.Generic;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>A line under a phone setting's row saying what its current choice means: a phone has no hover, so a
/// row's tooltip is never seen there. Only settings with a note (<see cref="Refii.AndroidRuntime.AndroidSettingNotes"/>).</summary>
public partial class SettingsPanel
{
	private readonly Dictionary<string, Label> _settingNotes = new();

	/// <summary>Adds the note under <paramref name="definition"/>'s row, when it has one.</summary>
	private void AddSettingNote(VBoxContainer parent, RuntimeCVarDefinition definition, string current)
	{
		if (Refii.AndroidRuntime.AndroidSettingNotes.For(definition.Name, current) is not { } text)
		{
			return;
		}

		Label note = MakeLabel(text);
		note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		note.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 3));
		note.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
		parent.AddChild(note);
		_settingNotes[definition.Name] = note;
	}

	/// <summary>Updates a setting's note once a new choice is committed.</summary>
	private void RefreshSettingNote(string name, string value)
	{
		if (_settingNotes.TryGetValue(name, out Label? note) && IsInstanceValid(note) &&
		    Refii.AndroidRuntime.AndroidSettingNotes.For(name, value) is { } text)
		{
			note.Text = text;
		}
	}
}
