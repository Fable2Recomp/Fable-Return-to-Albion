#if TOOLS
using System;
using Godot;
using Refii.Core;

namespace Refii.EditorTools;

/// <summary>
/// The editor's game: Fable 2 or Fable 3, chosen on the editor's own toolbar.
/// </summary>
/// <remarks>
/// <para>THE TITLE IS THE PROCESS'S, so a change restarts the editor. Everything the editor builds is
/// built for <see cref="Engine_Title.Active"/> once — the bank mounts, the mods root, the Mod Studio,
/// the loaders — and the launcher opens the editor with <see cref="Engine_Title.EnvironmentVariable"/>
/// set for the same reason. A switch states the new title in that variable (which the restarted editor
/// inherits) and in the editor setting, then restarts with the open scenes saved: the editor comes back
/// exactly as the launcher would have opened it for that game.</para>
/// <para>An editor opened without the variable (from Godot directly) takes the remembered choice.</para>
/// </remarks>
public static class EditorTitlePicker
{
	/// <summary>The editor setting holding the last chosen title's key.</summary>
	public const string SettingKey = "refii/active_title";

	/// <summary>
	/// The remembered title for an editor the launcher did not name one for. Call before anything
	/// builds for the active title.
	/// </summary>
	public static void ApplyRemembered()
	{
		if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(Engine_Title.EnvironmentVariable)))
		{
			return;
		}

		EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
		if (settings.HasSetting(SettingKey) && Engine_Title.Find(settings.GetSetting(SettingKey).AsString()) is { } title)
		{
			Engine_Title.Active = title;
			State(title);
		}
	}

	/// <summary>The toolbar's picker: every title, the active one selected; picking another asks, then restarts.</summary>
	public static Control Create()
	{
		var row = new HBoxContainer { Name = "RefiiTitlePicker" };
		row.AddChild(new Label { Text = "Game:" });
		var picker = new OptionButton
		{
			TooltipText = "The game the editor works on. Switching restarts the editor (open scenes are saved).",
		};
		for (int i = 0; i < Engine_Title.All.Count; i++)
		{
			picker.AddItem(Engine_Title.All[i].Name, i);
			if (Engine_Title.All[i] == Engine_Title.Active)
			{
				picker.Select(i);
			}
		}

		picker.ItemSelected += index =>
		{
			Engine_Title chosen = Engine_Title.All[(int)index];
			if (chosen == Engine_Title.Active)
			{
				return;
			}

			var confirm = new ConfirmationDialog
			{
				Title = "Switch game",
				DialogText = $"Switch the editor to {chosen.Name}?\n\nThe editor restarts; open scenes are saved first.",
				OkButtonText = "Switch and restart",
			};
			confirm.Confirmed += () => Switch(chosen);
			confirm.Canceled += () => picker.Select(IndexOf(Engine_Title.Active));
			confirm.CloseRequested += () => picker.Select(IndexOf(Engine_Title.Active));
			EditorInterface.Singleton.GetBaseControl().AddChild(confirm);
			confirm.PopupCentered();
		};
		row.AddChild(picker);
		return row;
	}

	/// <summary>
	/// The title in the process environment, both copies: .NET's own (what managed code reads) and the
	/// native one (OS.SetEnvironment, setenv), which is what the restarted editor process inherits — on
	/// Linux .NET's setter changes only its own copy.
	/// </summary>
	private static void State(Engine_Title title)
	{
		System.Environment.SetEnvironmentVariable(Engine_Title.EnvironmentVariable, title.Key);
		OS.SetEnvironment(Engine_Title.EnvironmentVariable, title.Key);
	}

	private static int IndexOf(Engine_Title title)
	{
		for (int i = 0; i < Engine_Title.All.Count; i++)
		{
			if (Engine_Title.All[i] == title)
			{
				return i;
			}
		}

		return 0;
	}

	private static void Switch(Engine_Title title)
	{
		EditorInterface.Singleton.GetEditorSettings().SetSetting(SettingKey, title.Key);
		State(title);
		GD.Print($"[bnk_mount] switching the editor to {title.Name}: restarting");
		EditorInterface.Singleton.RestartEditor(save: true);
	}
}
#endif
