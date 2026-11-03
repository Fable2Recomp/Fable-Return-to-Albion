namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// The element-written box-manager screens — hero status, stats, promises, quest summary, the
/// king's schedule and the ones <see cref="Fable3BoxScreenModel"/> composes — and the save menu,
/// drawn on the one menu template (<see cref="Fable3ListScreen"/>) from what the
/// guest states (<see cref="Fable3ScreenModel"/>). The screen showing is the box manager's
/// (display_box_hooks.cpp names its class; <see cref="Fable3ScreenBoxes.ScreenRoot"/> is its
/// element root); the rows, the cursor row and the values are its element writes. The pad reaches
/// the guest unchanged, which moves its own cursor and closes its own screen.
/// </summary>
public partial class Fable3StatedScreen : Fable3ListScreen
{
	private readonly Dictionary<string, Fable3ScreenModel.Probe> _stated = new();
	private ulong _elements = ulong.MaxValue;
	private string? _root;
	private string? _boxRoot;
	private Fable3ScreenPage? _page;

	protected override string LogTag => "fable3-screens";

	protected override string LogContext => $"{_root} ";

	protected override Fable3ScreenPage? Compose(double delta)
	{
		if (Fable3Elements.Generation == _elements && Fable3ScreenBoxes.ScreenRoot == _boxRoot)
		{
			return _page;
		}

		_elements = Fable3Elements.Generation;
		_boxRoot = Fable3ScreenBoxes.ScreenRoot;

		// The box manager's screen first; else the save menu, which is no box — its root's own
		// state is the statement (1..4 open, 5 closed; Fable3BoxScreenModel.SaveMenuOpen).
		string? root = _boxRoot;
		if (root is null && Fable3Elements.Latest(Fable3BoxScreenModel.SaveMenu) is { State: >= 1 and <= 4 })
		{
			root = Fable3BoxScreenModel.SaveMenu;
		}

		if (root is not null)
		{
			Collect(root);
		}

		_root = root;
		_page = root is null ? null : Fable3ScreenModel.Compose(root, _stated, Resolve);
		return _page;
	}

	/// <summary>
	/// The values stated under <paramref name="root"/> in the presentation that last wrote under
	/// its first segment — a reopened screen gets a new presentation and the old one's elements
	/// stay in the model, and two .bgf share a root (foodquerybox's and proposalquerybox's
	/// `InfoBox`), so only the latest one is the screen showing.
	/// </summary>
	private void Collect(string root)
	{
		_stated.Clear();
		int dot = root.IndexOf('.');
		uint presentation = Fable3Elements.LatestPresentationOf(dot < 0 ? root : root[..dot]);
		if (presentation == 0u)
		{
			return;
		}

		string prefix = root + ".";
		foreach (KeyValuePair<(uint Presentation, string Path), Fable3Elements.Element> entry in Fable3Elements.Of(presentation))
		{
			string path = entry.Key.Path;
			if (path == root || path.StartsWith(prefix, System.StringComparison.Ordinal))
			{
				Fable3Elements.Element element = entry.Value;
				_stated[path] = new Fable3ScreenModel.Probe(element.State, element.Hidden, element.Text, element.Time);
			}
		}
	}
}
