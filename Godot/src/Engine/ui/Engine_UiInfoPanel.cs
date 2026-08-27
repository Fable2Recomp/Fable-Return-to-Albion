using Godot;

namespace Refii.Ui;

/// <summary>
/// The owned INFO PANEL — the block of live text an in-game screen states about whatever the
/// cursor is on: an item's name and description, a property's price, a quest's summary.
///
/// It paints OVER a mirrored tree, unlike a book, which replaces the screen's chrome outright:
/// it stands one layer in front and hides only the mirrored elements it actually drew, so a
/// moved path costs one field rather than the panel. `Describe()` says how many resolved.
///
/// A screen cannot have both — once its book is standing the same text is painted into the
/// book's clones. The caller decides, since only it knows whether the book stood this frame.
/// </summary>
public sealed class Engine_UiInfoPanel
{
	/// <summary>The host the panel is parented to and takes its art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>The presentation the standing panel was built against, 0 for none.</summary>
	public uint Address { get; private set; }

	/// <summary>The screen the standing panel belongs to.</summary>
	public uint Screen { get; private set; } = uint.MaxValue;

	private Engine_UiScreenPanel? _panel;

	private string? _reported;

	/// <summary>Takes the panel down — the screen behind it is no longer the one it serves.</summary>
	public void Withdraw()
	{
		if (_panel is not null && GodotObject.IsInstanceValid(_panel))
		{
			_panel.Visible = false;
		}
	}

	/// <summary>Forces the next drive to re-report — the presentation it was built against has
	/// been rebuilt under it.</summary>
	public void Invalidate() => _reported = null;

	/// <summary>
	/// The info-panel fields each in-game screen states, by the guest's own dotted paths.
	///
	/// Screen 15's writer is `sub_829B6B50`: each field is a `sub_82426798(path, gui)` resolve
	/// then a text write (0x829b6cc8 ItemName via `sub_82425F40`, 0x829b7094 ItemDescription via
	/// `sub_82425DD0`). `ItemValueName` is the constant tag `GUI_SCREEN_SHOP_VALUE`; `ItemValue`
	/// is '$' + `ShopItemComponent.BasePrice`, and the guest hides both when the item has no
	/// `ShopItemComponent`.
	///
	/// `Page2.Contents.StarRating` is deliberately excluded: the guest writes it with
	/// `sub_82426BA8`, the METER primitive, so it is a playhead and belongs with the HUD's bound
	/// values. It stays mirrored.
	///
	/// The description is the one multiline field: per-stat lines, then the augment list behind
	/// `&lt;bullet.img&gt;` bullets, then the item's own description, joined with '\n'.
	/// </summary>
	/// <param name="screen">
	/// The screen's own document, for expanding <see cref="Engine_UiScreenTemplate.PanelSubtrees"/>.
	/// Null before it is built, which yields the enumerated fields alone.
	/// </param>
	public static System.Collections.Generic.IReadOnlyList<Engine_UiScreenPanel.Field>?
		Fields(uint screenId, Engine_UiScreen? screen)
	{
		if (Engine_UiTemplates.Screen(screenId) is not { } template)
		{
			return null;
		}

		var fields = new System.Collections.Generic.List<Engine_UiScreenPanel.Field>(
			template.PanelFields);

		if (screen is not null)
		{
			foreach (string root in template.PanelSubtrees)
			{
				fields.AddRange(Engine_UiScreenPanel.FieldsUnder(screen, root));
			}
		}

		return fields.Count > 0 ? fields : null;
	}

	/// <summary>
	/// Stands the owned info panel over a screen and hides the guest's copies of the fields it
	/// drew. A path not in this screen's document, or a field the guest has not written yet,
	/// leaves the mirrored element alone.
	/// </summary>
	public void Drive(uint screenId, uint address, Engine_UiDrawnPresentation drawn)
	{
		// A node held here can be freed under it: the built tree is freed on every guest state
		// change, taking the subtree with it, so validity is checked before use — the free is
		// not ours to observe.
		if (_panel is not null && !GodotObject.IsInstanceValid(_panel))
		{
			_panel = null;
			Address = 0;
			Screen = uint.MaxValue;
			_reported = null;
		}

		if (Fields(screenId, drawn.Screen) is not { } fields)
		{
			if (_panel is not null)
			{
				_panel.Visible = false;
			}

			return;
		}

		if (_panel is null || Address != address || Screen != screenId)
		{
			if (_panel is not null)
			{
				_panel.QueueFree();
			}

			_panel = new Engine_UiScreenPanel
			{
				Name = "ScreenPanel",
				Fields = fields,
				Theme = Host!.PatternBookFor(drawn.Name),
			};

			// Parented to the HOST, not the built tree: `drawn.Node` is freed whenever the guest
			// changes a slide, which would take the panel with it.
			Host.AddChild(_panel);
			Address = address;
			Screen = screenId;
			_reported = null;
		}

		// Over the mirrored slab, every frame — the slab's Z is its draw depth and moves with the
		// guest's screen ordering.
		_panel.Position = new Vector3(
			0f, 0f, drawn.Node.Position.Z + Engine_UiPresentationDepth.OverlayOffset);

		_panel.Visible = true;
		if (!_panel.Read(drawn.Screen, drawn.Texts, drawn.Visible, drawn.Node) &&
			_reported is not null)
		{
			return;
		}

		// Hide everything OWNED, not just what was drawn: a field with no text this frame is
		// still ours, and gating on FieldsDrawn blinks the guest's copy back.
		foreach (string path in _panel.Owned)
		{
			if (Engine_UiTheme.FindByPath(drawn.Node, path) is { } mirrored)
			{
				mirrored.Visible = false;
			}
		}

		string described = _panel.Describe();
		if (_reported != described)
		{
			_reported = described;
			GD.Print($"[game-ui] Godot owns screen {screenId}'s info panel — {described}");
		}
	}
}
