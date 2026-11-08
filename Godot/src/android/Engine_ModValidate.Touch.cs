using System.Text.Json;

namespace Refii.Mods;

/// <summary>
/// The touchscreen half of an <c>input.json</c> check: an action's <c>"touch"</c>
/// (<see cref="ModInputTouch"/>). Checked on every platform, so a mod written on the desktop is
/// told what its phone players would see.
/// </summary>
public static partial class ModValidate
{
	/// <summary>An object <c>"touch"</c>'s fields (ModInputTouch.Parse).</summary>
	private static readonly string[] InputTouchKeys = { "pill", "label", "caption", "when" };

	/// <summary>
	/// An action's <c>"touch"</c>: <c>true</c>, a pill's text, or <c>{ pill | label, caption, when }</c>.
	/// True when it gives the action a pill of its own (which binds it on a touchscreen).
	/// </summary>
	private static bool CheckInputTouch(Doc doc, JsonElement touch, string at)
	{
		switch (touch.ValueKind)
		{
			case JsonValueKind.True:
				return true;
			case JsonValueKind.False:
				return false;
			case JsonValueKind.String:
				if (touch.GetString()!.Trim().Length == 0)
				{
					doc.Warn(at, "an empty pill text gives no pill");
					return false;
				}

				return true;
			case JsonValueKind.Object:
				doc.Keys(touch, at, InputTouchKeys);
				string pill = (doc.Text(touch, "pill", at, strict: true) ?? doc.Text(touch, "label", at, strict: true) ?? string.Empty).Trim();
				string caption = (doc.Text(touch, "caption", at, strict: true) ?? string.Empty).Trim();
				doc.Text(touch, "when", at, strict: true);
				if (pill.Length == 0 && caption.Length == 0)
				{
					doc.Error(at, "names neither a \"pill\" nor a \"caption\"; ignored");
				}

				return pill.Length != 0;
			default:
				doc.Error(at, "must be true, a pill's text or { \"pill\", \"caption\", \"when\" }; ignored");
				return false;
		}
	}
}
