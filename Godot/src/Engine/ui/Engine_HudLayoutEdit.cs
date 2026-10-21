using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Refii.Ui;

/// <summary>
/// THE HUD PLACEMENT EDIT, shared by every editor that moves HUD pieces — the Mod Studio's UI tab
/// (writing a mod's <c>ui/hud.json</c> <c>layout</c> block) and the player's layout tool (writing
/// <c>user://hud/layout.json</c>). Both hold one entry shape (<see cref="Engine_HudModLayout.Move"/>),
/// so the anchor presets, the re-anchoring that keeps a piece where it stands, a drag in the preview
/// and the "what changed" diff are written once here.
/// </summary>
/// <remarks>
/// <para>The placement rule is <see cref="Engine_HudLayout.Place"/>: a piece's point is
/// <c>anchor × extent + offset × scale</c>, in canvas units from the top-left, +Y down. The
/// functions take the extent and scale as numbers so they stay pure .NET and the tests hold
/// them without a Godot type in the host.</para>
/// </remarks>
public static class Engine_HudLayoutEdit
{
	/// <summary>One of the nine anchors the editors offer, in reading order.</summary>
	public readonly record struct Preset(string Label, float X, float Y);

	public static readonly Preset[] Presets =
	{
		new("Top Left", 0f, 0f), new("Top Centre", 0.5f, 0f), new("Top Right", 1f, 0f),
		new("Middle Left", 0f, 0.5f), new("Centre", 0.5f, 0.5f), new("Middle Right", 1f, 0.5f),
		new("Bottom Left", 0f, 1f), new("Bottom Centre", 0.5f, 1f), new("Bottom Right", 1f, 1f),
	};

	/// <summary>Two numbers an editor wrote and read back count as one below this.</summary>
	public const float Tolerance = 1e-3f;

	/// <summary>The preset nearest an anchor (Manhattan distance; the first of a tie).</summary>
	public static int NearestPreset(float x, float y)
	{
		int best = 0;
		float bestDistance = float.MaxValue;
		for (int i = 0; i < Presets.Length; i++)
		{
			float d = MathF.Abs(Presets[i].X - x) + MathF.Abs(Presets[i].Y - y);
			if (d < bestDistance)
			{
				bestDistance = d;
				best = i;
			}
		}

		return best;
	}

	/// <summary>The preset an anchor IS, or -1 for an anchor between them.</summary>
	public static int PresetOf(float x, float y)
	{
		int nearest = NearestPreset(x, y);
		return MathF.Abs(Presets[nearest].X - x) <= Tolerance && MathF.Abs(Presets[nearest].Y - y) <= Tolerance
			? nearest
			: -1;
	}

	/// <summary>
	/// The same place under another anchor: the offset absorbs the anchor's move across the
	/// <paramref name="width"/> × <paramref name="height"/> extent at <paramref name="scale"/>, so
	/// the piece stays put at that window while its anchor now follows another edge.
	/// </summary>
	public static Engine_HudModLayout.Move Rebase(
		Engine_HudModLayout.Move move, float anchorX, float anchorY, float width, float height, float scale)
	{
		float s = scale > 0f ? scale : 1f;
		return new Engine_HudModLayout.Move(
			anchorX,
			anchorY,
			move.OffsetX + (move.AnchorX - anchorX) * width / s,
			move.OffsetY + (move.AnchorY - anchorY) * height / s);
	}

	/// <summary>
	/// Re-anchors a piece to the preset nearest where it stands now — what a drop in the preview
	/// does, so a piece dragged to the right edge follows the right edge on a wider window.
	/// </summary>
	public static Engine_HudModLayout.Move Snap(Engine_HudModLayout.Move move, float width, float height, float scale)
	{
		if (width <= 0f || height <= 0f)
		{
			return move;
		}

		float s = scale > 0f ? scale : 1f;
		float fx = (move.AnchorX * width + move.OffsetX * s) / width;
		float fy = (move.AnchorY * height + move.OffsetY * s) / height;
		Preset preset = Presets[NearestPreset(Math.Clamp(fx, 0f, 1f), Math.Clamp(fy, 0f, 1f))];
		return Rebase(move, preset.X, preset.Y, width, height, s);
	}

	/// <summary>
	/// A drag of <paramref name="dx"/>, <paramref name="dy"/> canvas units (+Y DOWN, the layout's
	/// direction) at <paramref name="scale"/>: the offset moves by the drag over the scale, the
	/// anchor stays.
	/// </summary>
	public static Engine_HudModLayout.Move Drag(Engine_HudModLayout.Move move, float dx, float dy, float scale)
	{
		float s = scale > 0f ? scale : 1f;
		return move with { OffsetX = move.OffsetX + dx / s, OffsetY = move.OffsetY + dy / s };
	}

