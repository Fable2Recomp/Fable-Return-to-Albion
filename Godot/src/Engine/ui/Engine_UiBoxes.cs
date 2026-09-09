using Godot;

namespace Refii.Ui;

// The owned box views — the Node3D each surface in Engine_UiOwnedSurfaces.cs drives. Every one
// implements IUiSurfaceView, clones its art from the file-only pattern book, and degrades to
// text-only stand-ins rather than to null when the art is missing.

/// <summary>
/// The MenuBox (screen id 9), Godot-owned: shipped Lua opens an option list — the bed's sleep
/// menu, the world map's destination stop, DLC prompts — and Godot draws it and answers.
/// </summary>
/// <remarks>
/// The guest states the list: option labels are live text on
/// `Group/Choices/Multiplayer_Button{i+1}/Choice1/Choice1`, the title on `Group/Title`, and the
/// model behind them (count, selection, cancelable, per-option enabled) comes from the controller
/// struct through <see cref="Engine_UiSource.Current.GetMenuBox"/>. Six options is the guest's cap.
///
/// Dispatch is the guest's own accept: the host writes the index and calls `sub_823DC720`, which
/// re-checks the enabled byte, fires the Lua callback, publishes the result (player message
/// 35/101) and tears the box down.
/// </remarks>
public partial class Engine_UiMenuBox : Node3D, IUiSurfaceView
{
	/// <summary>The bank entry name of the guest's presentation.</summary>
	public const string Presentation = "menubox";

	/// <summary>The authored option cap — the guest refuses a seventh.</summary>
	public const int MaxOptions = 6;

	/// <summary>The remaster's readability bump on the option labels — see FromPatternBook.</summary>
	private const float ChoiceTextScale = 2f;

	public const string GroupName = "Group";

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _group;

	/// <summary>Document ordinals: the title, and per option its button root and label.</summary>
	public sealed class Joins
	{
		public int Title = -1;
		public readonly int[] Buttons = new int[MaxOptions];
		public readonly int[] Labels = new int[MaxOptions];
		public int Cancel = -1;
	}

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? group = Engine_UiStatedPanel.FindByName(screen.Roots, GroupName);
		UiNode? choices = Engine_UiStatedPanel.Child(group, "Choices");
		UiNode? title = Engine_UiStatedPanel.Child(group, "Title");
		if (choices is null || title is null)
		{
			return null;
		}

		var joins = new Joins { Title = title.Ordinal };
		for (int i = 0; i < MaxOptions; ++i)
		{
			UiNode? button = Engine_UiStatedPanel.Child(choices, $"Multiplayer_Button{i + 1}");
			UiNode? label = Engine_UiStatedPanel.Child(
				Engine_UiStatedPanel.Child(button, "Choice1"), "Choice1");
			if (button is null || label is null)
			{
				return null;
			}

			joins.Buttons[i] = button.Ordinal;
			joins.Labels[i] = label.Ordinal;
		}

