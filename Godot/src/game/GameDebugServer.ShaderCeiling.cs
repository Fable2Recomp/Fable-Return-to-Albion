using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>The render channel's `shaderCeiling` lever (Engine_ShaderCeiling); normal play never requests it.</summary>
public partial class GameDebugServer
{
	private static readonly Engine_ShaderCeiling ShaderCeiling = new(CeilingRoots, IsTreeProp);

	/// <summary>Applies a validated `shaderCeiling` request on the Godot thread, mirrors included.</summary>
	private void ApplyShaderCeiling(Engine_ShaderCeiling.Request ceiling)
	{
		var viewports = new List<Viewport> { GetViewport() };
		foreach (Node found in GetTree().Root.FindChildren("*", "SubViewport", true, false))
		{
			viewports.Add((SubViewport)found);
		}
		ShaderCeiling.Apply(ceiling, GetTree().Root, viewports);
	}

	/// <summary>
	/// Where each category stands: the props containers (trees told apart per node), the terrain
	/// containers — the grid's `Terrain` and each vista's `AdjacentTerrainN` — and the entities.
	/// </summary>
	private static IEnumerable<(Node Root, Engine_ShaderCeiling.Category Category)> CeilingRoots(Node root)
	{
		foreach (Node props in root.FindChildren(Refii.Level.Engine_LevelProps.NodeName, "Node3D", true, false))
		{
			if (props is Refii.Level.Engine_PropStreamer)
			{
				yield return (props, Engine_ShaderCeiling.Category.Props);
			}
		}
		foreach (string pattern in new[] { Refii.Level.Engine_LevelScene.TerrainNodeName, Refii.Level.Engine_LevelScene.AdjacentNodeName + "*" })
		{
			foreach (Node terrain in root.FindChildren(pattern, "Node3D", true, false))
			{
				if (terrain is not GeometryInstance3D)
				{
					yield return (terrain, Engine_ShaderCeiling.Category.Terrain);
				}
			}
		}
		if (GameEntities.Current is { } entities && GodotObject.IsInstanceValid(entities))
		{
			yield return (entities, Engine_ShaderCeiling.Category.Entities);
		}
	}

	/// <summary>
	/// A TreeMesh placement (`.mdl` byte 77 == 2): its main and LOD models carry the tree band, and
	/// each model's class is cached by path (Engine_PropDrawRange.ClassOf); its cast-only `_shadow`
	/// model is the tree's when the model it shadows is one.
	/// </summary>
	private static bool IsTreeProp(GeometryInstance3D node)
	{
		if (node.HasMeta(Refii.Level.Engine_PropDrawRange.TreeBandMeta))
		{
			return true;
		}
		if (!node.HasMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta) ||
			node.GetMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta).AsString() is not { Length: > 0 } path)
		{
			return false;
		}
		if (Refii.Level.Engine_PropDrawRange.ClassOf(path) == Refii.Level.PropModelClass.Tree)
		{
			return true;
		}
		int shadow = path.LastIndexOf("_shadow", StringComparison.OrdinalIgnoreCase);
		return shadow > 0 &&
			Refii.Level.Engine_PropDrawRange.ClassOf(path.Remove(shadow, "_shadow".Length)) == Refii.Level.PropModelClass.Tree;
	}
}
