using System.Collections.Generic;

using Godot;

using Refii.Ui;

namespace Refii.Titles.Fable3;

/// <summary>
/// The mini-game bar's CLONED segments on the drawn view. `minigamebarcontrol.lua`
/// `SpawnButtonOfType` calls Gameface `clone(template)` when every entry of a colour is in use and
/// writes the clone's `scale.x`, `position.x` and `opacity` (<see cref="Fable3MiniGameBar"/>); the
/// shared view draws one node per .bgf element, so each clone is a copy of its template's drawn
/// node under the same parent (`Layer.Bar.Elements.Buttons`), posed in the parent's guest units as
/// the clone binding poses the template (origin = position, the templates' pivot is 0), and faded
/// as the resolve fades the template: the parent chain's opacity × the clone's own × each
/// descendant's own. The templates themselves (copy 0) are driven through the document's channels.
/// </summary>
public sealed class Fable3MiniGameBarClones
{
	private static readonly StringName OrdinalMeta = "ordinal";
	private static readonly StringName OpacityParameter = "opacity";

	private readonly Dictionary<Fable3MiniGameBar.Element, Node3D> _copies = new();
	private readonly Dictionary<int, Node3D> _templates = new();

	/// <summary>
	/// Poses every clone the script has made. <paramref name="pathOrdinals"/> is the document's
	/// statement join (a path to its ordinals).
	/// </summary>
	public void Drive(
		Engine_HudView? view, Fable3MiniGameBar? bar, Engine_UiScreen? screen, Engine_UiElementResolve? resolve,
		System.Func<string, IReadOnlyList<int>> pathOrdinals)
	{
		if (view is null || bar is null || screen is null || resolve is null)
		{
			HideAll();
			return;
		}

		// A reloaded presentation's Lua starts empty (Fable3LuaContracts.Forget): its old clones go.
		if (_copies.Count != 0)
		{
			var live = new HashSet<Fable3MiniGameBar.Element>(bar.Elements);
			var gone = new List<Fable3MiniGameBar.Element>();
			foreach ((Fable3MiniGameBar.Element element, Node3D copy) in _copies)
			{
				if (!live.Contains(element))
				{
					gone.Add(element);
					if (GodotObject.IsInstanceValid(copy))
					{
						copy.QueueFree();
					}
				}
			}

			foreach (Fable3MiniGameBar.Element element in gone)
			{
				_copies.Remove(element);
			}
		}

		foreach (Fable3MiniGameBar.Element element in bar.Elements)
		{
			if (element.Copy == 0)
			{
				continue;
			}

			IReadOnlyList<int> ordinals =
				pathOrdinals(Fable3MiniGameBar.ButtonsPath + "." + Fable3MiniGameBar.TemplateName(element.Colour));
			if (ordinals.Count == 0 || TemplateNode(view, ordinals[0]) is not { } template ||
				template.GetParent() is not Node3D parent)
			{
				Hide(element);
				continue;
			}

			int templateOrdinal = ordinals[0];
			if (!_copies.TryGetValue(element, out Node3D? copy) || !GodotObject.IsInstanceValid(copy) ||
				copy.GetParent() != parent)
			{
				if (copy is not null && GodotObject.IsInstanceValid(copy))
				{
					copy.QueueFree();
				}

				copy = MakeCopy(template, element);
				parent.AddChild(copy);
				_copies[element] = copy;
			}

			Pose(copy, template, element, templateOrdinal, screen, resolve);
		}
	}

	private static Node3D MakeCopy(Node3D template, Fable3MiniGameBar.Element element)
	{
		var copy = (Node3D)template.Duplicate();
		copy.Name = $"{template.Name}_lua{element.Copy}";

		// Each copy fades on its own: a duplicate shares its source's material resources.
		var stack = new Stack<Node>();
		stack.Push(copy);
		while (stack.Count > 0)
		{
			Node node = stack.Pop();
			if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material)
			{
				mesh.MaterialOverride = (ShaderMaterial)material.Duplicate();
			}

			foreach (Node child in node.GetChildren())
			{
				stack.Push(child);
			}
		}

