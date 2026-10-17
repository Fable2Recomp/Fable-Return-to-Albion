#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.EditorTools;
using Refii.Models;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// Interactive, multi-tab Modding Guide built directly into Mod Studio.
/// Walks creators through .f2mod workspaces, GDB records &amp; components,
/// 3D models &amp; textures, .mdl.gmd attachment sockets, the Refii Blender plugin,
/// and level/Lua modding workflows.
/// </summary>
public partial class ModdingGuideView : MarginContainer
{
    private readonly List<Button> _subTabButtons = new();
    private readonly List<Control> _subTabPages = new();
    private Label? _statusToastLabel;

    public ModdingGuideView()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        AddThemeConstantOverride("margin_left", 6);
        AddThemeConstantOverride("margin_top", 6);
        AddThemeConstantOverride("margin_right", 6);
        AddThemeConstantOverride("margin_bottom", 6);

        BuildLayout();
    }

    private void BuildLayout()
    {
        var rootVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        rootVBox.AddThemeConstantOverride("separation", 8);
        AddChild(rootVBox);

        // Top Banner + Sub-Tab Navigation Bar
        var topPanel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var topStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.15f),
            BorderColor = new Color(0.22f, 0.36f, 0.54f),
            BorderWidthLeft = 2,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        topPanel.AddThemeStyleboxOverride("panel", topStyle);

        var topVBox = new VBoxContainer();
        topVBox.AddThemeConstantOverride("separation", 6);

        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 10);

        var titleLbl = new Label
        {
            Text = "📖 Fable II Mod Studio — Creator Guide & Reference",
            Modulate = new Color(0.55f, 0.84f, 1.0f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 16);
        titleRow.AddChild(titleLbl);

        _statusToastLabel = new Label
        {
            Text = "",
            Modulate = new Color(0.45f, 0.95f, 0.65f)
        };
        _statusToastLabel.AddThemeFontSizeOverride("font_size", 13);
        titleRow.AddChild(_statusToastLabel);

        var openToolsBtn = new Button
        {
            Text = "🧩 Open res://modding_tools/ (Blender Plugin)",
            TooltipText = "Open the folder containing refii_blender_tools.py in File Explorer"
        };
        openToolsBtn.AddThemeFontSizeOverride("font_size", 13);
        openToolsBtn.Pressed += OnOpenModdingToolsFolder;
        titleRow.AddChild(openToolsBtn);

        topVBox.AddChild(titleRow);

        var subNavRow = new HBoxContainer();
        subNavRow.AddThemeConstantOverride("separation", 6);

        AddSubTabButton(subNavRow, "🚀 1. Overview & Workspaces", 0);
        AddSubTabButton(subNavRow, "🗂️ 2. Modding GDB Records & Components", 1);
        AddSubTabButton(subNavRow, "🎨 3. Models, Textures & .gmd Sockets", 2);
        AddSubTabButton(subNavRow, "🗺️ 4. Levels & 3D Placements", 3);
        AddSubTabButton(subNavRow, "📜 5. Lua Scripts & Quests", 4);
        AddSubTabButton(subNavRow, "🧩 6. Script API & Hard Mods", 5);

        topVBox.AddChild(subNavRow);
        topPanel.AddChild(topVBox);
        rootVBox.AddChild(topPanel);

        // Scrollable Page Host
        var pageScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };

        var pageHost = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        _subTabPages.Add(BuildOverviewPage());
        _subTabPages.Add(BuildGdbRecordsPage());
        _subTabPages.Add(BuildModelsTexturesGmdPage());
        _subTabPages.Add(BuildLevelsPage());
        _subTabPages.Add(BuildLuaPage());
        _subTabPages.Add(BuildScriptApiPage());

        foreach (Control page in _subTabPages)
        {
            pageHost.AddChild(page);
        }

        pageScroll.AddChild(pageHost);
        rootVBox.AddChild(pageScroll);

        SwitchSubTab(0);
    }

    private void AddSubTabButton(HBoxContainer bar, string label, int index)
    {
        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            CustomMinimumSize = new Vector2(0, 28)
        };
        btn.AddThemeFontSizeOverride("font_size", 13);
        btn.Pressed += () => SwitchSubTab(index);
        _subTabButtons.Add(btn);
        bar.AddChild(btn);
    }

    public void SwitchSubTab(int index)
    {
        for (int i = 0; i < _subTabButtons.Count; i++)
        {
            _subTabButtons[i].ButtonPressed = (i == index);
        }

        for (int i = 0; i < _subTabPages.Count; i++)
        {
            _subTabPages[i].Visible = (i == index);
        }
    }

    private void ShowToast(string text)
    {
        if (_statusToastLabel is not null)
        {
            _statusToastLabel.Text = text;
        }
    }

    private void OnOpenModdingToolsFolder()
    {
        string toolsDir = ProjectSettings.GlobalizePath("res://modding_tools");
        Directory.CreateDirectory(toolsDir);
        string pluginFile = Path.Combine(toolsDir, "refii_blender_tools.py");
        EditorModExporter.RevealInExplorer(File.Exists(pluginFile) ? pluginFile : toolsDir);
        ShowToast("Opened res://modding_tools/ in File Explorer.");
    }

    // =========================================================================
    // SUB-TAB 0: OVERVIEW & WORKSPACES
    // =========================================================================
    private Control BuildOverviewPage()
    {
        var page = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        page.AddThemeConstantOverride("separation", 10);

        page.AddChild(CreateSectionCard(
            "🚀 Welcome to the Fable II Mod Studio",
            new Color(0.32f, 0.72f, 1.0f),
            "Refii Mod Studio lets you inspect, override, and create new Fable II items, weapons, outfits, textures, 3D meshes, and game database records without touching raw hex files.\n\n" +
            "Every mod is stored in two places while you work in the editor:\n" +
            "  1. Creator Workspace (res://creator/<YourMod>/): Your editable source folder containing standard .glb 3D models and uncompressed .png textures for Blender, Substance Painter, or Photoshop.\n" +
            "  2. Compiled Mod Package (assets/mods/<YourMod>.f2mod): The self-contained archive that the game mounts at runtime. Whenever you drag-and-drop a .glb or .png onto a field or click 💾 Save Mod Overrides, Mod Studio automatically compiles your assets into Xbox/Refii formats (.mdl, .mdl.gmd, .tex, mod.gdb.tu, mod.babel.csv) inside your .f2mod package!"));

        page.AddChild(CreateSectionCard(
            "⚡ The 5-Minute Modding Workflow",
            new Color(0.42f, 0.90f, 0.62f),
            "Step 1 — Create or Select a Mod:\n" +
            "  • Use the 'Active Mod' dropdown in the top bar (or click '+ New Mod') to create your workspace.\n\n" +
            "Step 2 — Pick a Record in '🗂️ GDB Hierarchy & Components':\n" +
            "  • Switch to the GDB Hierarchy tab, filter by domain (e.g. Weapons, Clothing, Furniture & Props), or search by name (e.g. 'Toy Pistol' or 'Master Longsword').\n" +
            "  • Want to make a brand new standalone weapon instead of replacing a vanilla one? Select a template weapon and click '[ + Create Child Node ]'!\n\n" +
            "Step 3 — Export Assets to Your Creator Folder (📤):\n" +
            "  • In the right-hand Component Inspector, find the GraphicComponent or Model property.\n" +
            "  • Click the '📤' Export button next to the Model field. Mod Studio exports the clean 3D mesh (.glb) and its attachment sockets ('GmdDummies') into res://creator/<YourMod>/<ItemName>/ without cluttering your workspace with texture dumps.\n" +
            "  • Want to export individual textures as .png? Expand '▾ 🎨 Surface Material Textures' and click '📤' on any texture slot!\n\n" +
            "Step 4 — Edit in Blender / DCC & Drag-and-Drop Back (📥):\n" +
            "  • Open the exported .glb in Blender, Substance 3D, or Maya. You can author full PBR materials (Base Color, Metallic-Roughness, Ambient Occlusion, and Emission) right on your shader nodes!\n" +
            "  • Drag the saved .glb directly onto the '📥 DROP .GLB' drop zone in the GDB Component Inspector. Mod Studio automatically extracts all embedded PBR textures, channel-packs ORM maps, compiles .mdl geometry, and updates your .f2mod package!\n\n" +
            "Step 5 — Preview in 3D & Test In-Game:\n" +
            "  • Click '[ 🔍 Inspect in 3D ]' in the record header to see your updated mesh, textures, and 3D attachment socket gizmos right inside the editor, or launch the game to test it live!"));

        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);

        var goGdbBtn = new Button
        {
            Text = "🗂️ Open GDB Hierarchy & Components Tab"
        };
        goGdbBtn.Pressed += () => ModdingHubPanel.Instance?.SwitchTab(2);
        actionRow.AddChild(goGdbBtn);

        var goModelsGuideBtn = new Button
        {
            Text = "🎨 Read the 3D Models, Textures & .gmd Sockets Guide →"
        };
        goModelsGuideBtn.Pressed += () => SwitchSubTab(2);
        actionRow.AddChild(goModelsGuideBtn);

        page.AddChild(actionRow);
        return page;
    }

    // =========================================================================
    // SUB-TAB 1: MODDING GDB RECORDS & COMPONENTS
    // =========================================================================
    private Control BuildGdbRecordsPage()
    {
        var page = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        page.AddThemeConstantOverride("separation", 10);

        page.AddChild(CreateSectionCard(
            "🗂️ What Are GDB Records & How Does Fable II's Database Work?",
            new Color(0.38f, 0.78f, 1.0f),
            "Fable II's Game Database (game.gdb) is an object-oriented hierarchy of entity definitions rooted at World Entity Root (0x46F79080).\n\n" +
            "• Every item, weapon, piece of clothing, creature, potion, building, and visual effect is a Node Record in this tree.\n" +
            "• Child nodes automatically inherit all Components and Properties from their parent chain. For example:\n" +
            "  World Entity Root → Object → ObjectInventory → Weapon → RangedWeapon → Pistol → Pistol_Clockwork → Toy Pistol.\n" +
            "• Instead of duplicating hundreds of fields on every item, a specific weapon only attaches or overrides the components that make it unique (such as its GraphicComponent model, InventoryItemComponent price/name, or WeaponComponent damage)!"));

        page.AddChild(CreateSectionCard(
            "🧩 Direct Components vs. Inherited Components",
            new Color(0.95f, 0.78f, 0.35f),
            "When you select any record in the '🗂️ GDB Hierarchy & Components' tab, the right pane shows two sections:\n\n" +
            "1. Direct Components on This Node:\n" +
            "   • Components attached directly to this specific record. Editing properties here ONLY affects this node (and any children below it that don't override them).\n" +
            "   • Modified properties are highlighted in gold so you can immediately see what your mod changes.\n\n" +
            "2. Inherited Components (Walked Up Parent Chain):\n" +
            "   • Components that this item inherits from a parent template (e.g. RangedWeapon or ObjectInventory).\n" +
            "   • If you want to customize an inherited component on your item without changing other items that share the same parent, click the '[ Override ]' button on that inherited component card! Mod Studio automatically creates a child component record linked to your node.\n" +
            "   • If you want to completely remove an inherited behavior from your node, click '[ ✕ Strip ]' (which writes a native RemoveComponent entry)."));

        page.AddChild(CreateSectionCard(
            "🌿 Creating Brand New Standalone Items ('+ Create Child Node')",
            new Color(0.45f, 0.92f, 0.65f),
            "You don't have to overwrite vanilla items! To create a brand new item:\n" +
            "  1. Select an existing item or template node that behaves like what you want (for example, 'Master Flintlock Pistol' or 'Highwayman Coat').\n" +
            "  2. Click '[ + Create Child Node ]' in the top-right of the inspector.\n" +
            "  3. Enter a Display Name (e.g. 'Dragonbreath Pistol'), an Internal GDB Record Name (auto-filled as 'ObjectInventory_DragonbreathPistol'), and an optional Description.\n" +
            "  4. Mod Studio computes the deterministic FNV-1a 32-bit hash for your new record, registers its localized name & description in your mod's Babel text table (mod.babel.csv), and branches it directly under the parent node.\n" +
            "  5. Override its GraphicComponent or WeaponComponent, drag-and-drop your custom .glb model, and click '💾 Save Mod Overrides'!"));

        var jumpRow = new HBoxContainer();
        jumpRow.AddThemeConstantOverride("separation", 6);

        var jumpLbl = new Label
        {
            Text = "Quick Jump to GDB Domain:",
            Modulate = new Color(0.75f, 0.82f, 0.92f)
        };
        jumpLbl.AddThemeFontSizeOverride("font_size", 13);
        jumpRow.AddChild(jumpLbl);

        foreach (string domain in new[] { "Weapons", "Clothing", "Potions", "Furniture & Props", "Creatures & NPCs" })
        {
            string captured = domain;
            var btn = new Button { Text = $"Open {captured}" };
            btn.AddThemeFontSizeOverride("font_size", 13);
            btn.Pressed += () => ModdingHubPanel.Instance?.OpenGdbDomain(captured);
            jumpRow.AddChild(btn);
        }

        page.AddChild(jumpRow);
        return page;
    }

    // =========================================================================
    // SUB-TAB 2: MODDING MODELS, TEXTURES & .GMD SOCKETS
    // =========================================================================
    private Control BuildModelsTexturesGmdPage()
    {
        var page = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        page.AddThemeConstantOverride("separation", 10);

        page.AddChild(CreateSectionCard(
            "🎨 3D Models (.glb), Textures & Full PBR Material Pipeline",
            new Color(0.35f, 0.82f, 1.0f),
            "Every Model and Texture property in the GDB Component Inspector is an interactive Drop Zone:\n" +
            "  • Model Drop Zones show a cyan '📥 DROP .GLB' badge and accept .glb, .gltf, or .mdl files.\n" +
            "  • Texture Drop Zones show a purple '📥 DROP .PNG' badge and accept .png, .dds, or .tex files.\n\n" +
            "🌟 Full PBR Materials Workflow (Automatic glTF Extraction):\n" +
            "  • Author in Blender / Substance / Maya: Apply standard PBR materials to your mesh with Base Color, Metallic-Roughness, Ambient Occlusion (AO), Normal, and Emission maps.\n" +
            "  • Export as .glb: Standard glTF 2.0 exports embed these texture channels directly inside the .glb binary.\n" +
            "  • Drag-and-Drop onto Model: When you drop your .glb into Mod Studio, the background tools automatically:\n" +
            "      1. Extract all embedded textures and compile Base Color, Normal, and Emission into Xbox/Refii .tex streams.\n" +
            "      2. Channel-pack Metallic-Roughness (Green = Roughness, Blue = Metallic) and Occlusion (Red = AO) into unified ORM PNG bytes (Red = AO, Green = Roughness, Blue = Metallic).\n" +
            "      3. Package mat{i}_diffuse_orm.png and mat{i}_orm.png companion sidecars directly into your .f2mod archive and register them in the engine texture index.\n" +
            "      4. Automatically synthesize neutral diffuse textures for untextured PBR surfaces (e.g. solid metallic gold or rough plastic defined solely by factors) so the companion ORM binds seamlessly!\n" +
            "  • Non-PBR Cleanliness: Models without authored metallic-roughness or occlusion maps are strictly preserved as standard legacy materials (no dummy flat ORMs are generated).\n\n" +
            "🎨 Dedicated Surface Material Slots & Manual Loose ORM / Emission Assignment:\n" +
            "  • Material Slots are Fully Dynamic & Model-Driven: If your imported model has 1 material, exactly 1 slot set is shown. If your model has 2, 3, or more materials (even if the vanilla template model only had 1), Mod Studio automatically detects the model's surfaces and allocates all required material slots dynamically!\n" +
            "  • Click '▾ 🎨 Surface Material Textures' below any Model property to expand its submaterials.\n" +
            "  • Each material exposes Diffuse, Normal, Specular, a dedicated 'Mat {i} · Emission' slot, and a 'Mat {i} · PBR ORM' slot!\n" +
            "  • Glowing Runes / Lights: Drop an emissive .png directly onto 'Emission' to make magic runes, neon glows, or glowing gemstones radiate light without modifying the diffuse base texture.\n" +
            "  • Have a loose authored _orm.png file? Simply drag-and-drop your _orm.png onto the 'PBR ORM' slot to bind Occlusion, Roughness, and Metallic.\n" +
            "  • The slot illuminates in cyan (ORM) or warm amber (Emission) when an authored map is active. Click the '↺' revert button to purge any authored map and restore baseline rendering.\n\n" +
            "📤 Clean Model Exports:\n" +
            "  • Clicking '📤' on a Model property exports only the clean .glb model and its .mdl.gmd sockets into res://creator/<Mod>/<ItemName>/. It will never dump loose textures or dummy flat ORMs into your folder.\n" +
            "  • To export an individual texture as .png, click the '📤' export button on that specific texture slot under Surface Material Textures."));

        page.AddChild(CreateSectionCard(
            "🎯 What Is a .mdl.gmd File & Why Does Weapon Rotation / Hand Grip Depend on It?",
            new Color(1.0f, 0.78f, 0.32f),
            "In Fable II, every 3D model (.mdl) has a companion sidecar file named <model>.mdl.gmd ('GameMesh' v3 skeleton & attachment metadata).\n\n" +
            "Why this matters for weapons & items:\n" +
            "  • Fable II does NOT just place a weapon mesh at (0,0,0) in the Hero's hand! Instead, the engine pairs a parent dummy on the Character skeleton (such as 'Character.Carry.Hand.Right') with a child Attachment Socket inside the weapon's .mdl.gmd file.\n" +
            "  • 'Weapon.Weapon.Hold' is on EVERY weapon in Fable II: it defines the exact position and rotation where the Hero's right hand grips the handle/hilt.\n" +
            "  • 'Weapon.Weapon.UpperHold' is on 2-handed weapons (rifles, blunderbusses, crossbows, greatswords, hammers) for the left-hand foregrip/secondary grip.\n" +
            "  • 'Weapon.Weapon.Sheath' defines where the weapon attaches to the Hero's hip holster or back sheath when not drawn.\n" +
            "  • 'Weapon.Weapon.ProjectileEmitter' & 'Weapon.FX.Particle.*' define the barrel-tip muzzle origin and forward trajectory where bullets and muzzle flash particles spawn.\n" +
            "  • 'Weapon.Weapon.Top' defines the blade/hammer tip on melee weapons (measuring weapon reach from Hold → Top for hit sweeps and trails).\n" +
            "  • Carried props (mugs, potions, wooden toy swords) use 'Prop.Carry.1Hand.Right' and 'Prop.Layout.Position'.\n\n" +
            "How Refii Handles .mdl.gmd Automatically:\n" +
            "  1. On Export (📤): When you export a model to .glb, Mod Studio reads its .mdl.gmd sidecar and embeds a 'GmdDummies' Empty node hierarchy right inside the .glb (converted from Fable II's Z-Up space to standard glTF +Y Up space).\n" +
            "  2. In Blender: You see the exact socket Empties (like 'Weapon.Weapon.Hold', 'Weapon.Weapon.Sheath', and 'Weapon.Weapon.ProjectileEmitter') right on your mesh! Keep your mesh aligned with the vanilla template mesh, or move the socket Empties to fit your custom weapon's grip and barrel tip.\n" +
            "  3. On Import / Drag-and-Drop (📥): When you drag your .glb onto the Model drop zone, Mod Studio extracts any 'GmdDummies' Empties (or any Empty matching a Fable II socket name), converts their transforms back to GameMesh Z-Up space, and writes an updated .mdl.gmd sidecar into your .f2mod package! (If your .glb has no socket Empties, Mod Studio safely preserves the template item's baseline .mdl.gmd sockets)."));

        page.AddChild(CreateSectionCard(
            "🧩 Using the Refii Blender Plugin (res://modding_tools/refii_blender_tools.py)",
            new Color(0.45f, 0.95f, 0.68f),
            "We built a dedicated Blender add-on located at res://modding_tools/refii_blender_tools.py that visualizes Fable II sockets right in Blender's 3D Viewport!\n\n" +
            "What the Blender Add-on Does:\n" +
            "  • Live 3D Viewport Overlays: Draws a cyan Right-Hand Grip Cylinder + Aim Arrow at 'Weapon.Weapon.Hold' / 'Prop.Carry.*', a green 2H Foregrip Cylinder at 'Weapon.Weapon.UpperHold', an orange Muzzle Cone + Bullet Trajectory Ray at 'Weapon.Weapon.ProjectileEmitter', a magenta Blade Reach line from 'Weapon.Weapon.Hold' to 'Weapon.Weapon.Top', a blue Holster Diamond at 'Weapon.Weapon.Sheath', and a gold Starburst at 'Weapon.FX.Particle.*'.\n" +
            "  • 1-Click Weapon & Prop Rig Presets: Building a custom weapon from scratch? Click 'Spawn Pistol Rig', 'Spawn Rifle / Blunderbuss Rig', 'Spawn Crossbow Rig', 'Spawn 1H Melee Rig', 'Spawn 2H Greatsword / Hammer Rig', or 'Spawn Carried Prop Rig' in the N-Panel ('Refii Fable 2' tab) to create all required GmdDummies sockets with authentic Fable II grip rotations.\n" +
            "  • 1-Click 'Export Refii .glb': Exports your scene with +Y Up, Apply Modifiers, Custom Properties (export_extras=True), and Tangents automatically configured.\n\n" +
            "How to Install in Blender (3.6 / 4.x):\n" +
            "  1. Click the '[ 🧩 Open res://modding_tools/ (Blender Plugin) ]' button at the top of this guide.\n" +
            "  2. In Blender, open Edit → Preferences → Add-ons → Install... and select refii_blender_tools.py.\n" +
            "  3. Enable 'Import-Export: Refii: Fable II Model & GMD Socket Tools', then press N in the 3D Viewport and open the 'Refii Fable 2' tab!"));

        // Interactive Socket Reference Table Card
        var tableCard = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var cardStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.10f, 0.14f),
            BorderColor = new Color(0.24f, 0.34f, 0.48f),
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        tableCard.AddThemeStyleboxOverride("panel", cardStyle);

        var tableVBox = new VBoxContainer();
        tableVBox.AddThemeConstantOverride("separation", 6);

        var tableHeader = new Label
        {
            Text = "📋 Standard Fable II .mdl.gmd Socket Names (Click Copy to paste into Blender):",
            Modulate = new Color(0.95f, 0.82f, 0.42f)
        };
        tableHeader.AddThemeFontSizeOverride("font_size", 15);
        tableVBox.AddChild(tableHeader);

        foreach (var sock in Engine_GmdWriter.StandardSockets)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);

            var catLbl = new Label
            {
                Text = $"[{sock.Category}]",
                CustomMinimumSize = new Vector2(115, 0),
                Modulate = new Color(0.55f, 0.80f, 0.98f)
            };
            catLbl.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(catLbl);

            var nameBox = new LineEdit
            {
                Text = sock.Name,
                Editable = false,
                CustomMinimumSize = new Vector2(235, 0)
            };
            nameBox.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(nameBox);

            var descLbl = new Label
            {
                Text = sock.Description,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.75f, 0.80f, 0.88f)
            };
            descLbl.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(descLbl);

            string capturedName = sock.Name;
            var copyBtn = new Button
            {
                Text = "📋 Copy",
                CustomMinimumSize = new Vector2(74, 24)
            };
            copyBtn.AddThemeFontSizeOverride("font_size", 12);
            copyBtn.Pressed += () =>
            {
                DisplayServer.ClipboardSet(capturedName);
                ShowToast($"Copied '{capturedName}' to clipboard!");
            };
            row.AddChild(copyBtn);

            tableVBox.AddChild(row);
        }

        tableCard.AddChild(tableVBox);
        page.AddChild(tableCard);

        return page;
    }

    // =========================================================================
    // SUB-TAB 3: MODDING LEVELS & 3D PLACEMENTS
    // =========================================================================
    private Control BuildLevelsPage()
    {
        var page = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        page.AddThemeConstantOverride("separation", 12);

        page.AddChild(CreateSectionCard(
            "🗺️ Fable II Region Architecture Overview",
            new Color(0.55f, 0.78f, 1.0f),
            "Fable II regions (such as Bowerstone Market, Rookridge, or Fairfax Castle) are composed of:\n" +
            "  • Region .level / .entity / .tng Streams: Packed inside region BNK archives. These stream models, materials, lights, and water into the rendering engine.\n" +
            "  • Scenario Manifests (.list): Each region has one or more scenarios (e.g. 'defaultscenario', 'epilogue') defining which .gdb database, .save layer map, and .havok_scenario physics packfile are loaded.\n" +
            "  • GDB Game Database: Defines entity properties, components, transforms, and model references.\n" +
            "  • .save Layer Maps: Defines layer containers (e.g. 'DefaultLayer', 'QuestIntro_Layer') and their initial runtime load state (AlwaysOn, InitiallyOn, InitiallyOff).\n" +
            "  • 3D Level Editor Scene: When you open a region level in the Refii Godot editor, it reconstructs the 3D world with 1:1 GDB record nodes organized under their .save layer containers."));

        page.AddChild(CreateSectionCard(
            "🧭 Top Viewport Toolbar: Controls & Buttons Reference",
            new Color(0.60f, 0.90f, 0.75f),
            "When a Fable II level scene is opened in Godot, a custom toolbar appears at the top of the 3D viewport:\n\n" +
            "1. 🗺️ Level: <Scenario> (Scenario Dropdown):\n" +
            "   • Switches between scenarios defined for the region (e.g. 'defaultscenario', 'epilogue').\n" +
            "   • Selecting a scenario re-populates the 3D viewport with that specific scenario's .list, .gdb records, and .save layer hierarchy.\n\n" +
            "2. 🗂️ Save Layers (Layers & Load States Popover):\n" +
            "   • Opens an interactive management panel for all .save layer containers in the level.\n" +
            "   • Checkbox toggles: Instantly show or hide specific layers in the 3D viewport to declutter your workspace.\n" +
            "   • '🎮 Default Game State (On Only)': Quick preset that shows AlwaysOn and InitiallyOn layers while hiding InitiallyOff layers, matching the clean in-game state.\n" +
            "   • '👁️ Show All Layers': Makes all layers visible, including hidden quest and script props.\n" +
            "   • '👻 Ghost / Translucent Mode': Renders InitiallyOff props at 45% opacity so you can visually identify inactive/scripted objects without hiding them completely.\n" +
            "   • Load State Dropdown: Change a layer's runtime state (AlwaysOn, InitiallyOn, InitiallyOff). When saved, this writes sparse load-state overrides to <level>.save.tu.\n\n" +
            "3. 🗑️ Deleted (N) (Deleted Entities Popover):\n" +
            "   • Displays the count of vanilla entities that have been removed/deleted from the 3D scene.\n" +
            "   • Clicking the button lists all deleted entities with their name, hex hash, and layer assignment.\n" +
            "   • Click '↺ Restore' next to any entity to instantly bring it back into the scene at its exact vanilla coordinates.\n\n" +
            "4. Target: <Layer> (Target Layer Dropdown):\n" +
            "   • Selects which .save layer newly placed or duplicated props will be assigned to (default: 'DefaultLayer').\n" +
            "   • When you press Ctrl+D or add new models, they are automatically organized under this layer.\n\n" +
            "5. 👁️ Overlays (Category Visibility Menu):\n" +
            "   • Checkable dropdown menu to selectively show or hide broad scene categories:\n" +
            "     - Props (1:1 GDB / .save entities)\n" +
            "     - Static Environment (.engine_level static geometry)\n" +
            "     - Entities & NPCs (characters, creatures, combat actors)\n" +
            "     - Markers: NPC & Village, Combat Spawners, Patrols/Splines/Vaults, Triggers & Cameras\n" +
            "     - Kynapse AI NavGraph (.pdl navmesh overlay)\n\n" +
            "6. 💾 Save Level to Mod (Primary Save Action):\n" +
            "   • Scans your 3D viewport edits, calculates sparse differences against vanilla baselines, and compiles:\n" +
            "     - <level>.gdb.tu (sparse transform changes and new placement records)\n" +
            "     - <level>.save.tu (sparse layer additions, removals, and load-state overrides)\n" +
            "   • Automatically embeds these compiled Title Updates into your active .f2mod package!\n\n" +
            "7. 🔄 Revert to GDB:\n" +
            "   • Discards uncommitted 3D viewport edits, snaps all moved objects back to their vanilla GDB transforms, restores deleted entities, and removes unsaved new placements."));

        page.AddChild(CreateSectionCard(
            "🛠️ Step-by-Step Level Editing Workflow",
            new Color(0.70f, 0.85f, 1.0f),
            "Follow this workflow to edit levels safely and reliably:\n\n" +
            "Step 1: Set Your Active Mod\n" +
            "  • Go to the '📦 Mod Management' tab and make sure your target mod is set as Active (e.g. 'MyLevelMod').\n" +
            "  • All level edits compiled via '💾 Save Level to Mod' are persisted directly into this mod package.\n\n" +
            "Step 2: Open the 3D Level Scene\n" +
            "  • In the Bnk File System tree, navigate to your desired level folder (e.g. 'worlds/albion/bwsmarket').\n" +
            "  • Double-click the scene file to open the 3D level in Godot's viewport.\n\n" +
            "Step 3: Choose Your Target Layer\n" +
            "  • Set the 'Target:' dropdown on the top toolbar to the appropriate .save layer (e.g. 'DefaultLayer').\n\n" +
            "Step 4: Edit Objects with 3D Gizmos\n" +
            "  • Select an object in the viewport or Scene Tree.\n" +
            "  • Move (W), Rotate (E), or Scale (R) the object using Godot's standard 3D transform gizmos.\n" +
            "  • Duplicate (Ctrl+D): Clones the selected prop, creates a unique GDB entity hash, and assigns it to your Target layer.\n" +
            "  • Delete (Del): Deletes the prop from the scene. If it was a vanilla entity, it is tracked in the '🗑️ Deleted' popover.\n\n" +
            "Step 5: Inspect & Tweak in the GDB Inspector Card\n" +
            "  • Select any 1:1 GDB record node to inspect its vanilla vs current transforms in the bottom inspector card.\n" +
            "  • You can swap visual models by editing 'ModelFile', move the object to another layer, or click '↺ Reset' to undo changes.\n\n" +
            "Step 6: Click '💾 Save Level to Mod' (DO NOT rely solely on Godot's Ctrl+S!)\n" +
            "  • Click the '💾 Save Level to Mod' button on the top toolbar.\n" +
            "  • The compiler analyzes the scene, builds sparse .gdb.tu and .save.tu files, and writes them into your .f2mod package.\n" +
            "  • Verify the status banner turns green: '✓ Saved [ModName]: X moved, Y added, Z removed'.\n\n" +
            "Step 7: Test In-Game\n" +
            "  • Launch Refii! Your mod Title Updates overlay seamlessly over the vanilla game world in real time."));

        page.AddChild(CreateSectionCard(
            "🧱 Level Collisions: Placements, Visual Swapping & Adding Colliders",
            new Color(0.95f, 0.75f, 0.35f),
            "Static collision in Fable II is pre-baked into a monolithic Havok world mesh (<scenario>.havok_scenario / fable_world.hkx). Here is how collision works in modding:\n\n" +
            "⚠️ WARNING: Moving Vanilla Static Scenery Objects:\n" +
            "  • Because static collision is compiled into the monolithic world mesh, moving or deleting a vanilla scenery object (e.g. houses, market stalls, tables, rocks, fences) leaves behind its baked collision at the original location, creating an invisible 'ghost barrier'.\n" +
            "  • Moving vanilla static objects should be avoided. Modifying or excluding pre-baked collision without full static collision mesh visualization can create holes in the floor if the 2008 level baker culled terrain triangles under the prop.\n\n" +
            "🎨 Swapping Visual Models (ModelFile Override):\n" +
            "  • Swapping the visual mesh of a vanilla static prop (changing 'ModelFile' in the GDB Inspector) is fully supported and safe!\n" +
            "  • Keep in mind that swapping the visual mesh does NOT update the collision mesh — the object will retain the original vanilla collision boundaries.\n\n" +
            "✨ Adding Collision to New or Duplicated Objects (Fully Supported):\n" +
            "  • Adding collision to custom objects, new placements, or duplicated props is straightforward and natively supported without runtime hacks.\n" +
            "  • To give collision to an object, create a Havok collision file (.hkx) in Blender/Havok tools and attach a native GDB physics component in the GDB Hierarchy tab:\n" +
            "      - Component: PhysicsSimulationDynamicComponent (0xFC8A57C5) or PhysicsSimulationStaticComponent (0x5883C406)\n" +
            "      - PhysicsFile (0x92F5FEEE): Set to your Havok collision file (.hkx) inside your mod package (e.g. res://creator/<YourMod>/<PropName>.hkx)\n" +
            "      - MotionType (0xA4864A5F): Set to Fixed (1) for static, immovable scenery\n" +
            "  • Native Havok collision natively supports convex hulls, compound shapes, and full trimeshes for complex architecture, stairs, archways, and buildings with hollow walkable interiors."));

        page.AddChild(CreateSectionCard(
            "⚠️ Critical Pitfalls & Known Pipeline Limitations",
            new Color(1.0f, 0.50f, 0.50f),
            "Keep these essential rules in mind when creating level mods:\n\n" +
            "1. Godot Scene Save (Ctrl+S) vs '💾 Save Level to Mod':\n" +
            "   • Saving the Godot scene (Ctrl+S / Scene -> Save Scene) only saves the temporary Godot editor .tscn file!\n" +
            "   • The game CANNOT read Godot .tscn files. To apply your changes to the game, you MUST click '💾 Save Level to Mod' on the top toolbar to generate .gdb.tu and .save.tu files.\n\n" +
            "2. New & Duplicated Items Do NOT Have Collision By Default:\n" +
            "   • When you place or duplicate an object in the 3D viewport, it is given a visual mesh (StaticMeshComponent) and a transform (SimpleTransformComponent).\n" +
            "   • The player and NPCs will walk straight through it unless you author a .hkx collision mesh and link a PhysicsSimulationStaticComponent in the GDB.\n\n" +
            "3. Ghost Collision from Moved Vanilla Props:\n" +
            "   • As noted above, moving or deleting vanilla static props leaves behind their baked Havok collision in the world. If you need an area cleared, prefer redecorating around vanilla props or replacing their visual meshes rather than moving them across the map.\n\n" +
            "4. Scene Hierarchy Organization is Safe:\n" +
            "   • The Refii level compiler computes transforms relative to world region coordinates, NOT Godot parent-node local space.\n" +
            "   • Grouping nodes under visual containers or moving nodes between layer folders in the Godot scene tree will NEVER cause false transform diffs or misalign your objects in-game."));

        return page;
    }

    // =========================================================================
    // SUB-TAB 4: MODDING LUA SCRIPTS & QUESTS
    // =========================================================================
    private Control BuildLuaPage()
    {
        var page = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        page.AddThemeConstantOverride("separation", 10);

        page.AddChild(CreateSectionCard(
            "📜 Modding Lua Scripts & Runtime Hooks",
            new Color(0.85f, 0.65f, 1.0f),
            "Fable II uses an embedded Lua 5.1 virtual machine to drive quest logic, NPC schedules, combat abilities, and world triggers.\n\n" +
            "How Lua Modding Works in .f2mod Packages:\n" +
            "  • Entrypoint Script: A mod package can include 'scripts/mod_init.lua' (or custom script files under 'scripts/') to execute custom Lua code when the session starts.\n" +
            "  • GDB Script References: Many GDB components (such as EntityScriptComponent or QuestComponent) reference Lua script names or state machines that can be inspected and overridden in the GDB Hierarchy tab.\n" +
            "  • Dedicated visual level placement and interactive Lua script template tools are being expanded in upcoming Mod Studio updates."));

        return page;
    }

    // =========================================================================
    // SUB-TAB 5: THE SCRIPT API AND HARD MODS
    // =========================================================================
    private Control BuildScriptApiPage()
    {
        var page = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        page.AddThemeConstantOverride("separation", 10);

        page.AddChild(CreateSectionCard(
            "🧩 RefiiModApi — everything a mod is, as calls",
            new Color(0.55f, 0.85f, 1.0f),
            "Every kind of content a mod can carry can be written from a script: an EditorScript (File > Run), a tool script, or C#. " +
            "Each call writes into the ACTIVE mod — the one the Studio is editing — re-mounts the mods and refreshes the Studio, exactly as saving from the Studio's own editors does.\n\n" +
            "  var api := RefiiModApi.new()\n" +
            "  api.CreateMod(\"my_mod\", \"My Mod\", \"Me\", \"What it does\")   # or api.SetActiveMod(\"my_mod\")\n" +
            "  var error := api.SetModLua(\"CombatWheel.ActiveAttackerTimeOut = 6\")\n" +
            "  if error != \"\": push_error(error)\n\n" +
            "Calls that write return an error string; an empty string means it was written. Pass every argument — GDScript sees no defaults.\n\n" +
            "Mods: GetModsRoot, ListMods, GetActiveMod, SetActiveMod, CreateMod(mod, title, author, description), ImportFolder(folder) — packs a source folder (mod.toml as the metadata) into the active mod.\n" +
            "Files: ListFiles, WriteFile(path, bytes), WriteText, ReadFile, ReadText, DeleteFile — any file at its game path overrides the game's own.\n" +
            "Settings: SetSetting(key, value) — settings.json, the numbers a mod's native code reads with refii::mods::Setting(key, default); pushed to the game when the guest comes up, and changeable in a running game with refii_action \"mod-setting <key> <value>\"."));

        page.AddChild(CreateSectionCard(
            "📜 Lua, text and images",
            new Color(0.85f, 0.65f, 1.0f),
            "SetModLua(source) — mod/init.lua: runs in the game's Lua VM once per session, after the shipped scripts. Patch the shipped tables; never ship an edited copy of a shipped script.\n" +
            "SetCameraLua(source) — mod/camera.lua: runs after every load of the camera scripts, so it must be safe to run again.\n" +
            "CheckLua(source) — the game's own Lua compiler's verdict; both setters check before writing.\n" +
            "  • Each mod's Lua runs in its own pcall; an error lands in the global RefiiModErrors instead of stopping the game.\n" +
            "  • RefiiModSave: whatever a mod puts in this table is saved with the save game and back on load (numbers, strings, tables).\n\n" +
            "SetText(tag, value) / SetTextIn(language, tag, value) — one localised string, a row in language/<lang>/text/book.babel.csv.\n" +
            "SetImage(texPath, image) — a Godot Image served to the game as a .tex (e.g. art/gui/hud/my_icon.tex)."));

        page.AddChild(CreateSectionCard(
            "🖼️ HUD, abilities and menus",
            new Color(0.95f, 0.80f, 0.45f),
            "HUD (ui/hud.json):\n" +
            "  • SetHudClone({id, of, offset, hue, hueElements, icon, values, bars}) — a copy of a shipped piece, recoloured, with its own icon and fed by mod values. Souls Combat's mana and stamina bars are copies of the health bar.\n" +
            "  • SetHudPiece({id, kind: meter|panel|picture, ...}) — a piece of the mod's own; follow, widthFrom and chip place it against shipped pieces.\n" +
            "  • HideHudPiece(id), ShowHudPiece(id), SetHudScale(id, factor), RemoveHudEntry(id); ListShippedHudPieces() names what can be copied, followed or hidden.\n\n" +
            "Abilities (ui/abilities.json): SetAbility({name, category, nameTag, descriptionTag, maxLevel, level, cost, stat, buy, refund, refundValue, image}), RemoveAbility(name). " +
            "The row sits after its category's own abilities and reads the mod values its keys name; A buys and Y unlearns through the game's own boxes, and the mod's native code does the spending.\n\n" +
            "Menus and screens (ui/templates.json): PatchMenu(menu, entries) adds, replaces or removes entries by name (after/before/parent/remove), composing with other mods; SetScreen(id, record) replaces a screen; ListMenus() names the menus."));

        page.AddChild(CreateSectionCard(
            "⚙ Hard mods — native code",
            new Color(1.0f, 0.60f, 0.40f),
            "A hard mod ships C++ under native/. The launcher compiles it into the game code when the mod is enabled, after the player allows that mod's native code once per version.\n\n" +
            "SetNativeSource(fileName, source) — writes native/<fileName>.\n" +
            "GetNativeOverrides() — the game functions the mod overrides, read from its sources.\n" +
            "CheckNativeConflicts() — functions refii or another enabled mod also overrides; any one refuses the build, so resolve them before shipping.\n" +
            "GetNativeHeaderPath() — core/refii_mod.h, the header native mods compile against.\n\n" +
            "In the source:\n" +
            "  #include \"core/refii_mod.h\"\n" +
            "  REFII_MOD_ORIGINAL(rex_CPlayer_SetLookAtButtonDown);   // the game's own body, as __imp__<name>\n" +
            "  REX_EXTERN(rex_CPlayer_SetLookAtButtonDown) { REX_FUNC_PROLOGUE(); ...; __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base); }\n" +
            "  static void Tick(PPCContext* ctx, uint8_t* base, float seconds) { ... }\n" +
            "  REFII_MOD_ON_TICK(Tick);   // runs Tick once per game step\n\n" +
            "  • Hook functions by their config names (versions/*/refii_config.toml), never sub_ addresses — the names hold for both game builds.\n" +
            "  • Helpers: refii_mod_player_hero, refii_mod_local_hero, refii_mod_component, refii_mod_run_game_lua, refii_mod_game_lua_int, refii_mod_lua_generation, refii::mods::LoadU32/LoadF32/StoreU8/StoreU32/StoreF32, IsPlayerHero, CallGuest, CallVirtual.\n" +
            "  • Mod values join native code to the UI: refii::mods::Publish(\"my_mod.mana\", v) is read as \"mod:my_mod.mana\" by HUD pieces and abilities; the UI hands a setting back with SetModSetting, read by refii::mods::Setting(key, fallback)."));

        page.AddChild(CreateSectionCard(
            "🗡️ Worked example: Souls Combat",
            new Color(0.60f, 0.90f, 0.60f),
            "The bundled Souls Combat mod is built from exactly these pieces — mod/init.lua (enemy pacing), native/ (stamina, roll, guard, lock-on, mana, the Stamina ability), ui/hud.json (the mana and stamina copies of the health bar), ui/abilities.json, its Babel text and its icons. " +
            "Its source folder is mods-src/souls_combat in the refii tree; to take it apart or start a mod from it:\n\n" +
            "  var api := RefiiModApi.new()\n" +
            "  api.CreateMod(\"my_combat\", \"My Combat\", \"Me\", \"Souls Combat, my way\")\n" +
            "  api.ImportFolder(\"/path/to/mods-src/souls_combat\")\n" +
            "  api.SetText(\"SOULS_COMBAT_STAMINA_NAME\", \"Endurance\")\n" +
            "  print(api.ReadText(\"ui/hud.json\"))   # a Set* call replaces the whole record it names, so start from what is there\n\n" +
            "Then enable it in the launcher's Mods panel; the game code is rebuilt with its native sources on the next Play."));

        return page;
    }

    private static Control CreateSectionCard(string title, Color accentColor, string bodyText)
    {
        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.10f, 0.14f),
            BorderColor = accentColor,
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        card.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        vbox.AddThemeConstantOverride("separation", 6);

        var titleLbl = new Label
        {
            Text = title,
            Modulate = accentColor,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 15);
        vbox.AddChild(titleLbl);

        var bodyLbl = new Label
        {
            Text = bodyText,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.84f, 0.88f, 0.94f)
        };
        bodyLbl.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(bodyLbl);

        card.AddChild(vbox);
        return card;
    }
}
#endif
