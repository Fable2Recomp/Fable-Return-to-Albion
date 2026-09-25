using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// The editor debugger's remote scene tree and inspector, in the running game. The tree
/// expands a node's children only when it is opened (a live game holds tens of thousands of
/// nodes); a search lists matching nodes by name. The inspector shows the selected node's editor
/// properties, re-read twice a second, with a visibility toggle for anything that draws.
/// </summary>
public partial class GameDevTabScene : GameDevTab
{
	public override string Title => "Scene";

	private const int SearchLimit = 300;
	private const string Placeholder = "…";

	private readonly Tree _tree = new()
	{
		HideRoot = false,
		SizeFlagsVertical = SizeFlags.ExpandFill,
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};

	private readonly LineEdit _search = new()
	{
		PlaceholderText = "find node by name…",
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};

	private readonly Label _path = new() { AutowrapMode = TextServer.AutowrapMode.Arbitrary };
	private readonly CheckBox _visible = new() { Text = "visible", Disabled = true };
	private readonly GameDevTable _properties = new(new[] { "property", "value" });

	private readonly Dictionary<TreeItem, ulong> _nodeOf = new();
	private ulong _selected;

	public GameDevTabScene()
	{
		var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild(split);

		var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		var bar = new HBoxContainer();
		bar.AddChild(_search);
		var reload = new Button { Text = "Reload" };
		reload.Pressed += Rebuild;
		bar.AddChild(reload);
		left.AddChild(bar);
		left.AddChild(_tree);
		split.AddChild(left);

		// Values are the long column (resources, paths, transforms); names are short.
		_properties.SetColumnExpand(0, false);
		_properties.SetColumnCustomMinimumWidth(0, 200);
		_properties.SetColumnExpand(1, true);

		var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		right.AddChild(_path);
		right.AddChild(_visible);
		right.AddChild(_properties);
		split.AddChild(right);

		// Deferred: the tree refuses to create items inside its own mouse event.
		_tree.ItemCollapsed += item =>
		{
			if (!item.Collapsed)
			{
				Callable.From(() => Populate(item)).CallDeferred();
			}
		};
		_tree.ItemSelected += () =>
		{
			if (_tree.GetSelected() is { } item && _nodeOf.TryGetValue(item, out ulong id))
			{
				_selected = id;
				Inspect();
			}
		};
		_search.TextSubmitted += _ => Search();
		_visible.Toggled += on =>
		{
			switch (InstanceFromId(_selected))
			{
				case CanvasItem canvas:
					canvas.Visible = on;
					break;
				case Node3D spatial:
					spatial.Visible = on;
					break;
			}
		};
	}

	public override void _Ready() => Rebuild();

	private void Rebuild()
	{
		_tree.Clear();
		_nodeOf.Clear();
		Node root = GetTree().Root;
		TreeItem item = Add(null, root);
		item.Collapsed = false;
		Populate(item);
	}

	private TreeItem Add(TreeItem? parent, Node node)
	{
		TreeItem item = _tree.CreateItem(parent);
		item.SetText(0, $"{node.Name}  ({node.GetClass()})");
		_nodeOf[item] = node.GetInstanceId();
		if (node.GetChildCount() > 0)
		{
			_tree.CreateItem(item).SetText(0, Placeholder);
			item.Collapsed = true;
		}

		return item;
	}

	/// <summary>Replaces an item's placeholder with its node's children, once.</summary>
	private void Populate(TreeItem item)
	{
		if (item.GetFirstChild() is not { } first || first.GetText(0) != Placeholder ||
			!_nodeOf.TryGetValue(item, out ulong id) || InstanceFromId(id) is not Node node)
		{
			return;
		}

		first.Free();
		foreach (Node child in node.GetChildren())
		{
			Add(item, child);
		}
	}

	private void Search()
	{
		string wanted = _search.Text;
		if (wanted.Length == 0)
		{
			Rebuild();
			return;
		}

		_tree.Clear();
		_nodeOf.Clear();
		TreeItem results = _tree.CreateItem();
		results.SetText(0, $"nodes named like '{wanted}'");
		var queue = new Queue<Node>();
		queue.Enqueue(GetTree().Root);
		int found = 0;
		while (queue.Count > 0 && found < SearchLimit)
		{
			Node node = queue.Dequeue();
			if (node.Name.ToString().Contains(wanted, System.StringComparison.OrdinalIgnoreCase))
			{
				TreeItem item = Add(results, node);
				item.SetText(0, $"{node.GetPath()}  ({node.GetClass()})");
				++found;
			}

			foreach (Node child in node.GetChildren())
			{
				queue.Enqueue(child);
			}
		}

		results.SetText(0, $"{found}{(found >= SearchLimit ? "+" : string.Empty)} node(s) named like '{wanted}'");
	}

	public override void Refresh() => Inspect();

	private void Inspect()
	{
		if (InstanceFromId(_selected) is not Node node)
		{
			_path.Text = _selected == 0 ? "pick a node" : "(freed)";
			_properties.Show(System.Array.Empty<string[]>());
			_visible.Disabled = true;
			return;
		}

		_path.Text = node.GetPath();
		bool? shown = node switch
		{
			CanvasItem canvas => canvas.Visible,
			Node3D spatial => spatial.Visible,
			_ => null,
		};
		_visible.Disabled = shown is null;
		_visible.SetPressedNoSignal(shown ?? false);

		var rows = new List<string[]>();
		foreach (Godot.Collections.Dictionary property in node.GetPropertyList())
		{
			var usage = (PropertyUsageFlags)(long)property["usage"];
			if ((usage & PropertyUsageFlags.Editor) == 0)
			{
				continue;
			}

			string name = (string)property["name"];
			string value = node.Get(name).ToString();
			rows.Add(new[] { name, value.Length > 160 ? value[..160] + "…" : value });
		}

		_properties.Show(rows);
	}
}