		return copy;
	}

	private static void Pose(
		Node3D copy, Node3D template, Fable3MiniGameBar.Element element, int templateOrdinal, Engine_UiScreen screen,
		Engine_UiElementResolve resolve)
	{
		Transform3D transform = template.Transform;
		if (element.X is { } x && !float.IsNaN(x))
		{
			transform.Origin = new Vector3(x, transform.Origin.Y, transform.Origin.Z);
		}

		if (element.ScaleX is { } scale && !float.IsNaN(scale))
		{
			// The x column, never `Scale`: a zero column reads back (0,0,0) on every axis.
			Basis basis = transform.Basis;
			Vector3 axis = basis.X.LengthSquared() > 0.000001f ? basis.X.Normalized() : new Vector3(1f, 0f, 0f);
			basis.X = axis * scale;
			transform.Basis = basis;
		}

		copy.Transform = transform;

		BgfElement[] elements = screen.Document.Elements;
		int parentOrdinal = templateOrdinal < elements.Length ? elements[templateOrdinal].ParentOrdinal : -1;
		bool drawn = parentOrdinal >= 0 && parentOrdinal < resolve.Drawn.Length && resolve.Drawn[parentOrdinal];
		float chain = parentOrdinal >= 0 && parentOrdinal < resolve.Opacity.Length ? resolve.Opacity[parentOrdinal] : 0f;
		float own = (element.Opacity ?? 100f) / 100f;
		copy.Visible = drawn && own > 0f && chain > 0f;
		if (!copy.Visible)
		{
			return;
		}

		var stack = new Stack<Node>();
		stack.Push(copy);
		while (stack.Count > 0)
		{
			Node node = stack.Pop();
			if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material &&
				node.GetMeta(OrdinalMeta, -1).AsInt32() is var ordinal && ordinal >= 0)
			{
				material.SetShaderParameter(OpacityParameter, chain * own * Beneath(ordinal, templateOrdinal, elements, resolve));
			}

			foreach (Node child in node.GetChildren())
			{
				stack.Push(child);
			}
		}
	}

	/// <summary>The product of the resolve's own opacity from <paramref name="ordinal"/> up to, not including, the template.</summary>
	private static float Beneath(int ordinal, int templateOrdinal, BgfElement[] elements, Engine_UiElementResolve resolve)
	{
		float product = 1f;
		for (int at = ordinal; at >= 0 && at != templateOrdinal && at < elements.Length; at = elements[at].ParentOrdinal)
		{
			product *= at < resolve.Own.Length ? resolve.Own[at] : 1f;
		}

		return product;
	}

	/// <summary>The template's drawn node: the first node carrying its ordinal in a visible piece, cached while it lives.</summary>
	private Node3D? TemplateNode(Engine_HudView view, int ordinal)
	{
		if (_templates.TryGetValue(ordinal, out Node3D? cached) && GodotObject.IsInstanceValid(cached) &&
			cached.IsInsideTree())
		{
			return cached;
		}

		_templates.Remove(ordinal);
		foreach ((string id, Node3D piece) in view.DrawnPieces())
		{
			if (!id.Contains("MiniGameBar", System.StringComparison.Ordinal))
			{
				continue;
			}

			var stack = new Stack<Node>();
			stack.Push(piece);
			while (stack.Count > 0)
			{
				Node node = stack.Pop();
				if (node is Node3D node3D && node.GetMeta(OrdinalMeta, -1).AsInt32() == ordinal &&
					!node.Name.ToString().Contains("_lua", System.StringComparison.Ordinal))
				{
					_templates[ordinal] = node3D;
					return node3D;
				}

				foreach (Node child in node.GetChildren())
				{
					stack.Push(child);
				}
			}
		}

		return null;
	}

	private void Hide(Fable3MiniGameBar.Element element)
	{
		if (_copies.TryGetValue(element, out Node3D? copy) && GodotObject.IsInstanceValid(copy))
		{
			copy.Visible = false;
		}
	}

	private void HideAll()
	{
		foreach (Node3D copy in _copies.Values)
		{
			if (GodotObject.IsInstanceValid(copy))
			{
				copy.Visible = false;
			}
		}
	}
}
