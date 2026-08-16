using System.Collections.Generic;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// Reverse lookup from a Gameface attribute id back to its authored name.
///
/// The file stores only <see cref="Engine_GameHash.AttributeId"/> — sdbm masked to 27 bits —
/// so an id cannot be inverted. This is a dictionary of the names the shipped screens
/// actually use, hashed forward at startup; anything absent prints as a raw id, which
/// is the signal to add it here rather than a reason to guess.
///
/// Used for diagnostics and for applying slide commands, where knowing WHICH attribute
/// a command writes is the whole point.
/// </summary>
public static class Engine_BgfAttributeNames
{
	/// <summary>Names to hash. Extend freely — an unused one costs a dictionary slot.</summary>
	private static readonly string[] Known =
	{
		"name", "active", "visible", "opacity", "colour", "color",

		"position.x", "position.y", "position.z",
		"rotation.x", "rotation.y", "rotation.z",
		"scale.x", "scale.y", "scale.z",
		"pivot.x", "pivot.y", "pivot.z",

		"width", "height", "depth",

		// Camera
		"fov", "clipnear", "clipfar", "orthographic",
		"lookatpoint.x", "lookatpoint.y", "lookatpoint.z",

		// Light
		"brightness", "lightdiffuse.r", "lightdiffuse.g", "lightdiffuse.b",
		"lightspecular.r", "lightspecular.g", "lightspecular.b",
		"lightambient.r", "lightambient.g", "lightambient.b",
		"attenuation", "falloff", "range", "conangle",

		// Material / texture
		"texture", "material", "blendmode", "twosided", "alpharef",
		"diffuse.r", "diffuse.g", "diffuse.b", "diffuse.a",
		"emissive.r", "emissive.g", "emissive.b",
		"specular.r", "specular.g", "specular.b",
		"u", "v", "scaleu", "scalev", "offsetu", "offsetv",

		// Text
		"text", "font", "fontsize", "size", "halign", "valign",
		"boxwidth", "boxheight", "wordwrap", "linespacing",

		// HUD BINDINGS — how an element says which live VALUE it draws.
		//
		// These are the game's, and they are the whole reason the in-game HUD can be driven
		// without a table of our own: an element carries `MeterName = "PlayerHealth"` and the
		// runtime registers it under a hash of that STRING, so the name an element answers to is
		// stated in the file rather than inferred from where it sits in the tree.
		//
		// CASE IS EXACT. The id is sdbm of the authored name with no folding, so "MeterName" and
		// "metername" are different attributes — unlike everything above, which the artist
		// happened to author in lower case. These are spelled as the executable's own string
		// block spells them, at 0x820D9F3C onward.

		// The tint's WEIGHT, same exact-case family. `sub_8286ED40` routes it to
		// `sub_8286EFB8`, which rewrites lane .w of the packed vertex colour (.fac field 13).
		"TintingAmount",

		"MeterName", "SlideFaderName", "SlideFader2Name", "PulserName", "BoxName",
		"TextName", "TinterName", "SlideToggleName", "SlideSetName", "PrimDescName",
		"ImageName",

		// A sprite whose `Movie` string is non-empty draws a Bink movie into its own rect
		// INSTEAD of a texture; setting a texture clears it (`sub_8286DDC0`). Sprite +0x234.
		"Movie", "Mask", "RenderInGame",

		// THE CLIP BINDING, BY NAME not hierarchy: the mask carries `Mask` + `Scroller`, the
		// clipped element carries `MaskName` / `ScrollerName` holding that mask's NAME. The two
		// are INDEPENDENT — an element can scroll without being clipped.
		"MaskName", "Scroller", "ScrollerName",

		// Text and colour names the shipped screens use that were printing as raw #hex.
		// `TextID` is the BABEL KEY an authored Text carries beside its English `textstring`
		// — that pair is how a row states its own localised label.
		"TextID", "textstring", "texttype", "textcolor.r", "textcolor.g", "textcolor.b",
		"backcolor.r", "backcolor.g", "backcolor.b", "usebackcolor",
		"horzscroll", "vertscroll", "leading", "tracking", "orientation",
		"renderstyle", "rotationorder", "priority", "endtime", "timeoffset",

		// The widescreen and centring overrides the shipped Lua reads off an element — see
		// `widescreenadapter.lua` and `screencentertoggle.lua`. Zero means "no override".
		"widescreenXPos", "widescreenYPos", "WidescreenXWidth", "centerXPos",

		// The generic event payload every Gameface behaviour reads out of `g_Param`.
		"String1", "String2", "Float1", "Float2",
	};

	private static readonly Dictionary<uint, string> ById = Build();

	private static Dictionary<uint, string> Build()
	{
		var map = new Dictionary<uint, string>(Known.Length);
		foreach (string name in Known)
			map[Engine_GameHash.AttributeId(name)] = name;
		return map;
	}

	/// <summary>The authored name for an id, or a <c>#hex</c> placeholder when unknown.</summary>
	public static string Describe(uint id) =>
		ById.TryGetValue(id, out string? name) ? name : $"#{id:X7}";

	/// <summary>True when the id is one of the names above.</summary>
	public static bool TryGetName(uint id, out string name) => ById.TryGetValue(id, out name!);
}
