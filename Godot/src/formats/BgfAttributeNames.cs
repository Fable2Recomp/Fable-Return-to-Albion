using System.Collections.Generic;

namespace Refii.Launcher.Formats;

/// <summary>
/// Reverse lookup from a Gameface attribute id back to its authored name.
///
/// The file stores only <see cref="GameHash.AttributeId"/> — sdbm masked to 27 bits —
/// so an id cannot be inverted. This is a dictionary of the names the shipped screens
/// actually use, hashed forward at startup; anything absent prints as a raw id, which
/// is the signal to add it here rather than a reason to guess.
///
/// Used for diagnostics and for applying slide commands, where knowing WHICH attribute
/// a command writes is the whole point.
/// </summary>
public static class BgfAttributeNames
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
	};

	private static readonly Dictionary<uint, string> ById = Build();

	private static Dictionary<uint, string> Build()
	{
		var map = new Dictionary<uint, string>(Known.Length);
		foreach (string name in Known)
			map[GameHash.AttributeId(name)] = name;
		return map;
	}

	/// <summary>The authored name for an id, or a <c>#hex</c> placeholder when unknown.</summary>
	public static string Describe(uint id) =>
		ById.TryGetValue(id, out string? name) ? name : $"#{id:X7}";

	/// <summary>True when the id is one of the names above.</summary>
	public static bool TryGetName(uint id, out string name) => ById.TryGetValue(id, out name!);
}
