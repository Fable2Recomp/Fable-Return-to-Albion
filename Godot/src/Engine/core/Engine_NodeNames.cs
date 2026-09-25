using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// A node's name as a plain string, read from Godot ONCE per node.
/// </summary>
/// <remarks>
/// `Node.Name` crosses as a fresh finalizable `StringName` and `ToString()` decodes it into a
/// fresh string, so a per-frame loop over child names allocates two objects per child per frame.
/// Keyed by instance id, which Godot never reuses, so a freed node's entry can only go stale,
/// never be wrong. Entries are never evicted. Only for nodes whose names do not change after
/// they are built.
/// </remarks>
public static class Engine_NodeNames
{
	private static readonly Dictionary<ulong, string> Names = new();

	public static string Of(Node node)
	{
		ulong id = node.GetInstanceId();
		if (!Names.TryGetValue(id, out string? name))
		{
			name = node.Name.ToString();
			Names[id] = name;
		}

		return name;
	}
}
