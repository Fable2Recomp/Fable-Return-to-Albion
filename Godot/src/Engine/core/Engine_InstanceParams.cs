using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Core;

/// <summary>
/// Instance shader parameters written through here are also held here, so the code that needs
/// one back reads it without asking the server.
/// </summary>
/// <remarks>
/// `GetInstanceShaderParameter` is `instance_geometry_get_shader_parameter`, a `FUNC2RC`: with the
/// separate render thread it waits for that thread to finish what it is doing, and
/// `SetInstanceShaderParameter` with nil asks the server for the default first, the same way.
/// The server keeps a value it was given across material changes (`InstanceUniforms::set`,
/// `_init_param` fills only a nil one), so what was last written here is what it would answer.
/// Only a WATCHED node holds anything (<see cref="Watch"/>, by the reader that will want the
/// values back), so the thousands of props a load lights cost no table entry. A parameter not
/// written here since the node was watched is not held: its reader still asks the server
/// (counted, Engine_ServerSyncs). Main thread.
/// </remarks>
public static class Engine_InstanceParams
{
	private static readonly ConditionalWeakTable<GeometryInstance3D, Dictionary<StringName, Variant>> Held = new();

	/// <summary>Holds what is written to <paramref name="node"/> through <see cref="Set"/> from now on.</summary>
	public static void Watch(GeometryInstance3D node) => Held.GetValue(node, static _ => new Dictionary<StringName, Variant>());

	/// <summary>Writes a parameter, and holds it on a watched node; nil puts the server's default back and holds nothing.</summary>
	public static void Set(GeometryInstance3D node, StringName name, Variant value)
	{
		if (value.VariantType == Variant.Type.Nil)
		{
			long from = Engine_ServerSyncs.Begin();
			node.SetInstanceShaderParameter(name, value);
			Engine_ServerSyncs.End(Engine_ServerSyncs.Site.InstanceParam, from);
			if (Held.TryGetValue(node, out Dictionary<StringName, Variant>? cleared))
			{
				cleared.Remove(name);
			}
			return;
		}

		node.SetInstanceShaderParameter(name, value);
		if (Held.TryGetValue(node, out Dictionary<StringName, Variant>? held))
		{
			held[name] = value;
		}
	}

	/// <summary>Whether a value written through <see cref="Set"/> stands on the node.</summary>
	public static bool Holds(GeometryInstance3D node, StringName name) =>
		Held.TryGetValue(node, out Dictionary<StringName, Variant>? held) && held.ContainsKey(name);

	/// <summary>The parameter's value: the one written here, else the server's answer.</summary>
	public static Variant Get(GeometryInstance3D node, StringName name)
	{
		if (Held.TryGetValue(node, out Dictionary<StringName, Variant>? held) && held.TryGetValue(name, out Variant value))
		{
			return value;
		}

		long from = Engine_ServerSyncs.Begin();
		Variant read = node.GetInstanceShaderParameter(name);
		Engine_ServerSyncs.End(Engine_ServerSyncs.Site.InstanceParam, from);
		return read;
	}
}
