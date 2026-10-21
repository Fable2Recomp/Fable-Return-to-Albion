using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Refii.Ui;

/// <summary>
/// An edit of a mod's <c>ui/hud.json</c> (<see cref="Engine_HudModPieces"/>,
/// <see cref="Engine_HudModLayout"/>) as the per-entry calls the editor API makes — a piece or a
/// copy set or removed by id, a shipped piece hidden or shown, a scale set or dropped, a layout
/// entry set or dropped — so the Mod Studio saves only what its author changed, through the same
/// calls a script would make. Pure .NET, held by tests.
/// </summary>
public static class Engine_HudModEdit
{
	public enum OpKind
	{
		RemoveEntry,
		SetPiece,
		SetClone,
		Hide,
		Show,
		SetScale,
		RemoveScale,
		SetLayout,
		RemoveLayout,
	}

	/// <summary>One call: what it does, to which id, with which record or number (null for a removal).</summary>
	public sealed record Op(OpKind Kind, string Id, JsonNode? Value);

	public const string PiecesKey = "pieces";
	public const string ClonesKey = "clones";
	public const string HideKey = "hide";
	public const string ScaleKey = "scale";

	/// <summary>
	/// The calls that turn <paramref name="before"/> into <paramref name="after"/>: removals first,
	/// then every record that is new or differs. A record nobody touched makes no call.
	/// </summary>
	public static List<Op> Diff(JsonObject before, JsonObject after)
	{
		var ops = new List<Op>();

		Dictionary<string, JsonObject> piecesBefore = ById(before, PiecesKey);
		Dictionary<string, JsonObject> piecesAfter = ById(after, PiecesKey);
		Dictionary<string, JsonObject> clonesBefore = ById(before, ClonesKey);
		Dictionary<string, JsonObject> clonesAfter = ById(after, ClonesKey);

		foreach (string id in piecesBefore.Keys.Concat(clonesBefore.Keys).Distinct(StringComparer.Ordinal))
		{
			if (!piecesAfter.ContainsKey(id) && !clonesAfter.ContainsKey(id))
			{
				ops.Add(new Op(OpKind.RemoveEntry, id, null));
			}
		}

		var hiddenBefore = Strings(before, HideKey);
		var hiddenAfter = Strings(after, HideKey);
		foreach (string id in hiddenBefore.Where(id => !hiddenAfter.Contains(id)))
		{
			ops.Add(new Op(OpKind.Show, id, null));
		}

		JsonObject scalesBefore = before[ScaleKey] as JsonObject ?? new JsonObject();
		JsonObject scalesAfter = after[ScaleKey] as JsonObject ?? new JsonObject();
		foreach ((string id, _) in scalesBefore)
		{
			if (!scalesAfter.ContainsKey(id))
			{
				ops.Add(new Op(OpKind.RemoveScale, id, null));
			}
		}

		JsonObject layoutBefore = before[Engine_HudModLayout.Section] as JsonObject ?? new JsonObject();
		JsonObject layoutAfter = after[Engine_HudModLayout.Section] as JsonObject ?? new JsonObject();
		foreach ((string id, _) in layoutBefore)
		{
			if (!layoutAfter.ContainsKey(id))
			{
				ops.Add(new Op(OpKind.RemoveLayout, id, null));
			}
		}

		foreach ((string id, JsonObject record) in piecesAfter)
		{
			if (!piecesBefore.TryGetValue(id, out JsonObject? old) || !Engine_UiMenuEdit.Same(old, record))
			{
				ops.Add(new Op(OpKind.SetPiece, id, record.DeepClone()));
			}
		}

		foreach ((string id, JsonObject record) in clonesAfter)
		{
			if (!clonesBefore.TryGetValue(id, out JsonObject? old) || !Engine_UiMenuEdit.Same(old, record))
			{
				ops.Add(new Op(OpKind.SetClone, id, record.DeepClone()));
			}
		}

		foreach (string id in hiddenAfter.Where(id => !hiddenBefore.Contains(id)))
		{
			ops.Add(new Op(OpKind.Hide, id, null));
		}

		foreach ((string id, JsonNode? factor) in scalesAfter)
		{
			if (!scalesBefore.TryGetPropertyValue(id, out JsonNode? old) || !Engine_UiMenuEdit.Same(old, factor))
			{
				ops.Add(new Op(OpKind.SetScale, id, factor?.DeepClone()));
			}
		}

		foreach ((string id, JsonNode? entry) in layoutAfter)
		{
			if (!layoutBefore.TryGetPropertyValue(id, out JsonNode? old) || !Engine_UiMenuEdit.Same(old, entry))
			{
				ops.Add(new Op(OpKind.SetLayout, id, entry?.DeepClone()));
			}
		}

		return ops;
	}

