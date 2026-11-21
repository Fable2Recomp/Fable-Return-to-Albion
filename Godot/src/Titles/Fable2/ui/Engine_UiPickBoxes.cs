namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// Each pickbox's caption as the guest STATES it (hooks/gui_headless_presentation.cpp): Init (TU1
/// `sub_82881FD0`, disc `sub_828860E8`) names the pickbox's text element, `&lt;path&gt;.grp.text`,
/// posted as `@PickBox.Init`; SetIndex (TU1 `sub_828821D0`, disc `sub_828862E8`) hands that element
/// the chosen option, posted as `@PickBox.Set` with the path, the index and the text. Keyed by the
/// element path, so a screen reads its pickboxes by the paths it lays out.
/// </summary>
public static class Engine_UiPickBoxes
{
	private static readonly Dictionary<string, string> Captions = new(System.StringComparer.Ordinal);

	/// <summary>`@PickBox.Init`: pickbox (hex), text path. The element holds no option yet.</summary>
	public static void Init(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(1).Text is { Length: > 0 } path)
		{
			Captions.Remove(path);
		}
	}

	/// <summary>`@PickBox.Set`: pickbox (hex), text path, index, text.</summary>
	public static void Set(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(1).Text is { Length: > 0 } path)
		{
			Captions[path] = call.Argument(3).Text ?? string.Empty;
		}
	}

	/// <summary>The caption last stated for the pickbox text element at <paramref name="path"/>, or null.</summary>
	public static string? Caption(string path) => Captions.TryGetValue(path, out string? text) ? text : null;
}
