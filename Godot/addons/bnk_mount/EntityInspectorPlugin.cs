#if TOOLS
using Godot;
using Refii.EditorTools;

namespace Refii.Assets;

public partial class EntityInspectorPlugin : EditorInspectorPlugin
{
    public override bool _CanHandle(GodotObject godotObject) =>
        godotObject.HasMeta("container_type") ||
        godotObject.HasMeta("readable_text") ||
        godotObject.HasMeta("property") ||
        godotObject.HasMeta("equipment_hash");

    public override void _ParseBegin(GodotObject godotObject)
    {
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);

        if (godotObject.HasMeta("equipment_hash"))
        {
            AddEquipmentSection(root, godotObject);
        }

        if (godotObject.HasMeta("container_type"))
        {
            AddContainerSection(root, godotObject);
        }

        if (godotObject.HasMeta("readable_text"))
        {
            AddReadableSection(root, godotObject);
        }

        if (godotObject.HasMeta("property"))
        {
            AddPropertySection(root, godotObject);
        }

        if (root.GetChildCount() != 0)
        {
            var panel = new PanelContainer();
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 8);
            margin.AddThemeConstantOverride("margin_right", 8);
            margin.AddThemeConstantOverride("margin_top", 8);
            margin.AddThemeConstantOverride("margin_bottom", 8);
            margin.AddChild(root);
            panel.AddChild(margin);
            AddCustomControl(panel);
        }
        else
        {
            root.QueueFree();
        }
    }

    private void AddEquipmentSection(VBoxContainer root, GodotObject godotObject)
    {
        string activeMod = EditorTools.EditorModExporter.ActiveModName;
        root.AddChild(Header($"Item Modding [{activeMod}]"));

        string hashStr = godotObject.GetMeta("equipment_hash").AsString();
        uint hash = 0;
        if (hashStr.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
        {
            uint.TryParse(hashStr[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out hash);
        }

        bool hasOverrides = EditorModValueService.HasModOverridesForHash(hash, activeMod);

        var statusRow = new HBoxContainer();
        statusRow.AddThemeConstantOverride("separation", 8);

        var statusDot = new Label
        {
            Text = hasOverrides ? "●" : "○",
            Modulate = hasOverrides ? new Color(1.0f, 0.8f, 0.25f) : new Color(0.4f, 0.85f, 0.5f),
        };
        statusRow.AddChild(statusDot);

        var statusLabel = new Label
        {
            Text = hasOverrides
                ? $"Modified in mod [{activeMod}]"
                : "Using vanilla game defaults",
            Modulate = hasOverrides ? new Color(1.0f, 0.85f, 0.5f) : new Color(0.75f, 0.75f, 0.75f),
        };
        statusRow.AddChild(statusLabel);
        root.AddChild(statusRow);

        var btn = new Button
        {
            Text = "✏️ Open Modding Dialogue...",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        btn.Pressed += () =>
        {
            if (godotObject is Equipment.Engine_EquipmentInstance equip && GodotObject.IsInstanceValid(equip))
            {
                equip.OpenModEditor();
            }
        };
        root.AddChild(btn);
    }

    private void AddContainerSection(VBoxContainer root, GodotObject godotObject)
    {
        string type = godotObject.GetMeta("container_type").AsString();
        string title = type;
        if (godotObject.HasMeta("silver_keys_needed"))
        {
            int keys = godotObject.GetMeta("silver_keys_needed").AsInt32();
            title += $"  ({keys} silver key{(keys == 1 ? "" : "s")})";
        }

        root.AddChild(Header(title));

        var flags = new HBoxContainer();
        flags.AddThemeConstantOverride("separation", 12);
        AddFlag(flags, godotObject, "stealable", "Stealable");
        AddFlag(flags, godotObject, "respawns_once", "Respawns once");
        AddFlag(flags, godotObject, "respawns_repeatedly", "Respawns repeatedly");
        AddFlag(flags, godotObject, "dog_can_lead_here", "Dog leads here");
        if (godotObject.HasMeta("respawn_chance"))
        {
            flags.AddChild(new Label
            {
                Text = $"Chance {godotObject.GetMeta("respawn_chance").AsSingle():0.###}",
            });
        }

        if (flags.GetChildCount() != 0)
        {
            root.AddChild(flags);
        }
        else
        {
            flags.QueueFree();
        }

        AddItemList(root, godotObject, "contents", "Contents");
        AddItemList(root, godotObject, "potential_contents", "Potential contents");
    }

    private void AddReadableSection(VBoxContainer root, GodotObject godotObject)
    {
        string[] texts = godotObject.GetMeta("readable_text").AsStringArray();
        if (texts.Length == 0)
        {
            return;
        }

        root.AddChild(Header(texts.Length == 1 ? "Readable" : $"Readable ({texts.Length})"));
        foreach (string text in texts)
        {
            var label = new RichTextLabel
            {
                Text = text,
                FitContent = true,
                SelectionEnabled = true,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(0, 0),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            root.AddChild(label);
        }
    }

    private void AddPropertySection(VBoxContainer root, GodotObject godotObject)
    {
        var details = godotObject.GetMeta("property").AsGodotDictionary();
        if (details.Count == 0)
        {
            return;
        }

        root.AddChild(Header("Property"));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        foreach (Variant key in details.Keys)
        {
            grid.AddChild(new Label
            {
                Text = key.AsString(),
                Modulate = new Color(1f, 1f, 1f, 0.65f),
            });
            grid.AddChild(new Label
            {
                Text = details[key].AsString(),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            });
        }

        root.AddChild(grid);
    }

    private static void AddFlag(
        HBoxContainer flags, GodotObject godotObject, string meta, string label)
    {
        if (!godotObject.HasMeta(meta))
        {
            return;
        }

        flags.AddChild(new CheckBox
        {
            Text = label,
            ButtonPressed = godotObject.GetMeta(meta).AsBool(),
            Disabled = true,
        });
    }

    private static void AddItemList(
        VBoxContainer root, GodotObject godotObject, string meta, string title)
    {
        if (!godotObject.HasMeta(meta))
        {
            return;
        }

        string[] items = godotObject.GetMeta(meta).AsStringArray();
        if (items.Length == 0)
        {
            return;
        }

        root.AddChild(new Label
        {
            Text = $"{title} ({items.Length})",
            Modulate = new Color(1f, 1f, 1f, 0.65f),
        });

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        foreach (string item in items)
        {
            list.AddChild(new Label
            {
                Text = "• " + item,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            });
        }

        root.AddChild(list);
    }

    private static Label Header(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 15);
        return label;
    }
}
#endif