	public static bool Same(Engine_HudModLayout.Move a, Engine_HudModLayout.Move b) =>
		MathF.Abs(a.AnchorX - b.AnchorX) <= Tolerance && MathF.Abs(a.AnchorY - b.AnchorY) <= Tolerance &&
		MathF.Abs(a.OffsetX - b.OffsetX) <= Tolerance && MathF.Abs(a.OffsetY - b.OffsetY) <= Tolerance;

	/// <summary>What an edit changed: entries to write and entries to drop.</summary>
	public sealed record Changes(
		IReadOnlyDictionary<string, Engine_HudModLayout.Move> Set, IReadOnlyList<string> Removed)
	{
		public bool Empty => Set.Count == 0 && Removed.Count == 0;
	}

	/// <summary>
	/// The entries <paramref name="edited"/> states differently from <paramref name="loaded"/> (new
	/// or moved), and the ids <paramref name="loaded"/> stated that <paramref name="edited"/> no
	/// longer does. An entry nobody touched is not in either list — a save writes only what the
	/// editor changed.
	/// </summary>
	public static Changes Diff(
		IReadOnlyDictionary<string, Engine_HudModLayout.Move> loaded,
		IReadOnlyDictionary<string, Engine_HudModLayout.Move> edited)
	{
		var set = new Dictionary<string, Engine_HudModLayout.Move>(StringComparer.Ordinal);
		foreach ((string id, Engine_HudModLayout.Move move) in edited)
		{
			if (!loaded.TryGetValue(id, out Engine_HudModLayout.Move before) || !Same(before, move))
			{
				set[id] = move;
			}
		}

		var removed = new List<string>();
		foreach (string id in loaded.Keys)
		{
			if (!edited.ContainsKey(id))
			{
				removed.Add(id);
			}
		}

		removed.Sort(StringComparer.Ordinal);
		return new Changes(set, removed);
	}

	/// <summary>
	/// A layout file with <paramref name="changes"/> applied under <paramref name="section"/>
	/// (<see cref="Engine_HudModLayout.PlayerSection"/> for the player's file): every other key of the
	/// file and every other field of a changed entry is kept, so the file round-trips; an entry is
	/// written as <c>{ "anchor": [x, y], "offset": [x, y] }</c>. A missing or unreadable file starts
	/// empty.
	/// </summary>
	public static string Apply(string? existing, string section, Changes changes)
	{
		JsonObject root = ParseObject(existing);
		Apply(root, section, changes);
		return root.ToJsonString(Indented) + "\n";
	}

	/// <summary><see cref="Apply(string?, string, Changes)"/> on a document held in memory.</summary>
	public static void Apply(JsonObject root, string section, Changes changes)
	{
		if (root[section] is not JsonObject entries)
		{
			entries = new JsonObject();
			root[section] = entries;
		}

		foreach (string id in changes.Removed)
		{
			entries.Remove(id);
		}

		foreach ((string id, Engine_HudModLayout.Move move) in changes.Set)
		{
			if (entries[id] is not JsonObject entry)
			{
				entry = new JsonObject();
				entries[id] = entry;
			}

			entry["anchor"] = new JsonArray(move.AnchorX, move.AnchorY);
			entry["offset"] = new JsonArray(move.OffsetX, move.OffsetY);
		}
	}

	/// <summary>
	/// Whether a HUD snapshot's text holds the arrangement beneath the player's file (its
	/// <see cref="Engine_HudLayout.SnapshotLayerKey"/> is <see cref="Engine_HudLayout.SnapshotLayer"/>).
	/// </summary>
	public static bool SnapshotIsBeneath(string? text) =>
		ParseObject(text)[Engine_HudLayout.SnapshotLayerKey] is JsonValue layer &&
		layer.TryGetValue(out string? value) && value == Engine_HudLayout.SnapshotLayer;

	/// <summary>Indented output, with the resolver stated (see Engine_UiTemplateJson.WriteOptions).</summary>
	internal static readonly JsonSerializerOptions Indented = new()
	{
		WriteIndented = true,
		TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
	};

	/// <summary>A JSON object from text; empty for null, blank or anything that is not an object.</summary>
	public static JsonObject ParseObject(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return new JsonObject();
		}

		try
		{
			return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
		}
		catch (JsonException)
		{
			return new JsonObject();
		}
	}
}
