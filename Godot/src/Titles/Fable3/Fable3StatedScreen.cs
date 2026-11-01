namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// The element-written box-manager screens — hero status, stats, promises, quest summary, the
/// king's schedule — drawn on the one menu template (<see cref="Fable3ListScreen"/>) from what the
/// guest states (<see cref="Fable3ScreenModel"/>). The screen showing is the box manager's
/// (display_box_hooks.cpp names its class; <see cref="Fable3Interaction.ScreenRoot"/> is its
/// element root); the rows, the cursor row and the values are its element writes. The pad reaches
/// the guest unchanged, which moves its own cursor and closes its own screen.
/// </summary>
public partial class Fable3StatedScreen : Fable3ListScreen
{
	private readonly Dictionary<string, Fable3ScreenModel.Probe> _stated = new();
	private ulong _elements = ulong.MaxValue;
	private string? _root;
	private Fable3ScreenPage? _page;

	protected override string LogTag => "fable3-screens";

	protected override string LogContext => $"{_root} ";

	protected override Fable3ScreenPage? Compose(double delta)
	{
		string? root = Fable3Interaction.ScreenRoot;
		if (root is null)
		{
			_root = null;
			_page = null;
			return null;
		}

		if (root == _root && Fable3Elements.Generation == _elements)
		{
			return _page;
		}

		_root = root;
		_elements = Fable3Elements.Generation;
		_stated.Clear();
		string prefix = root + ".";
		foreach (KeyValuePair<(uint Presentation, string Path), Fable3Elements.Element> entry in Fable3Elements.All)
		{
			string path = entry.Key.Path;
			if (path == root || path.StartsWith(prefix, System.StringComparison.Ordinal))
			{
				Fable3Elements.Element element = entry.Value;
				_stated[path] = new Fable3ScreenModel.Probe(element.State, element.Hidden, element.Text, element.Time);
			}
		}

		_page = Fable3ScreenModel.Compose(root, _stated, Resolve);
		return _page;
	}
}
