using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Refii.Titles.Fable2;

/// <summary>One mod enabled in Archon's Toolbox when the save was written.</summary>
public sealed class ArchonMod
{
	public string Name = string.Empty;
	public string Version = string.Empty;

	public string Label => (Name + " " + Version).Trim();
}

/// <summary>
/// Archon's Toolbox as applied to the install when the save was written: the sidecar's `archon`
/// layer, `{"bank":…,"patched":…,"runner":…,"mods":[{"name":…,"version":…}…]}`.
/// </summary>
public sealed class ArchonLayer
{
	/// <summary>The sidecar key the layer is kept under.</summary>
	public const string Key = "archon";

	/// <summary>The gamescripts_r.bnk SHA-256 (lower-case hex) the save was written under.</summary>
	public string Bank = string.Empty;
	/// <summary>The bank carried the toolbox's own patch.</summary>
	public bool Patched;
	/// <summary>The runner's version, as runner.lua states it.</summary>
	public string Runner = string.Empty;
	/// <summary>The mods installed and enabled in installedmods.lua, in file order.</summary>
	public List<ArchonMod> Mods = new();

	public ArchonMod? Find(string name) => Mods.Find(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

	public string ToJson()
	{
		using var buffer = new MemoryStream();
		using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			w.WriteStartObject();
			w.WriteString("bank", Bank);
			w.WriteBoolean("patched", Patched);
			w.WriteString("runner", Runner);
			w.WriteStartArray("mods");
			foreach (ArchonMod mod in Mods)
			{
				w.WriteStartObject();
				w.WriteString("name", mod.Name);
				w.WriteString("version", mod.Version);
				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
	}

	/// <summary>The layer from its JSON, or null when it is not an object.</summary>
	public static ArchonLayer? FromJson(string json)
	{
		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement archon = doc.RootElement;
		if (archon.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var layer = new ArchonLayer { Bank = Str(archon, "bank"), Patched = Bool(archon, "patched"), Runner = Str(archon, "runner") };
		if (archon.TryGetProperty("mods", out JsonElement mods) && mods.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement m in mods.EnumerateArray())
			{
				var mod = new ArchonMod { Name = Str(m, "name"), Version = Str(m, "version") };
				if (mod.Name.Length != 0) layer.Mods.Add(mod);
			}
		}

		return layer;
	}

	private static string Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

	private static bool Bool(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True;
}