		joins.Cancel = Engine_UiStatedPanel.Child(
			Engine_UiStatedPanel.Child(group, "OK/CANCEL"), "Negative")?.Ordinal ?? -1;
		return joins;
	}

	public static Engine_UiMenuBox FromPatternBook(Engine_UiTheme? book, string name = "MenuBox")
	{
		var panel = new Engine_UiMenuBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the menu box has no art";
			return panel;
		}

		Node3D? source = book.ArtSourceNode(GroupName);
		Node3D? clone = book.Art(GroupName);
		if (source is not null && clone is not null)
		{
			clone.Transform = Engine_UiTheme.CanvasTransform(source);

			// Private materials with their authored alpha remembered: rows are ghosted per
			// option, and a shared material would fade the pattern book.
			Engine_UiSceneBuilder.OwnSubtreeMaterials(clone);

			// REMASTER: the option labels are authored at node scale 0.4 over an 18pt face —
			// about seven pixels at the original 720 canvas, and the shipped game showed them
			// that small too. The frame band around each row is ~24 units tall, so doubling the
			// label still sits inside it. Only the TEXT scales; the art keeps its layout.
			for (int i = 0; i < MaxOptions; ++i)
			{
				// The shipped file frames the label: `Choice1/Frame/Choice1`.
				Node3D? label =
					clone.GetNodeOrNull<Node3D>(
						$"Choices/Multiplayer_Button{i + 1}/Choice1/Frame/Choice1") ??
					clone.GetNodeOrNull<Node3D>(
						$"Choices/Multiplayer_Button{i + 1}/Choice1/Choice1");
				if (label is not null)
				{
					label.Scale *= ChoiceTextScale;
				}
			}

			panel.AddChild(clone);
			panel._group = clone;
			panel.HasShippedArt = true;
		}

		panel.LastReport = panel.HasShippedArt ? "authored group cloned" : "group NOT found";
		return panel;
	}

	/// <summary>
	/// Shows the guest's stated list: the title, one row per option with its live label, the
	/// selection on the guest's slide convention, disabled rows ghosted, and the cancel prompt only
	/// when the box allows it.
	/// </summary>
	public void ShowStated(
		string? title, string?[] labels, int count, int selected,
		uint enabledMask, bool cancelable)
	{
		if (_group is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		if (!string.IsNullOrEmpty(title) &&
			_group.GetNodeOrNull<MeshInstance3D>("Title") is { } titleNode)
		{
			LastReport = Theme.SetMeshText(titleNode, title);
		}

		for (int i = 0; i < MaxOptions; ++i)
		{
			if (_group.GetNodeOrNull<Node3D>($"Choices/Multiplayer_Button{i + 1}") is not { } button)
			{
				continue;
			}

			bool present = i < count;
			button.Visible = present;
			if (!present)
			{
				continue;
			}

			if (labels[i] is { Length: > 0 } label &&
				(button.GetNodeOrNull<MeshInstance3D>("Choice1/Frame/Choice1") ??
				 button.GetNodeOrNull<MeshInstance3D>("Choice1/Choice1")) is { } labelNode)
			{
				Theme.SetMeshText(labelNode, label);
			}

			// Host-owned highlight: the selected row at full strength, the rest dimmed, a
			// disabled option dimmer still.
			bool enabled = (enabledMask & (1u << i)) != 0;
			Engine_UiSceneBuilder.FadeSubtree(
				button, !enabled ? 0.35f : i == selected ? 1f : 0.7f);
		}

		if (_group.GetNodeOrNull<Node3D>("OK/CANCEL/Negative") is { } cancel)
		{
			cancel.Visible = cancelable;
		}

		Visible = true;
	}
}

/// <summary>
/// The in-game message box (modal mode 2), Godot-owned: the guest opens it, fits its text,
/// and states the size; Godot draws it and dismisses through the guest's own input router.
/// </summary>
/// <remarks>
/// The opener's fitter (`sub_823CEFA8`) measures the text, writes it into the first of
/// `MessageText1..6` whose authored box fits, and sizes the `paper` frame by putting it on that
/// size's element slide. The live state therefore carries the chosen text ordinal (the one with
/// non-empty text) and the nine-slice's resolved scale (the live transform on `paperbox`); this
/// view re-states both on its clone and never re-measures.
///
/// The `Action` row (X) belongs to the marketplace-offer box alone (query id 42), which stays
/// mirrored, so the row is hidden here unconditionally.
/// </remarks>
public partial class Engine_UiMessageBox : Node3D, IUiSurfaceView
{
	/// <summary>The bank entry name of the guest's presentation.</summary>
	public const string Presentation = "messageboxmulti";

	/// <summary>Authored group names, as `messageboxmulti.bgf` names them.</summary>
	public const string PaperName = "paper";
	public const string DimName = "Background";

	/// <summary>The authored text-size variants, smallest first.</summary>
	public const int TextSizes = 6;

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _paper;

	/// <summary>Document ordinals: the six size-variant texts, and `paperbox` (with its `paper`
	/// parent as fallback) for the guest's live scale — the size slide may write either.</summary>
	public sealed class Joins
	{
		public readonly int[] Texts = new int[TextSizes];
		public int PaperBox = -1;
		public int Paper = -1;
	}

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? paper = Engine_UiStatedPanel.FindByName(screen.Roots, PaperName);
		UiNode? box = Engine_UiStatedPanel.Child(paper, "MessageBox");
		UiNode? paperBox = Engine_UiStatedPanel.Child(paper, "paperbox");
		if (paper is null || box is null || paperBox is null)
		{
			return null;
		}

		var joins = new Joins { PaperBox = paperBox.Ordinal, Paper = paper.Ordinal };
		for (int i = 0; i < TextSizes; ++i)
		{
			UiNode? text = Engine_UiStatedPanel.Child(box, $"MessageText{i + 1}");
			if (text is null)
			{
				return null;
			}

			joins.Texts[i] = text.Ordinal;
		}

