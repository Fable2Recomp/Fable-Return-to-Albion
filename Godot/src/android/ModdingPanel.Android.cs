using Godot;
using Refii.Mods;

namespace Refii.Launcher;

/// <summary>The Mods panel on the Android launcher.</summary>
public partial class ModdingPanel
{
	/// <summary>
	/// The Android launcher. The phone has no folder to open, no editor and no Mod Studio, and its
	/// phone compiler builds no mod's native code yet, so those mods cannot be enabled there.
	/// </summary>
	private static bool Phone => !Engine.IsEditorHint() && Refii.Core.Engine_Platform.IsAndroid;

	/// <summary>The panel's help line on a phone.</summary>
	private static string PhoneHelp(string extension) =>
		$"Install a mod package ({extension}) from this phone's storage; the mods that come with " +
		"the app are already listed. A mod is installed disabled: tick it to turn it on. " +
		"Higher in the list wins a conflict. Changes apply the next time the game starts. " +
		"Mods with native code cannot run on the phone yet.";

	/// <summary>
	/// On a phone, finishes the panel after the install row: the refresh rides on the install row;
	/// the rest of the desktop's buttons have nothing to open on a phone.
	/// </summary>
	/// <returns>False on the desktop, which goes on to its own buttons.</returns>
	private bool BuildPhoneInstallRow(HBoxContainer installRow)
	{
		if (!Phone)
		{
			return false;
		}

		installRow.AddChild(RefreshButton());
		AddStatusAndList();
		return true;
	}

	/// <summary>
	/// Picks a .f2mod in Android's document picker, copies it into <c>.cache/downloads/</c> (the
	/// picker gives a content URI, not a path) and shows what it is before installing it.
	/// </summary>
	private void OnInstallFromPhone()
	{
		Error error = DisplayServer.FileDialogShow("Install Mod Package", "", "", false,
			DisplayServer.FileDialogMode.OpenFile,
			// A .f2mod has no MIME type Android knows: show every document and let the installer check it.
			new[] { "*.f2mod;Refii mod package;*/*" },
			Callable.From<bool, string[], int>((selected, paths, _) =>
			{
				if (!selected || paths.Length != 1)
				{
					return;
				}

				string uri = paths[0];
				Callable.From(() => ConfirmInstall(
					Refii.AndroidRuntime.AndroidModPicker.Fetch(uri, _modsRoot,
						target => Refii.AndroidRuntime.AndroidModPicker.CopyDocument(uri, target)),
					downloaded: true)).CallDeferred();
			}));
		if (error != Error.Ok)
		{
			_statusMessage = "The Android document picker could not open: " + error;
			Rebuild();
		}
	}

	/// <summary>
	/// Turns a native mod's tick back off on a phone, which cannot build its code into the game yet.
	/// </summary>
	/// <returns>True when the tick was refused.</returns>
	private bool RefuseNativeModOnPhone(CheckBox check, ModInstall mod)
	{
		if (!Phone || !_native.ContainsKey(mod.Name))
		{
			return false;
		}

		check.SetPressedNoSignal(false);
		_statusMessage = $"'{mod.Name}' has native code, which the phone cannot build into the game yet.";
		Rebuild();
		return true;
	}

	/// <summary>
	/// A native mod's status on a phone. The phone compiler builds refii's own hooks only, so a native
	/// mod's code would be missing from the game while its data and Lua loaded around it.
	/// </summary>
	private Label NotOnPhoneLabel()
	{
		var phoneLabel = MakeLabel("⛔ not on phone");
		phoneLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.3f, 0.25f));
		phoneLabel.HorizontalAlignment = HorizontalAlignment.Center;
		phoneLabel.TooltipText = "This mod has native code, which the phone cannot build into the game yet.";
		return phoneLabel;
	}

	/// <summary>A row control's size, grown on the phone to a fingertip (twice the text height).</summary>
	private Vector2 Hit(Vector2 size) => Phone ? size.Max(new Vector2(_fontSize * 2, _fontSize * 2)) : size;
}