	/// <summary>The fields of a piece that name a value source.</summary>
	public static readonly string[] SourceFields = { "value", "max", "widthFrom", "index", "text" };

	/// <summary>
	/// Every <c>mod:&lt;key&gt;</c> the file's pieces and copies read, sorted — what an editor offers a
	/// stand-in for. A copy's <c>values</c> are <c>source</c> or <c>source/cap</c>; both sides count.
	/// </summary>
	public static List<string> ModKeys(JsonObject hud)
	{
		var keys = new SortedSet<string>(StringComparer.Ordinal);
		foreach (JsonObject piece in ById(hud, PiecesKey).Values)
		{
			foreach (string field in SourceFields)
			{
				Take(Engine_UiMenuEdit.Text(piece, field));
			}
		}

		foreach (JsonObject clone in ById(hud, ClonesKey).Values)
		{
			if (clone["values"] is JsonObject values)
			{
				foreach ((_, JsonNode? source) in values)
				{
					if (source is JsonValue value && value.TryGetValue(out string? text))
					{
						foreach (string side in text.Split('/'))
						{
							Take(side);
						}
					}
				}
			}
		}

		return keys.ToList();

		void Take(string source)
		{
			string trimmed = source.Trim();
			if (trimmed.StartsWith(Engine_HudModText.ModPrefix, StringComparison.Ordinal) &&
				trimmed.Length > Engine_HudModText.ModPrefix.Length)
			{
				keys.Add(trimmed[Engine_HudModText.ModPrefix.Length..]);
			}
		}
	}

	/// <summary>
	/// Every NAMED value source the file's pieces and copies read, whole and sorted —
	/// <c>mod:…</c>, <c>game:…</c>, <c>player:…</c> (<see cref="Engine_UiValueSources"/>) — what an
	/// editor offers a stand-in for. <c>hero:</c> is the preview's meter stand-in.
	/// </summary>
	public static List<string> SourceKeys(JsonObject hud)
	{
		var keys = new SortedSet<string>(StringComparer.Ordinal);
		foreach (JsonObject piece in ById(hud, PiecesKey).Values)
		{
			foreach (string field in SourceFields)
			{
				Take(Engine_UiMenuEdit.Text(piece, field));
			}
		}

		foreach (JsonObject clone in ById(hud, ClonesKey).Values)
		{
			if (clone["values"] is JsonObject values)
			{
				foreach ((_, JsonNode? source) in values)
				{
					if (source is JsonValue value && value.TryGetValue(out string? text))
					{
						foreach (string side in text.Split('/'))
						{
							Take(side);
						}
					}
				}
			}
		}

		return keys.ToList();

		void Take(string text)
		{
			Engine_UiValueSources.Source source = Engine_UiValueSources.Parse(text);
			if (source.Kind is Engine_UiValueSources.Kind.Mod or Engine_UiValueSources.Kind.Game or
				Engine_UiValueSources.Kind.Player && source.Key.Length != 0)
			{
				keys.Add(text.Trim());
			}
		}
	}

	/// <summary>The records of a list keyed by <c>id</c>, in file order; records without an id are skipped.</summary>
	public static Dictionary<string, JsonObject> ById(JsonObject hud, string listKey)
	{
		var records = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
		if (hud[listKey] is JsonArray list)
		{
			foreach (JsonNode? node in list)
			{
				if (node is JsonObject record && Engine_UiMenuEdit.Text(record, "id") is { Length: > 0 } id)
				{
					records[id] = record;
				}
			}
		}

		return records;
	}

	private static List<string> Strings(JsonObject hud, string key)
	{
		var list = new List<string>();
		if (hud[key] is JsonArray array)
		{
			foreach (JsonNode? node in array)
			{
				if (node is JsonValue value && value.TryGetValue(out string? text) && !list.Contains(text))
				{
					list.Add(text);
				}
			}
		}

		return list;
	}
}