		return joins;
	}

	public static Engine_UiMessageBox FromPatternBook(Engine_UiTheme? book, string name = "MessageBox")
	{
		var panel = new Engine_UiMessageBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the box has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in new[] { DimName, PaperName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			panel.AddChild(clone);
			if (piece == PaperName)
			{
				panel._paper = clone;
			}

			++cloned;
		}

		panel.HasShippedArt = cloned == 2;
		panel.LastReport = $"{cloned}/2 authored groups cloned";

		// The X row is the unowned marketplace box's; see the class remarks.
		if (panel._paper?.GetNodeOrNull<Node3D>("MessageBox/Action") is { } action)
		{
			action.Visible = false;
		}

		return panel;
	}

	/// <summary>
	/// Shows the guest's stated text on the guest's chosen size variant, at the guest's
	/// stated paper scale (null keeps the authored one).
	/// </summary>
	public void ShowStated(string body, int textIndex, Vector2? paperScale)
	{
		if (_paper is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		for (int i = 0; i < TextSizes; ++i)
		{
			if (_paper.GetNodeOrNull<Node3D>($"MessageBox/MessageText{i + 1}") is { } text)
			{
				text.Visible = i == textIndex;
			}
		}

		if (_paper.GetNodeOrNull<MeshInstance3D>($"MessageBox/MessageText{textIndex + 1}") is { } node)
		{
			LastReport = Theme.SetMeshText(node, body);
		}

		if (paperScale is { } scale && _paper.GetNodeOrNull<Node3D>("paperbox") is { } paperBox)
		{
			// THROUGH THE BUILDER'S WRITER, not `Scale =`: the origin folds in the pivot against
			// the basis, and a bare scale write drifts this slice sideways. See SetLiveScale.
			Engine_UiSceneBuilder.SetLiveScale(paperBox, new Vector3(scale.X, scale.Y, 1f));
		}

		Visible = true;
	}

}

/// <summary>
/// The in-game query box, Godot-owned: the guest opens it and states its text; Godot draws
/// it and answers through the guest's own input router.
/// </summary>
/// <remarks>
/// The body is written by the guest's `querybox.lua` as a live `textstring` on the
/// `QueryBox/Text` element, so display text is read from the live elements — never a host-side
/// tag lookup. The art is `querybox.bgf`'s: the `QueryBox` group (text and the authored A/B
/// choice rows), the 50%-opacity `Background` dim, and the nine-slice `Paper` frame, cloned from
/// the file-only pattern book at their authored transforms
/// (<see cref="Engine_UiTheme.CanvasTransform"/>; a zero-rooted clone loses the placement).
///
/// The file authors three choice rows — `Choices` (A yes / B no), `XAChoices` and
/// `ReloadChoices`, the latter two shipped inactive. Which row a box uses is the guest's
/// statement (the opener slides the root for ids 20 and 13), read off the live active flags and
/// passed to <see cref="ShowStated"/>.
/// </remarks>
public partial class Engine_UiQueryBox : Node3D, IUiSurfaceView
{
	/// <summary>The bank entry name of the guest's presentation.</summary>
	public const string Presentation = "querybox";

	/// <summary>Authored group names, as `querybox.bgf` names them.</summary>
	public const string BoxName = "QueryBox";
	public const string DimName = "Background";
	public const string PaperName = "Paper";

	/// <summary>Fonts and strings for the re-meshed text.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>True when the shipped art was cloned; false means text-only stand-ins.</summary>
	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _box;

	/// <summary>
	/// The three authored choice rows and their child choices. The opener puts the root on a slide
	/// (`Choices` is the default A/B pair; box 20 gets `XAChoices`, box 13 `ReloadChoices`) and the
	/// live active flags say which one stood up.
	/// </summary>
	public static readonly (string Row, string First, string Second)[] ChoiceRows =
	{
		("Choices", "Affirmative", "Negative"),
		("XAChoices", "EquipAndContinue", "Continue"),
		("ReloadChoices", "Reload", "Cancel"),
	};

	/// <summary>One row's document ordinals: the row root and its two label texts.</summary>
	public readonly record struct RowJoin(string Name, int Row, int First, int Second);

	/// <summary>
	/// Document ordinals of the live-text elements, resolved by path: the screen has five
	/// elements named "Text", so a flat name join re-meshes somebody else's label.
	/// </summary>
	public sealed class Joins
	{
		public int Body = -1;
		public readonly System.Collections.Generic.List<RowJoin> Rows = new();
	}

	/// <summary>Resolves the live-text ordinals from the parsed document. Null when the
	/// authored structure is not what this class knows how to drive.</summary>
	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? box = Engine_UiStatedPanel.FindByName(screen.Roots, BoxName);
		UiNode? body = Engine_UiStatedPanel.Child(box, "Text");
		if (box is null || body is null)
		{
			return null;
		}

