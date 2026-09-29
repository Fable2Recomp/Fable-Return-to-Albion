using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Refii.Equipment;

[Tool]
[GlobalClass]
public partial class Engine_EquipmentInstance : MeshInstance3D
{
	public const string NameMeta = "equipment_name";
	public const string KindMeta = "equipment_kind";
	public const string SlotMeta = "equipment_slot";
	public const string SlotLabelMeta = "equipment_slot_label";
	public const string HashMeta = "equipment_hash";
	public const string ModelMeta = "equipment_model";
	public const string DetailMeta = "equipment_detail";

	private bool _meshTried;

	public override void _Ready() => EnsureMesh();

	public string ModelPath => Text(ModelMeta);

	public const string DefaultSlotLabel = "Slot";

	private string SlotLabel =>
		Text(SlotLabelMeta).Length != 0 ? Text(SlotLabelMeta) : DefaultSlotLabel;

	public void EnsureMesh(bool forceReload = false)
	{
		if ((_meshTried && !forceReload) || (Mesh is not null && !forceReload))
		{
			return;
		}

		_meshTried = true;

		string path = ModelPath;
		if (path.Length == 0)
		{
			return;
		}

		ArrayMesh? mesh = Refii.Level.Engine_PropStreamer.Resolve(path);
		if (mesh is null || mesh.GetSurfaceCount() == 0)
		{
			GD.PushWarning($"[equipment] {Name}: no geometry from {path}");
			return;
		}

		Mesh = mesh;

		Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
			this, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);
	}

	public override Array<Dictionary> _GetPropertyList()
	{
		var list = new Array<Dictionary>();

		Group(list, Text(KindMeta).Length != 0 ? Text(KindMeta) : "Equipment");
		Row(list, "Name", Text(NameMeta));

		if (Text(SlotMeta).Length != 0)
		{
			Row(list, SlotLabel);
		}

		Row(list, "Record Hash");

		if (ModelPath.Length != 0)
		{
			Row(list, "Model", ModelPath);
		}

		if (!HasMeta(DetailMeta))
		{
			return list;
		}

		Dictionary detail = GetMeta(DetailMeta).AsGodotDictionary();
		if (detail.Count == 0)
		{
			return list;
		}

		Group(list, "GDB Record");

		foreach (Variant key in detail.Keys)
		{
			Row(list, key.AsString(), detail[key].AsString());
		}

		return list;
	}

	public override Variant _Get(StringName property)
	{
		string name = property.ToString();

		if (name == SlotLabel)
		{
			return Text(SlotMeta);
		}

		switch (name)
		{
			case "Name":
				return Text(NameMeta);
			case "Record Hash":
				return Text(HashMeta);
			case "Model":
				return ModelPath;
		}

		if (!HasMeta(DetailMeta))
		{
			return default;
		}

		Dictionary detail = GetMeta(DetailMeta).AsGodotDictionary();
		return detail.TryGetValue(name, out Variant value) ? value : default;
	}

	public override bool _Set(StringName property, Variant value)
	{
		return false;
	}

	public void OpenModEditor()
	{
#if TOOLS
		uint hash = 0;
		string hashStr = Text(HashMeta);
		if (hashStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			uint.TryParse(hashStr[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out hash);
		}

		if (hash != 0)
		{
			Refii.Gdb.GdbMulti? multi = Refii.Gdb.Engine_GameGdb.Database;
			if (multi is not null)
			{
				ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, hash);
				if (detail is not null)
				{
					Refii.Assets.BnkMountPlugin.Instance?.OpenModItemEditor(detail);
				}
			}
		}
#endif
	}

	/// <summary>
	/// Reloads item metadata and GDB record attributes from the active database and text overlays,
	/// updating the inspector and node attributes without needing to recreate the scene node.
	/// </summary>
	public void RefreshDetails()
	{
		uint hash = 0;
		string hashStr = Text(HashMeta);
		if (hashStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			uint.TryParse(hashStr[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out hash);
		}

		if (hash == 0)
		{
			return;
		}

		Refii.Gdb.GdbMulti? multi = Refii.Gdb.Engine_GameGdb.Database;
		if (multi is null)
		{
			return;
		}

		ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, hash);
		if (detail is null)
		{
			return;
		}

		var rows = Engine_EquipmentScene.BuildRowsForDetail(detail);
		SetMeta(DetailMeta, DetailRows(rows));

		string safeName = Engine_ItemNames.DisplayNameOf(detail);
		if (!string.IsNullOrEmpty(safeName))
		{
			SetMeta(NameMeta, safeName);
		}

		// Re-evaluate geometry if a mod overrode mesh or textures
		EnsureMesh(forceReload: true);

		NotifyPropertyListChanged();

#if TOOLS
		if (Engine.IsEditorHint())
		{
			var selection = EditorInterface.Singleton?.GetSelection()?.GetSelectedNodes();
			if (selection is not null && selection.Contains(this))
			{
				EditorInterface.Singleton?.InspectObject(this, string.Empty, true);
			}
		}
#endif
	}

	public static Dictionary DetailRows(IEnumerable<KeyValuePair<string, string>> rows)
	{
		var stored = new Dictionary();
		foreach (KeyValuePair<string, string> row in rows)
		{
			if (row.Key.Length != 0 && row.Value.Length != 0)
			{
				stored[row.Key] = row.Value;
			}
		}

		return stored;
	}

	private string Text(string meta) =>
		HasMeta(meta) ? GetMeta(meta).AsString() : string.Empty;

	private static void Group(Array<Dictionary> list, string name) =>
		list.Add(new Dictionary
		{
			{ "name", name },
			{ "type", (int)Variant.Type.Nil },
			{ "usage", (int)PropertyUsageFlags.Group },
			{ "hint_string", string.Empty },
		});

	public const int MultilineThreshold = 48;

	public static bool NeedsMultiline(string name, string value) =>
		name.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
		value.Length > MultilineThreshold ||
		value.Contains('\n') ||
		value.Contains('\r');

	private static void Row(Array<Dictionary> list, string name) =>
		list.Add(new Dictionary
		{
			{ "name", name },
			{ "type", (int)Variant.Type.String },
			{ "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
		});

	private static void Row(Array<Dictionary> list, string name, string value)
	{
		if (!NeedsMultiline(name, value))
		{
			Row(list, name);
			return;
		}

		list.Add(new Dictionary
		{
			{ "name", name },
			{ "type", (int)Variant.Type.String },
			{ "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
			{ "hint", (int)PropertyHint.MultilineText },
			{ "hint_string", string.Empty },
		});
	}
}
