#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The UI tab's Modals page: who ANSWERS each message and query box (<see cref="Engine_UiModals"/>,
/// the <c>modals</c> block of <c>ui/templates.json</c>). Every shipped row is listed with what the
/// mod states over it: an override (a whole record — query or message, its confirm and cancel
/// codes), a release (<c>null</c>: the box keeps the guest's own input), or nothing.
/// </summary>
[Tool]
public partial class UiStudioModalsView : VBoxContainer
{
	private readonly UiStudioDocs _docs;

	public event Action? Edited;

	private Tree _rows = null!;
	private Label _detail = null!;
	private Button _override = null!, _release = null!, _remove = null!;
	private OptionButton _kind = null!;
	private SpinBox _confirm = null!, _cancel = null!;
	private CheckBox _hasCancel = null!;
	private Control _recordFields = null!;
	private LineEdit _newKey = null!;
	private JsonObject _shipped = new();
	private bool _filling;

	public UiStudioModalsView(UiStudioDocs docs) => _docs = docs;

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 8);
		AddChild(ModStudioUiStyles.Note("A key is \"<modal id>\" for every screen or \"<modal id>@<screen id>\" for one; a screen's own row " +
			"wins over the any-screen one. An unlisted box is still drawn — it keeps the guest's own input."));

		_rows = new Tree { Columns = 3, ColumnTitlesVisible = true, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 220) };
		_rows.SetColumnTitle(0, "Key");
		_rows.SetColumnTitle(1, "Shipped");
		_rows.SetColumnTitle(2, "This mod");
		_rows.ItemSelected += ShowRow;
		AddChild(_rows);

		var add = new HBoxContainer();
		_newKey = new LineEdit { PlaceholderText = "13 or 0@22", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		add.AddChild(_newKey);
		var addButton = new Button { Text = "+ Row for a box" };
		addButton.Pressed += AddKey;
		add.AddChild(addButton);
		AddChild(add);

		var body = new VBoxContainer();
		_detail = ModStudioUiStyles.Note(string.Empty);
		body.AddChild(_detail);
		var actions = new HFlowContainer();
		_override = new Button { Text = "✎ Override", TooltipText = "State a whole record for this box (starting from the shipped one)" };
		_override.Pressed += Override;
		actions.AddChild(_override);
		_release = new Button { Text = "⇢ Release to the guest", TooltipText = "A null record: the host does not answer this box; the guest's own input does" };
		_release.Pressed += () => Set(null);
		actions.AddChild(_release);
		_remove = new Button { Text = "↺ Drop the mod's row" };
		ModStudioUiStyles.ApplyRedButtonStyle(_remove);
		_remove.Pressed += Remove;
		actions.AddChild(_remove);
		body.AddChild(actions);

		var fields = new VBoxContainer();
		_kind = new OptionButton();
		_kind.AddItem("query");
		_kind.AddItem("message");
		_kind.ItemSelected += _ => WriteRecord();
		fields.AddChild(ModStudioUiStyles.Field("Kind", _kind, "query: confirm and cancel; message: one dismiss code"));
		_confirm = new SpinBox { MinValue = 0, MaxValue = 100000, Step = 1, AllowGreater = true };
		_confirm.ValueChanged += _ => WriteRecord();
		fields.AddChild(ModStudioUiStyles.Field("Confirm", _confirm, "The code A answers with (36 accept, 46 the shop and saving confirms, 39 fast travel's Y)"));
		_hasCancel = new CheckBox { Text = "B answers" };
		_hasCancel.Toggled += _ => WriteRecord();
		_cancel = new SpinBox { MinValue = 0, MaxValue = 100000, Step = 1, AllowGreater = true };
		_cancel.ValueChanged += _ => WriteRecord();
		var cancelRow = new HBoxContainer();
		cancelRow.AddChild(_hasCancel);
		cancelRow.AddChild(_cancel);
		fields.AddChild(ModStudioUiStyles.Field("Cancel", cancelRow, "The code B answers with (37 back, 47 the confirms' no); unticked, B does nothing"));
		_recordFields = fields;
		body.AddChild(fields);
		AddChild(ModStudioUiStyles.Card("Answer", new Color(0.95f, 0.65f, 0.55f), body));
	}

	private JsonObject ModModals()
	{
		if (_docs.Templates[Engine_UiModals.Section] is not JsonObject modals)
		{
			modals = new JsonObject();
			_docs.Templates[Engine_UiModals.Section] = modals;
		}

		return modals;
	}

	/// <summary>Lists the rows as the docs hold them.</summary>
	public void Reload()
	{
		if (!IsNodeReady())
		{
			return;
		}

		_shipped = UiStudioDocs.FromGodot(_docs.Api.ShippedModals());
		Fill(null);
	}

	private void Fill(string? select)
	{
		select ??= Selected();
		_rows.Clear();
		TreeItem root = _rows.CreateItem();
		IEnumerable<string> keys = _shipped.Select(p => p.Key).Concat(ModModals().Select(p => p.Key)).Distinct(StringComparer.Ordinal)
			.OrderBy(k => Engine_UiModals.TryParseKey(k, out uint id, out uint screen) ? id * 100000.0 + (screen == uint.MaxValue ? 0 : screen + 1) : double.MaxValue);
		foreach (string key in keys)
		{
			TreeItem item = _rows.CreateItem(root);
			item.SetText(0, key);
			item.SetText(1, Describe(_shipped[key]));
			bool stated = ModModals().TryGetPropertyValue(key, out JsonNode? mine);
			item.SetText(2, stated ? (mine is null ? "released to the guest" : Describe(mine)) : "—");
			if (stated)
			{
				item.SetCustomColor(2, new Color(0.55f, 0.85f, 1.0f));
			}

			item.SetMetadata(0, key);
			if (key == select)
			{
				item.Select(0);
			}
		}

		ShowRow();
	}

	private static string Describe(JsonNode? record) =>
		record is not JsonObject row ? string.Empty :
		$"{Engine_UiMenuEdit.Text(row, "kind")} {Code(row, "confirm")}" + (row.ContainsKey("cancel") ? $" / {Code(row, "cancel")}" : string.Empty);

	private static string Code(JsonObject row, string key) =>
		Engine_UiMenuEdit.NumberOf(row[key]) is { } n ? n.ToString("0", CultureInfo.InvariantCulture) : "?";

	private string? Selected() => _rows.GetSelected()?.GetMetadata(0).AsString();

	private void ShowRow()
	{
		string? key = Selected();
		bool stated = key is not null && ModModals().TryGetPropertyValue(key, out _);
		JsonObject? record = key is not null && stated ? ModModals()[key] as JsonObject : null;
		_override.Disabled = key is null || record is not null;
		_release.Disabled = key is null || (stated && record is null);
		_remove.Disabled = !stated;
		_recordFields.Visible = record is not null;
		_detail.Text = key is null ? "Select a box." :
			!stated ? $"Box {key}: the mod states nothing; " + (_shipped.ContainsKey(key) ? "the shipped answer stands." : "the guest answers it.") :
			record is null ? $"Box {key}: released — the guest's own input answers it." :
			$"Box {key}: the mod's record replaces the shipped one whole.";
		if (record is null)
		{
			return;
		}

		_filling = true;
		_kind.Select(Engine_UiMenuEdit.Text(record, "kind") == "message" ? 1 : 0);
		_confirm.Value = Engine_UiMenuEdit.Number(record, "confirm", 36);
		_hasCancel.ButtonPressed = record.ContainsKey("cancel");
		_cancel.Value = Engine_UiMenuEdit.Number(record, "cancel", 37);
		_cancel.Editable = _hasCancel.ButtonPressed && _kind.Selected == 0;
		_hasCancel.Disabled = _kind.Selected == 1;
		_filling = false;
	}

	private void Override()
	{
		if (Selected() is not { } key)
		{
			return;
		}

		JsonObject record = _shipped[key] is JsonObject shipped
			? (JsonObject)shipped.DeepClone()
			: new JsonObject { ["kind"] = "query", ["confirm"] = 36, ["cancel"] = 37 };
		Set(record);
	}

	private void Set(JsonObject? record)
	{
		if (Selected() is not { } key)
		{
			return;
		}

		ModModals()[key] = record;
		Fill(key);
		Edited?.Invoke();
	}

	private void Remove()
	{
		if (Selected() is not { } key)
		{
			return;
		}

		ModModals().Remove(key);
		if (ModModals().Count == 0)
		{
			_docs.Templates.Remove(Engine_UiModals.Section);
		}

		Fill(key);
		Edited?.Invoke();
	}

	private void WriteRecord()
	{
		if (_filling || Selected() is not { } key || ModModals()[key] is not JsonObject record)
		{
			return;
		}

		bool message = _kind.Selected == 1;
		record["kind"] = message ? "message" : "query";
		record["confirm"] = (long)_confirm.Value;
		if (!message && _hasCancel.ButtonPressed)
		{
			record["cancel"] = (long)_cancel.Value;
		}
		else
		{
			record.Remove("cancel");
		}

		Fill(key);
		Edited?.Invoke();
	}

	private void AddKey()
	{
		string key = _newKey.Text.Trim();
		if (!Engine_UiModals.TryParseKey(key, out _, out _))
		{
			_detail.Text = $"'{key}' is not a modal key: \"<modal id>\" or \"<modal id>@<screen id>\".";
			return;
		}

		if (!ModModals().ContainsKey(key))
		{
			ModModals()[key] = _shipped[key] is JsonObject shipped
				? shipped.DeepClone()
				: new JsonObject { ["kind"] = "query", ["confirm"] = 36, ["cancel"] = 37 };
			Edited?.Invoke();
		}

		_newKey.Text = string.Empty;
		Fill(key);
	}

	/// <summary>The modal conflicts with other enabled mods (the validator's issues are the Menus page's file too).</summary>
	public IEnumerable<(bool Error, string Text)> Issues(string templatesText, IReadOnlyList<Engine_UiTemplates.ModConflict> conflicts)
	{
		foreach (Refii.Mods.ModIssue issue in Refii.Mods.ModValidate.Validate(Engine_UiTemplates.ModTemplatesPath, templatesText)
			.Where(i => i.Pointer.Contains(Engine_UiModals.Section, StringComparison.Ordinal)))
		{
			yield return (issue.Error, issue.ToString());
		}

		foreach (Engine_UiTemplates.ModConflict conflict in conflicts.Where(c => !c.Piece.StartsWith("menu:", StringComparison.Ordinal) &&
			!c.Piece.StartsWith("screen:", StringComparison.Ordinal)))
		{
			if (UiStudioView.DescribeConflict(conflict, _docs.Mod) is { } text)
			{
				yield return (false, text);
			}
		}
	}
}
#endif