		var joins = new Joins { Body = body.Ordinal };
		foreach ((string rowName, string first, string second) in ChoiceRows)
		{
			UiNode? row = Engine_UiStatedPanel.Child(box, rowName);
			UiNode? firstLabel = Engine_UiStatedPanel.Child(Engine_UiStatedPanel.Child(row, first), "Text");
			UiNode? secondLabel = Engine_UiStatedPanel.Child(Engine_UiStatedPanel.Child(row, second), "Text");
			if (row is not null && firstLabel is not null && secondLabel is not null)
			{
				joins.Rows.Add(new RowJoin(
					rowName, row.Ordinal, firstLabel.Ordinal, secondLabel.Ordinal));
			}
		}

		// The default A/B row is the one every owned box id needs.
		return joins.Rows.Count > 0 && joins.Rows[0].Name == ChoiceRows[0].Row ? joins : null;
	}

	/// <summary>
	/// Builds the view from the file-only pattern book: the three authored groups cloned at their
	/// authored canvas transforms. A missing book or art degrades to an empty view that reports
	/// itself.
	/// </summary>
	public static Engine_UiQueryBox FromPatternBook(Engine_UiTheme? book, string name = "QueryBox")
	{
		var panel = new Engine_UiQueryBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the box has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in new[] { BoxName, DimName, PaperName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			// Art() zeroes the clone's root, and this screen authors its depth ordering across the
			// group roots, so the authored placement is restored.
			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			panel.AddChild(clone);
			if (piece == BoxName)
			{
				panel._box = clone;
			}

			++cloned;
		}

		panel.HasShippedArt = cloned == 3;
		panel.LastReport = $"{cloned}/3 authored groups cloned";

		// The A/B row is the pause family's; the alternates stay down until a box using them is
		// owned.
		panel.SetRow("Choices", true);
		panel.SetRow("XAChoices", false);
		panel.SetRow("ReloadChoices", false);

		return panel;
	}

	/// <summary>
	/// Puts the guest's stated strings on the authored elements and stands the row the guest chose.
	/// Empty label strings keep the authored tags, which the book's Babel strings already resolved
	/// at build time.
	/// </summary>
	public void ShowStated(string body, string activeRow, string? first, string? second)
	{
		if (_box is null || Theme is null)
		{
			// No art refuses to stand rather than drawing an empty panel: the caller's mirror
			// fallback and the guest's input stay live only while this view is invisible.
			LastReport = "no art — not standing in";
			return;
		}

		// `QueryBox.Open` writes `g_Theme:GetNormalTextColor()` into the body's `textcolor.r/g/b`
		// (`querybox.dis main.1 pc32..53`). The colour must lead here, since the re-mesh reads it.
		string report = SetMeshTextSafe(
			BodyNode(), body, Engine_UiBoxConstants.NormalText);

		(string Row, string First, string Second) descriptor = ChoiceRows[0];
		foreach ((string Row, string First, string Second) candidate in ChoiceRows)
		{
			SetRow(candidate.Row, candidate.Row == activeRow);
			if (candidate.Row == activeRow)
			{
				descriptor = candidate;
			}
		}

		if (!string.IsNullOrEmpty(first))
		{
			SetMeshTextSafe(LabelNode(descriptor.Row, descriptor.First), first);
		}

		if (!string.IsNullOrEmpty(second))
		{
			SetMeshTextSafe(LabelNode(descriptor.Row, descriptor.Second), second);
		}

		LastReport = report;
		Visible = true;
	}

	private MeshInstance3D? BodyNode() => _box?.GetNodeOrNull<MeshInstance3D>("Text");

	private MeshInstance3D? LabelNode(string row, string choice) =>
		_box?.GetNodeOrNull<MeshInstance3D>($"{row}/{choice}/Text");

	/// <summary>
	/// Re-meshes one text node, applying the script's theme colour first: `text_colour` is what
	/// <see cref="Engine_UiTheme.SetMeshText"/> reads.
	/// </summary>
	private string SetMeshTextSafe(MeshInstance3D? node, string display, Color? colour = null)
	{
		if (node is null || Theme is null)
		{
			return "text node not found";
		}

		if (colour is { } stated)
		{
			node.SetMeta("text_colour", stated);
		}

		return Theme.SetMeshText(node, display);
	}

	private void SetRow(string name, bool shown)
	{
		if (_box?.GetNodeOrNull<Node3D>(name) is { } row)
		{
			row.Visible = shown;
		}
	}

}

/// <summary>
/// The numeric adjuster (modal mode 5), Godot-owned: the guest opens it over the real-estate
/// and gift screens with a question, a value and its bounds; Godot draws it and drives it
/// through the guest's own input router.
/// </summary>
/// <remarks>
/// The widget (guiOwner+64) formats the value — including the %-suffix and +-prefix its flags
/// select — into the live `textstring` of `QueryBox/PriceWidget/Text`, and highlights the up/down
/// arrows by whether a step fits the remaining range; the question is live text on
/// `QueryBox/Text`. The value never lives host-side. Input codes through the router: 28 increment,
/// 29 decrement (the guest's accelerating step logic runs), 46 commit (the underlying screen's
/// handler reads the value off the widget), 47 cancel.
/// </remarks>
public partial class Engine_UiNumericBox : Node3D, IUiSurfaceView
{
	/// <summary>The bank entry name of the guest's presentation.</summary>
	public const string Presentation = "numericadjusterbox";

	/// <summary>Authored group names, as `numericadjusterbox.bgf` names them.</summary>
	public const string BoxName = "QueryBox";
	public const string DimName = "Background";
	public const string PaperName = "Paper";

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _box;

	/// <summary>Document ordinals of the live-stated elements.</summary>
	public sealed class Joins
	{
		public int Body = -1;
		public int Value = -1;
		public int ArrowUp = -1;
		public int ArrowDown = -1;
	}

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? box = Engine_UiStatedPanel.FindByName(screen.Roots, BoxName);
		UiNode? body = Engine_UiStatedPanel.Child(box, "Text");
		UiNode? widget = Engine_UiStatedPanel.Child(box, "PriceWidget");
		UiNode? value = Engine_UiStatedPanel.Child(widget, "Text");
		UiNode? arrows = Engine_UiStatedPanel.Child(widget, "Arrows");
		UiNode? up = Engine_UiStatedPanel.Child(arrows, "up");
		UiNode? down = Engine_UiStatedPanel.Child(arrows, "down");

		if (body is null || value is null || up is null || down is null)
		{
			return null;
		}

		return new Joins
		{
			Body = body.Ordinal,
			Value = value.Ordinal,
			ArrowUp = up.Ordinal,
			ArrowDown = down.Ordinal,
		};
	}

	public static Engine_UiNumericBox FromPatternBook(Engine_UiTheme? book, string name = "NumericBox")
	{
		var panel = new Engine_UiNumericBox { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the box has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in new[] { BoxName, DimName, PaperName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			panel.AddChild(clone);
			if (piece == BoxName)
			{
				panel._box = clone;
			}

			++cloned;
		}

		panel.HasShippedArt = cloned == 3;
		panel.LastReport = $"{cloned}/3 authored groups cloned";
		return panel;
	}

	/// <summary>Shows the guest's stated question, formatted value and arrow availability.</summary>
	public void ShowStated(string body, string value, bool canIncrement, bool canDecrement)
	{
		if (_box is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		if (body.Length != 0 && _box.GetNodeOrNull<MeshInstance3D>("Text") is { } bodyNode)
		{
			LastReport = Theme.SetMeshText(bodyNode, body);
		}

		if (_box.GetNodeOrNull<MeshInstance3D>("PriceWidget/Text") is { } valueNode)
		{
			Theme.SetMeshText(valueNode, value);
		}

		SetArrow("up", canIncrement);
		SetArrow("down", canDecrement);

		Visible = true;
	}

	private void SetArrow(string arrow, bool enabled)
	{
		if (_box?.GetNodeOrNull<Node3D>($"PriceWidget/Arrows/{arrow}") is { } node)
		{
			node.Visible = enabled;
		}
	}

}

/// <summary>
/// The received-item card (screen id 8), Godot-owned: the guest pushes it on every pickup
/// and states the item's name, description, icon and which side is up; Godot draws it and
/// presses its buttons through the handler's own screen-input method.
/// </summary>
/// <remarks>
/// `Foreground/NameAndIcon/Title` and `.../Text` carry the localised strings, `IconGraphic` the
/// item's icon as a live texture (the file's copy is a placeholder), the button prompts' active
/// flags say which of Done/Info/Action stand, and the card flip (Y) is the Foreground/CardBack
/// groups' active flags. Input: 36 done, 39 flip, 75 close — the pump's
/// "ReceiveItemsAction:&lt;code&gt;". This screen does not pause the game, so only A and Y are
/// masked while it stands.
/// </remarks>
public partial class Engine_UiReceiveItems : Node3D, IUiSurfaceView
{
	/// <summary>The bank entry name of the guest's presentation.</summary>
	public const string Presentation = "receiveitemsscreen";

	public const string FrontName = "Foreground";
	public const string BackName = "CardBack";

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Node3D? _front;
	private Node3D? _back;
	private ShaderMaterial? _iconMaterial;
	private uint _iconHash;

	/// <summary>Document ordinals for the live joins.</summary>
	public sealed class Joins
	{
		public int Title = -1;
		public int Name = -1;
		public int BackText = -1;
		public int Icon = -1;
		public int Front = -1;
		public int Back = -1;
	}

	public static Joins? Resolve(Engine_UiScreen screen)
	{
		UiNode? front = Engine_UiStatedPanel.FindByName(screen.Roots, FrontName);
		UiNode? back = Engine_UiStatedPanel.FindByName(screen.Roots, BackName);
		UiNode? nameAndIcon = Engine_UiStatedPanel.Child(front, "NameAndIcon");
		UiNode? title = Engine_UiStatedPanel.Child(nameAndIcon, "Title");
		UiNode? name = Engine_UiStatedPanel.Child(nameAndIcon, "Text");
		UiNode? icon = Engine_UiStatedPanel.Child(nameAndIcon, "IconGraphic");
		UiNode? backText = back is null
			? null
			: Engine_UiStatedPanel.FindByName(back.Children, "Text");

		if (front is null || back is null || title is null || name is null || icon is null)
		{
			return null;
		}

		return new Joins
		{
			Title = title.Ordinal,
			Name = name.Ordinal,
			BackText = backText?.Ordinal ?? -1,
			Icon = icon.Ordinal,
			Front = front.Ordinal,
			Back = back.Ordinal,
		};
	}

	public static Engine_UiReceiveItems FromPatternBook(Engine_UiTheme? book, string name = "ReceiveItems")
	{
		var panel = new Engine_UiReceiveItems { Name = name, Visible = false, Theme = book };

		if (book is null)
		{
			panel.LastReport = "no pattern book — the card has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in new[] { FrontName, BackName })
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			panel.AddChild(clone);
			if (piece == FrontName)
			{
				panel._front = clone;
			}
			else
			{
				panel._back = clone;
				clone.Visible = false;
			}

			++cloned;
		}

		// The icon's material is privatised once: the clone shares the pattern book's, and swapping
		// a texture on a shared material would restyle the book.
		if (panel._front?.GetNodeOrNull<MeshInstance3D>("NameAndIcon/IconGraphic") is { } iconMesh &&
			iconMesh.MaterialOverride is ShaderMaterial shared)
		{
			panel._iconMaterial = (ShaderMaterial)shared.Duplicate();
			iconMesh.MaterialOverride = panel._iconMaterial;
		}

		panel.HasShippedArt = cloned == 2;
		panel.LastReport = $"{cloned}/2 authored groups cloned" +
			$"{(panel._iconMaterial is null ? ", icon material NOT owned" : string.Empty)}";
		return panel;
	}

	/// <summary>Puts the guest's stated card on screen: strings, icon, and which side is up.</summary>
	public void ShowStated(
		string? title, string? name, string? backText,
		uint iconHash, Texture2D? icon,
		bool frontUp, bool backUp)
	{
		if (_front is null || Theme is null)
		{
			LastReport = "no art — not standing in";
			return;
		}

		if (!string.IsNullOrEmpty(title) &&
			_front.GetNodeOrNull<MeshInstance3D>("NameAndIcon/Title") is { } titleNode)
		{
			LastReport = Theme.SetMeshText(titleNode, title);
		}

		if (!string.IsNullOrEmpty(name) &&
			_front.GetNodeOrNull<MeshInstance3D>("NameAndIcon/Text") is { } nameNode)
		{
			Theme.SetMeshText(nameNode, name);
		}

		if (!string.IsNullOrEmpty(backText) && _back is not null &&
			FindMesh(_back, "Text") is { } backNode)
		{
			Theme.SetMeshText(backNode, backText);
		}

		if (icon is not null && iconHash != _iconHash && _iconMaterial is not null)
		{
			_iconHash = iconHash;
			_iconMaterial.SetShaderParameter("diffuse_tex", icon);
		}

		// THE BACK CARRIES THE SAME THREE BUTTONS AS THE FRONT, three canvas units apart, and
		// only its buttons and its text are authored on — its parchment and group are off. So
		// two groups both reading globally active does not draw a second card: it draws a
		// second copy of Use/Info/Take over the first, which is the ghosted labels.
		//
		// The flip is a CROSSFADE, so the guest having both active at once is correct; what the
		// owned clone lacks is the live OPACITY that would separate them, because a surface
		// builds its clone binding and then throws it away. Until the clone carries opacity,
		// the front wins outright: the back is the reverse of the card and belongs on screen
		// only once the front is down.
		_front.Visible = frontUp;
		if (_back is not null)
		{
			_back.Visible = backUp && !frontUp;
		}

		Visible = frontUp || backUp;
	}

	private static MeshInstance3D? FindMesh(Node node, string name)
	{
		if (node is MeshInstance3D mesh && node.Name == name)
		{
			return mesh;
		}

		foreach (Node child in node.GetChildren())
		{
			if (FindMesh(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}
}

/// <summary>
/// A display-only owned panel: authored art cloned from the pattern book, live text the
/// guest states re-meshed onto it, no input of its own.
/// </summary>
/// <remarks>
/// The intermission strip and the info/acceptance display box are this shape: the guest queues
/// them, writes one or two live strings, and closes them itself. Owning one is a configuration —
/// which presentation, which authored groups to clone, which text elements to follow.
/// </remarks>
public partial class Engine_UiStatedPanel : Node3D, IUiSurfaceView
{
	/// <summary>
	/// One followed text: the '/'-separated authored path from a cloned group's root to the node,
	/// and the theme colour the script writes onto it — `displaybox.dis main.6 pc3..56` writes
	/// `g_Theme:GetSubheadingColor()` onto `Title` and `g_Theme:GetNormalTextColor()` onto `Text`.
	/// Null colour leaves the authored `text_colour` standing.
	/// </summary>
	/// <param name="BoxWidth">
	/// A wrap width the script states over the authored one, in canvas units: `PrepareDisplayBox`
	/// writes `boxwidth = 1.4` (`displaybox.dis main.6 pc57..62`) onto the body before any text.
	/// Null keeps the authored box.
	/// </param>
	public readonly record struct TextBinding(
		string Group, string Path, Color? Colour = null, float? BoxWidth = null);

	public sealed class Config
	{
		public string Presentation = string.Empty;

		/// <summary>Top-level authored groups to clone at their authored transforms.</summary>
		public string[] Groups = System.Array.Empty<string>();

		public TextBinding[] Texts = System.Array.Empty<TextBinding>();

		/// <summary>
		/// Wear the HUD TopBox's look: the authored slab art is hidden, the texts take the
		/// TopBox's sizes and colours (<see cref="Engine_UiHudTextStyle"/>; a binding whose
		/// path ends in `Title` is the title, the rest are body), and a rounded panel is fitted
		/// behind them. One style for every in-game notice.
		/// </summary>
		public bool HudStyle;
	}

	private Engine_UiRoundedPanel? _panel;

	/// <summary>Document ordinals for the config's texts, in the same order.</summary>
	public sealed class Joins
	{
		public int[] Texts = System.Array.Empty<int>();
	}

	public Engine_UiTheme? Theme { get; set; }

	public bool HasShippedArt { get; private set; }

	public string LastReport { get; private set; } = string.Empty;

	private Config? _config;

	private readonly System.Collections.Generic.Dictionary<string, Node3D> _groups = new();

	public static Joins? Resolve(Engine_UiScreen screen, Config config)
	{
		var joins = new Joins { Texts = new int[config.Texts.Length] };
		for (int i = 0; i < config.Texts.Length; ++i)
		{
			UiNode? node = FindByName(screen.Roots, config.Texts[i].Group);
			foreach (string step in config.Texts[i].Path.Split('/'))
			{
				node = Child(node, step);
			}

			if (node is null)
			{
				return null;
			}

			joins.Texts[i] = node.Ordinal;
		}

		return joins;
	}

	public static Engine_UiStatedPanel FromPatternBook(
		Engine_UiTheme? book, Config config, string name)
	{
		var panel = new Engine_UiStatedPanel
		{
			Name = name,
			Visible = false,
			Theme = book,
			_config = config,
		};

		if (book is null)
		{
			panel.LastReport = "no pattern book — the panel has no art";
			return panel;
		}

		int cloned = 0;
		foreach (string piece in config.Groups)
		{
			Node3D? source = book.ArtSourceNode(piece);
			Node3D? clone = book.Art(piece);
			if (source is null || clone is null)
			{
				continue;
			}

			clone.Transform = Engine_UiTheme.CanvasTransform(source);
			panel.AddChild(clone);
			panel._groups[piece] = clone;
			++cloned;
		}

		panel.HasShippedArt = cloned == config.Groups.Length;
		panel.LastReport = $"{cloned}/{config.Groups.Length} authored groups cloned";
		if (config.HudStyle)
		{
			panel.HideArtExceptTexts();
		}

		return panel;
	}

	/// <summary>
	/// Hides every mesh of the cloned art that is not one of the bound texts — the slab, its
	/// frame and corners. The texts stay where the presentation authored them.
	/// </summary>
	private void HideArtExceptTexts()
	{
		if (_config is null)
		{
			return;
		}

		var keep = new System.Collections.Generic.HashSet<MeshInstance3D>();
		foreach (TextBinding binding in _config.Texts)
		{
			if (_groups.TryGetValue(binding.Group, out Node3D? group) &&
				group.GetNodeOrNull<MeshInstance3D>(binding.Path) is { } node)
			{
				keep.Add(node);
			}
		}

		foreach (Node3D group in _groups.Values)
		{
			HideMeshes(group, keep);
		}
	}

	private static void HideMeshes(Node node, System.Collections.Generic.HashSet<MeshInstance3D> keep)
	{
		if (node is MeshInstance3D mesh && !keep.Contains(mesh))
		{
			mesh.Visible = false;
		}

		foreach (Node child in node.GetChildren())
		{
			HideMeshes(child, keep);
		}
	}

	/// <summary>Fits the rounded panel behind whatever text is currently showing.</summary>
	private void FitHudPanel()
	{
		if (_config is null)
		{
			return;
		}

		Rect2? bounds = null;
		float behind = 1.5f;
		foreach (TextBinding binding in _config.Texts)
		{
			if (!_groups.TryGetValue(binding.Group, out Node3D? group) ||
				group.GetNodeOrNull<MeshInstance3D>(binding.Path) is not { } node ||
				!node.Visible || node.Mesh is null)
			{
				continue;
			}

			Aabb box = node.GlobalTransform * node.Mesh.GetAabb();
			if (box.Size.X <= 0f || box.Size.Y <= 0f)
			{
				continue;
			}

			var rect = new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
			bounds = bounds is null ? rect : bounds.Value.Merge(rect);
			behind = System.Math.Min(behind, node.GlobalPosition.Z - 0.5f);
		}

		if (bounds is not { } fit)
		{
			if (_panel is not null)
			{
				_panel.Visible = false;
			}

			return;
		}

		if (_panel is null)
		{
			_panel = new Engine_UiRoundedPanel { Name = "hud_panel" };
			AddChild(_panel);
		}

		// The panel is a child of this node; Fit positions it in the parent's space, and the
		// bounds above are global — so the parent's transform is taken back out.
		Transform3D inverse = GlobalTransform.AffineInverse();
		Vector3 a = inverse * new Vector3(fit.Position.X, fit.Position.Y, behind);
		Vector3 b = inverse * new Vector3(fit.End.X, fit.End.Y, behind);
		var local = new Rect2(new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)),
			new Vector2(Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y)));
		_panel.Fit(local, a.Z);
	}

	/// <summary>Puts the guest's stated strings on the followed elements, in config order.
	/// A null entry leaves that element as it stands.</summary>
	public void ShowStated(string?[] texts)
	{
		if (_config is null || Theme is null || _groups.Count == 0)
		{
			LastReport = "no art — not standing in";
			return;
		}

		for (int i = 0; i < _config.Texts.Length && i < texts.Length; ++i)
		{
			if (texts[i] is not { } display)
			{
				continue;
			}

			if (_groups.TryGetValue(_config.Texts[i].Group, out Node3D? group) &&
				group.GetNodeOrNull<MeshInstance3D>(_config.Texts[i].Path) is { } node)
			{
				// The theme colour goes on first: `text_colour` is what the re-mesh reads.
				if (_config.Texts[i].Colour is { } colour)
				{
					node.SetMeta("text_colour", colour);
				}

				if (_config.HudStyle)
				{
					bool isTitle = _config.Texts[i].Path.EndsWith("Title", System.StringComparison.OrdinalIgnoreCase);
					node.SetMeta("text_size", isTitle ? Engine_UiHudTextStyle.TitleSize : Engine_UiHudTextStyle.BodySize);
					node.SetMeta("text_colour", isTitle ? Engine_UiHudTextStyle.TitleColour : Engine_UiHudTextStyle.BodyColour);
				}

				if (_config.Texts[i].BoxWidth is { } width)
				{
					Vector2 box = node.HasMeta("text_box") ? (Vector2)node.GetMeta("text_box") : Vector2.Zero;
					node.SetMeta("text_box", new Vector2(width, box.Y));
				}

				LastReport = Theme.SetMeshText(node, display);
			}
		}

		Visible = true;
		if (_config.HudStyle)
		{
			FitHudPanel();
		}
	}

	/// <summary>Finds a named node anywhere under the roots — shared by every owned view's
	/// ordinal resolver.</summary>
	public static UiNode? FindByName(
		System.Collections.Generic.IReadOnlyList<UiNode> roots, string name)
	{
		foreach (UiNode root in roots)
		{
			if (root.Name == name)
			{
				return root;
			}

			if (FindByName(root.Children, name) is { } inner)
			{
				return inner;
			}
		}

		return null;
	}

	/// <summary>A direct child by authored name, null-tolerant for chaining.</summary>
	public static UiNode? Child(UiNode? node, string name)
	{
		if (node is null)
		{
			return null;
		}

		foreach (UiNode child in node.Children)
		{
			if (child.Name == name)
			{
				return child;
			}
		}

		return null;
	}
}
