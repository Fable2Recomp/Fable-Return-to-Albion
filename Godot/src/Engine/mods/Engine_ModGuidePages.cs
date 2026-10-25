using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Mods;

/// <summary>One card of a Modding Guide page: a title, its accent colour (RGB, 0..1) and its text.</summary>
public sealed record ModGuideCard(string Title, float R, float G, float B, string Body);

/// <summary>
/// One page of the Mod Studio's Modding Guide tab: the key the view builds its extra widgets by, the
/// navigation button's caption, the cards shown first, and the cards shown after the page's own widgets.
/// </summary>
public sealed record ModGuidePage(string Key, string Button, IReadOnlyList<ModGuideCard> Cards, IReadOnlyList<ModGuideCard> Closing);

/// <summary>
/// THE MODDING GUIDE'S PAGES as data. <c>ModdingGuideView</c> draws them; nothing here touches Godot, so
/// the tests hold every page to having content. The Mod API Guide page (<see cref="GuideKey"/>) has no
/// cards: it draws the markdown files <see cref="ModGuide"/> reads from <see cref="ModGuide.ResFolder"/>.
/// The events page's per-event cards are <see cref="EventCard"/>, generated from <see cref="ModApi"/>.
/// </summary>
public static class ModGuidePages
{
	public const string OverviewKey = "overview";
	public const string GdbKey = "gdb";
	public const string ModelsKey = "models";
	public const string LevelsKey = "levels";
	public const string LuaKey = "lua";
	public const string ScriptApiKey = "script-api";
	public const string EventsKey = "events";
	public const string FxKey = "fx";
	public const string GuideKey = "guide";

	/// <summary>Every page, in the order of the navigation buttons.</summary>
	public static readonly IReadOnlyList<ModGuidePage> All = new[]
	{
		new ModGuidePage(OverviewKey, "🚀 1. Overview & Workspaces",
			new[]
			{
				new ModGuideCard(
					"🚀 Welcome to the Fable II Mod Studio",
					0.32f, 0.72f, 1.0f,
					"Refii Mod Studio lets you inspect, override, and create new Fable II items, weapons, outfits, textures, 3D meshes, and game database records without touching raw hex files.\n\n" +
					"Every mod is stored in two places while you work in the editor:\n" +
					"  1. Creator Workspace (res://creator/<YourMod>/): Your editable source folder containing standard .glb 3D models and uncompressed .png textures for Blender, Substance Painter, or Photoshop.\n" +
					"  2. Compiled Mod Package (assets/mods/<YourMod>.f2mod): The self-contained archive that the game mounts at runtime. Whenever you drag-and-drop a .glb or .png onto a field or click 💾 Save Mod Overrides, Mod Studio automatically compiles your assets into Xbox/Refii formats (.mdl, .mdl.gmd, .tex, mod.gdb.tu, mod.babel.csv) inside your .f2mod package!"),
				new ModGuideCard(
					"⚡ The 5-Minute Modding Workflow",
					0.42f, 0.90f, 0.62f,
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
					"  • Click '[ 🔍 Inspect in 3D ]' in the record header to see your updated mesh, textures, and 3D attachment socket gizmos right inside the editor, or launch the game to test it live!"),
			},
			Array.Empty<ModGuideCard>()),
		new ModGuidePage(GdbKey, "🗂️ 2. GDB Records & Components",
			new[]
			{
				new ModGuideCard(
					"🗂️ What Are GDB Records & How Does Fable II's Database Work?",
					0.38f, 0.78f, 1.0f,
					"Fable II's Game Database (game.gdb) is an object-oriented hierarchy of entity definitions rooted at World Entity Root (0x46F79080).\n\n" +
					"• Every item, weapon, piece of clothing, creature, potion, building, and visual effect is a Node Record in this tree.\n" +
					"• Child nodes automatically inherit all Components and Properties from their parent chain. For example:\n" +
					"  World Entity Root → Object → ObjectInventory → Weapon → RangedWeapon → Pistol → Pistol_Clockwork → Toy Pistol.\n" +
					"• Instead of duplicating hundreds of fields on every item, a specific weapon only attaches or overrides the components that make it unique (such as its GraphicComponent model, InventoryItemComponent price/name, or WeaponComponent damage)!"),
				new ModGuideCard(
					"🧩 Direct Components vs. Inherited Components",
					0.95f, 0.78f, 0.35f,
					"When you select any record in the '🗂️ GDB Hierarchy & Components' tab, the right pane shows two sections:\n\n" +
					"1. Direct Components on This Node:\n" +
					"   • Components attached directly to this specific record. Editing properties here ONLY affects this node (and any children below it that don't override them).\n" +
					"   • Modified properties are highlighted in gold so you can immediately see what your mod changes.\n\n" +
					"2. Inherited Components (Walked Up Parent Chain):\n" +
					"   • Components that this item inherits from a parent template (e.g. RangedWeapon or ObjectInventory).\n" +
					"   • If you want to customize an inherited component on your item without changing other items that share the same parent, click the '[ Override ]' button on that inherited component card! Mod Studio automatically creates a child component record linked to your node.\n" +
					"   • If you want to completely remove an inherited behavior from your node, click '[ ✕ Strip ]' (which writes a native RemoveComponent entry)."),
				new ModGuideCard(
					"🌿 Creating Brand New Standalone Items ('+ Create Child Node')",
					0.45f, 0.92f, 0.65f,
					"You don't have to overwrite vanilla items! To create a brand new item:\n" +
					"  1. Select an existing item or template node that behaves like what you want (for example, 'Master Flintlock Pistol' or 'Highwayman Coat').\n" +
					"  2. Click '[ + Create Child Node ]' in the top-right of the inspector.\n" +
					"  3. Enter a Display Name (e.g. 'Dragonbreath Pistol'), an Internal GDB Record Name (auto-filled as 'ObjectInventory_DragonbreathPistol'), and an optional Description.\n" +
					"  4. Mod Studio computes the deterministic FNV-1a 32-bit hash for your new record, registers its localized name & description in your mod's Babel text table (mod.babel.csv), and branches it directly under the parent node.\n" +
					"  5. Override its GraphicComponent or WeaponComponent, drag-and-drop your custom .glb model, and click '💾 Save Mod Overrides'!"),
			},
			Array.Empty<ModGuideCard>()),
		new ModGuidePage(ModelsKey, "🎨 3. Models, Textures & .gmd Sockets",
			new[]
			{
				new ModGuideCard(
					"🎨 3D Models (.glb), Textures & Full PBR Material Pipeline",
					0.35f, 0.82f, 1.0f,
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
					"  • To export an individual texture as .png, click the '📤' export button on that specific texture slot under Surface Material Textures."),
				new ModGuideCard(
					"🎯 What Is a .mdl.gmd File & Why Does Weapon Rotation / Hand Grip Depend on It?",
					1.0f, 0.78f, 0.32f,
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
					"  3. On Import / Drag-and-Drop (📥): When you drag your .glb onto the Model drop zone, Mod Studio extracts any 'GmdDummies' Empties (or any Empty matching a Fable II socket name), converts their transforms back to GameMesh Z-Up space, and writes an updated .mdl.gmd sidecar into your .f2mod package! (If your .glb has no socket Empties, Mod Studio safely preserves the template item's baseline .mdl.gmd sockets)."),
				new ModGuideCard(
					"🧩 Using the Refii Blender Plugin (res://modding_tools/refii_blender_tools.py)",
					0.45f, 0.95f, 0.68f,
					"We built a dedicated Blender add-on located at res://modding_tools/refii_blender_tools.py that visualizes Fable II sockets right in Blender's 3D Viewport!\n\n" +
					"What the Blender Add-on Does:\n" +
					"  • Live 3D Viewport Overlays: Draws a cyan Right-Hand Grip Cylinder + Aim Arrow at 'Weapon.Weapon.Hold' / 'Prop.Carry.*', a green 2H Foregrip Cylinder at 'Weapon.Weapon.UpperHold', an orange Muzzle Cone + Bullet Trajectory Ray at 'Weapon.Weapon.ProjectileEmitter', a magenta Blade Reach line from 'Weapon.Weapon.Hold' to 'Weapon.Weapon.Top', a blue Holster Diamond at 'Weapon.Weapon.Sheath', and a gold Starburst at 'Weapon.FX.Particle.*'.\n" +
					"  • 1-Click Weapon & Prop Rig Presets: Building a custom weapon from scratch? Click 'Spawn Pistol Rig', 'Spawn Rifle / Blunderbuss Rig', 'Spawn Crossbow Rig', 'Spawn 1H Melee Rig', 'Spawn 2H Greatsword / Hammer Rig', or 'Spawn Carried Prop Rig' in the N-Panel ('Refii Fable 2' tab) to create all required GmdDummies sockets with authentic Fable II grip rotations.\n" +
					"  • 1-Click 'Export Refii .glb': Exports your scene with +Y Up, Apply Modifiers, Custom Properties (export_extras=True), and Tangents automatically configured.\n\n" +
					"How to Install in Blender (3.6 / 4.x):\n" +
					"  1. Click the '[ 🧩 Open res://modding_tools/ (Blender Plugin) ]' button at the top of this guide.\n" +
					"  2. In Blender, open Edit → Preferences → Add-ons → Install... and select refii_blender_tools.py.\n" +
					"  3. Enable 'Import-Export: Refii: Fable II Model & GMD Socket Tools', then press N in the 3D Viewport and open the 'Refii Fable 2' tab!"),
			},
			Array.Empty<ModGuideCard>()),
		new ModGuidePage(LevelsKey, "🗺️ 4. Levels & 3D Placements",
			new[]
			{
				new ModGuideCard(
					"🗺️ Fable II Region Architecture Overview",
					0.55f, 0.78f, 1.0f,
					"Fable II regions (such as Bowerstone Market, Rookridge, or Fairfax Castle) are composed of:\n" +
					"  • Region .level / .entity / .tng Streams: Packed inside region BNK archives. These stream models, materials, lights, and water into the rendering engine.\n" +
					"  • Scenario Manifests (.list): Each region has one or more scenarios (e.g. 'defaultscenario', 'epilogue') defining which .gdb database, .save layer map, and .havok_scenario physics packfile are loaded.\n" +
					"  • GDB Game Database: Defines entity properties, components, transforms, and model references.\n" +
					"  • .save Layer Maps: Defines layer containers (e.g. 'DefaultLayer', 'QuestIntro_Layer') and their initial runtime load state (AlwaysOn, InitiallyOn, InitiallyOff).\n" +
					"  • 3D Level Editor Scene: When you open a region level in the Refii Godot editor, it reconstructs the 3D world with 1:1 GDB record nodes organized under their .save layer containers."),
				new ModGuideCard(
					"🧭 Top Viewport Toolbar: Controls & Buttons Reference",
					0.60f, 0.90f, 0.75f,
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
					"   • Discards uncommitted 3D viewport edits, snaps all moved objects back to their vanilla GDB transforms, restores deleted entities, and removes unsaved new placements."),
				new ModGuideCard(
					"🛠️ Step-by-Step Level Editing Workflow",
					0.70f, 0.85f, 1.0f,
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
					"  • Launch Refii! Your mod Title Updates overlay seamlessly over the vanilla game world in real time."),
				new ModGuideCard(
					"🧱 Level Collisions: Placements, Visual Swapping & Adding Colliders",
					0.95f, 0.75f, 0.35f,
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
					"  • Native Havok collision natively supports convex hulls, compound shapes, and full trimeshes for complex architecture, stairs, archways, and buildings with hollow walkable interiors."),
				new ModGuideCard(
					"⚠️ Critical Pitfalls & Known Pipeline Limitations",
					1.0f, 0.50f, 0.50f,
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
					"   • Grouping nodes under visual containers or moving nodes between layer folders in the Godot scene tree will NEVER cause false transform diffs or misalign your objects in-game."),
			},
			Array.Empty<ModGuideCard>()),
		new ModGuidePage(LuaKey, "📜 5. Lua Scripts & Quests",
			new[]
			{
				new ModGuideCard(
					"📜 Modding Lua Scripts & Runtime Hooks",
					0.85f, 0.65f, 1.0f,
					"Fable II uses an embedded Lua 5.1 virtual machine to drive quest logic, NPC schedules, combat abilities, and world triggers.\n\n" +
					"How Lua Modding Works in .f2mod Packages:\n" +
					"  • Entrypoint Script: mod/init.lua runs in the game's VM once per session after the shipped scripts (RefiiModApi.SetModLua); mod/camera.lua after every camera-script load. Patch the shipped tables, never ship an edited copy of a shipped script.\n" +
					"  • Events: Refii.on(\"hit\", function(e) ... end) subscribes to the mod API's events from Lua — see tab 7 for every event and its fields. return true cancels where the event allows it.\n" +
					"  • GDB Script References: Many GDB components (such as EntityScriptComponent or QuestComponent) reference Lua script names or state machines that can be inspected and overridden in the GDB Hierarchy tab."),
				new ModGuideCard(
					"🧠 The game's own Lua API — 3,602 natives in 234 tables",
					0.55f, 0.85f, 1.0f,
					"Everything the shipped scripts can call, a mod's init.lua can call: the game registers its natives one by one at session start (the binary's own registrars), and the whole list — read from the game's code — is res://modding_tools/game_lua_api.tsv (table, name, native address).\n\n" +
					"  • Globals (_G, 122): CreateEntityFromIDAtPosition, GetDistanceBetweenEntities, AutoSaveGame, FNVHash, Entity(...), CVector3(...) ...\n" +
					"  • Component classes as tables: Player (110), Dog (140), Creature (40), Stats (70), Combat (76), SpellManager (48), Village (66), QuestTracker (57), Villager (56), PlayerFamily (54), Camera (64), Navigation (63), GUI (120), Debug (485) ...\n" +
					"  • Userdata methods: EntityMetatable (Destroy, GetPosition ...), CVector3Metatable, InventoryItemPtrMetatable, RecordPtrMetatable, MessageEventMetatable, AnimControl metatables.\n\n" +
					"In the editor: RefiiModApi.ListGameLuaTables(), ListGameLuaNatives(\"Player\"), FindGameLuaNatives(\"Gold\"). The shipped scripts (ai/*.lua, quests) show each call's arguments; the live debug channel's refii_lua '@game <chunk>' tries one in a running game."),
			},
			Array.Empty<ModGuideCard>()),
		new ModGuidePage(ScriptApiKey, "🧩 6. Script API & Hard Mods",
			new[]
			{
				new ModGuideCard(
					"🧩 RefiiModApi — everything a mod is, as calls",
					0.55f, 0.85f, 1.0f,
					"Every kind of content a mod can carry can be written from a script: an EditorScript (File > Run), a tool script, or C#. " +
					"Each call writes into the ACTIVE mod — the one the Studio is editing — re-mounts the mods and refreshes the Studio, exactly as saving from the Studio's own editors does.\n\n" +
					"  var api := RefiiModApi.new()\n" +
					"  api.CreateMod(\"my_mod\", \"My Mod\", \"Me\", \"What it does\")   # or api.SetActiveMod(\"my_mod\")\n" +
					"  var error := api.SetModLua(\"CombatWheel.ActiveAttackerTimeOut = 6\")\n" +
					"  if error != \"\": push_error(error)\n\n" +
					"Calls that write return an error string; an empty string means it was written. Pass every argument — GDScript sees no defaults.\n\n" +
					"Mods: GetModsRoot, ListMods, GetActiveMod, SetActiveMod, CreateMod(mod, title, author, description), ImportFolder(folder) — packs a source folder (mod.toml as the metadata) into the active mod.\n" +
					"Files: ListFiles, WriteFile(path, bytes), WriteText, ReadFile, ReadText, DeleteFile — any file at its game path overrides the game's own.\n" +
					"Settings: SetSetting(key, value) — settings.json, the numbers a mod's native code reads with refii::mods::Setting(key, default); pushed to the game when the guest comes up, and changeable in a running game with refii_action \"mod-setting <key> <value>\"."),
				new ModGuideCard(
					"📜 Lua, text and images",
					0.85f, 0.65f, 1.0f,
					"SetModLua(source) — mod/init.lua: runs in the game's Lua VM once per session, after the shipped scripts. Patch the shipped tables; never ship an edited copy of a shipped script.\n" +
					"SetCameraLua(source) — mod/camera.lua: runs after every load of the camera scripts, so it must be safe to run again.\n" +
					"CheckLua(source) — the game's own Lua compiler's verdict; both setters check before writing.\n" +
					"  • Each mod's Lua runs in its own pcall; an error lands in the global RefiiModErrors instead of stopping the game.\n" +
					"  • RefiiModSave: whatever a mod puts in this table is saved with the save game and back on load (numbers, strings, tables).\n\n" +
					"SetText(tag, value) / SetTextIn(language, tag, value) — one localised string, a row in language/<lang>/text/book.babel.csv.\n" +
					"SetImage(texPath, image) — a Godot Image served to the game as a .tex (e.g. art/gui/hud/my_icon.tex)."),
				new ModGuideCard(
					"🖼️ HUD, abilities and menus",
					0.95f, 0.80f, 0.45f,
					"HUD (ui/hud.json):\n" +
					"  • SetHudClone({id, of, offset, hue, hueElements, icon, values, bars}) — a copy of a shipped piece, recoloured, with its own icon and fed by mod values. Souls Combat's mana and stamina bars are copies of the health bar.\n" +
					"  • SetHudPiece({id, kind: meter|panel|picture, ...}) — a piece of the mod's own; follow, widthFrom and chip place it against shipped pieces; a picture with textures + index shows one of a list chosen by a mod value (Spell Weaving's partner glyph beside the charge meter).\n" +
					"  • HideHudPiece(id), ShowHudPiece(id), SetHudScale(id, factor), RemoveHudEntry(id); ListShippedHudPieces() names what can be copied, followed or hidden.\n\n" +
					"Abilities (ui/abilities.json): SetAbility({name, category, disciplines, nameTag, descriptionTag, maxLevel, levelKey, costLike, costLikeSetting, refund, stats, stat, legacySaveKey, image}), RemoveAbility(name). " +
					"The ENGINE runs it: the level is each player's saved value under levelKey, the price the game's own table of the ability costLike names split across the disciplines' pools, A buys and Y unlearns (\"refund\": \"half\") through the game's own boxes for the book's player, raising mod_ability first (a handler may refuse). The mod's code only reads the level where it applies the effect.\n\n" +
					"Menus and screens (ui/templates.json): PatchMenu(menu, entries) adds, replaces or removes entries by name (after/before/parent/remove), composing with other mods; SetScreen(id, record) replaces a screen; ListMenus() names the menus.\n\n" +
					"Spell slot picks (ui/spellwheel.json): { \"picks\": [ { \"name\": \"my_mod.pick\", \"store\": \"my_mod.pick_{slot}\", \"inEffect\": \"player:my_mod.pick_active_{slot}\", \"unlocked\": \"player:my_mod.level\", \"apply\": \"MY_MOD_SET\", \"remove\": \"MY_MOD_CLEAR\" } ] } — on the quick wheel and the abilities blade X on the highlighted will picks it for the highlighted slot and Y clears it, for the selectors' player; the engine raises spell_pick (a handler may refuse or change it) and saves the pick in that player's store under store. Every enabled mod's pick is drawn smaller under every assigned slot's icon, in load order; X and Y act on the player's active one (drawn larger) — a press with nothing to do on it, or LB/RB on the abilities blade, steps to the next. Once a pick's unlocked reads positive the abilities blade shows an X prompt and a Y prompt with the active pick's tags' text beside the page's own. Name a pick with your mod's prefix: a later mod's pick of the same name replaces it. Spell Weaving's partner pick.\n\n" +
					"Effects (fx/recipes.json): while a mod value equals a recipe's number (when/equals), or wherever a named effect plays (with), the live effects it names take a tint, intensity and size, authored pieces and attached effects on a turning ring. Spell Weaving plays each pair's look (fx/effects, bank entities, drawn under both looks) with play_fx_on on what each cast rides, and a with recipe tints that cast's own base effects."),
				new ModGuideCard(
					"🖼️ The UI tab — HUD, menus, modals and the settings page",
					0.55f, 0.85f, 1.0f,
					"The hub's UI tab edits the active mod's UI files against the game's OWN UI: the preview on the right is the game's HUD view and list view, built inside the editor from the banks on the editor's mount (the enabled mods' art included), with this mod's files as you are editing them composed in load order with the other enabled mods'. Nothing is written until 💾 Save UI, which makes the same per-entry RefiiModApi calls a script would (checked, then re-mounted once). Each page shows the schema check of its file as it would be saved, and its conflicts with the other enabled mods.\n\n" +
					"HUD page (ui/hud.json):\n" +
					"  • The mod's pieces — + Meter / Panel / Picture / Text, every field (anchor preset or anchor + offset, follow and followElement, size, value and max, colours, widthFrom, textures + index; a text piece's text — literal, Babel tag or mod:<key> — format, point size, colour, align).\n" +
					"  • The game's pieces — select one (or click it in the preview) to move it, hide it, scale it, or make a copy of it. A move is written to the mod's \"layout\" block: { \"<piece id>\": { \"anchor\": [x, y], \"offset\": [x, y] } }, the same entry as the player's user://hud/layout.json. Order: shipped < mods in load order < the player's file, so a player's own arrangement always wins.\n" +
					"  • The preview — drag any piece: a game piece writes layout, a mod piece its own anchor/offset (\"Snap anchor on drop\" re-anchors it to the nearest edge or centre so it follows that edge on other windows), a copy its offset. The Window menu shows 16:9, 21:9, 4:3 and 32:9, so the anchors can be checked. Stand-in values: one slider for every game meter and one per named source the pieces read — mod:<key>, game:<name>, player:<key> (Engine_HudModPieces.PreviewSource). The game's widgets drawn by their own readers (widget.*: world icons, world sprites, reticule, slow-time dial — each with a …2 for player two — and the achievement toast) stand in as frames you can drag, hide, scale and copy like any piece.\n\n" +
					"Menus page (ui/templates.json):\n" +
					"  • Every menu the game and the enabled mods state, and the mod's own. A menu the mod does not own is saved as a PATCH — only the rows you added, changed, moved or removed, as entries with after / before / parent / remove — computed against the menu as the game and the other mods leave it, so two mods' patches to the pause menu both land. A mod's own menu (+ Menu) is saved whole; a row opens it with \"opens\": \"<menu>\" or the command Opens:<menu>, and B returns.\n" +
					"  • Rows: + Row, + Child, → Nest, ← Promote, ↑ ↓, remove; name, tag, the command, opens, source, icon, eventType, rating, hostOnly, drop, counted, when. Every field a record carries is kept, shown or not.\n" +
					"  • A row's command is a VERB of the one dispatcher every list goes through — the front end, the pause book, the screen lists, mod menus, a screen's own buttons (revealedAccept / revealedBack / flipAction): Opens:<menu>, Mod:lua:<function>[:<arg>] (a Lua name path such as my_mod.on_row), Screen:open:<id>, setting:toggle:<key>, options:<page>, Inventory:…, PauseTab:<tab>, PauseSaving:<n>, PauseQuit:<n>, PauseCoop:leave|kick, QuitToDesktop, Net:… — any list's row may name any verb; one that belongs to another list (PauseTab: on a screen list) refuses and the log says why. A Godot script adds verbs of its own with RefiiHost.register_command. A command no verb claims is the front end's guest row name.\n" +
					"  • \"when\" builds a row only under a condition (player-two, coop, coop.online, coop.client, in-game; !name; a,b for both): the pause book is ONE menu, pause-root, for both players — player two's rows stand under player-two — so a patch reaches both. A patch entry with a \"when\" addresses that variant of a name; without, every variant.\n" +
					"  • Rows at runtime: \"source\": \"rows\" (or rows:<list>) shows what a script states with set_menu_rows (RefiiHost, Refii.set_menu_rows, refii_mod_set_menu_rows; per player), and \"source\": \"values:mod:<key>|<format>=<label>,...\" lists published values.\n" +
					"  • Row actions (\"rowActions\" in ui/templates.json): per shipped screen list by its menu template name (ItemsMenu, WeaponsMenu, ExpressionsMenu, AbilitiesMenu) a list of { button (X or Y), tag, command } — a prompt beside the page buttons, the button held from the guest, and on the press mod_menu_row with the list's name, the highlighted row, its own command and the button, then the action's command run as a mod menu row's (docs/modding/ui-and-hud.md, Row actions).\n" +
					"  • Screens: \"State in the mod\" copies a screen record as it runs into the mod to edit whole; \"Patch in the mod\" states only the fields the mod changes (\"patch\": true — lists replace, fields / switches / boxPaths merge per key, null removes a key); \"New host screen\" adds a book of the mod's own (id 1000+, a menu and a presentation) that a row opens with Screen:open:<id> (RefiiHost.open_screen, Refii.open_screen, refii_mod_open_screen) over the pause book. Its page is filled by provider \"data\": \"fields\" maps an element path to a binding — mod:<key>, player:<key> (the menu's player), game:<name>, row:label / row:name, a Babel tag or a literal, {row} the highlighted row's name, |<format> after a value — and \"switches\" an element to an on/off binding. Drop the mod's record to let the game's stand. The game's own templates are the same records (Engine_UiGameTemplates), composed first.\n" +
					"  • The preview is the game's list view with the front end's or the books' row art: click a row to highlight it, click it again to press it — an opens / Opens: row opens its menu over the list, ◀ Back (or right-click) returns; a Mod:lua: row names the call it makes in game.\n\n" +
					"Modals page: every shipped box row (13, or 0@22 for one screen) with what the mod states over it — Override (a whole { kind: query | message, confirm, cancel } record), Release to the guest (null: the guest's own input answers), or drop the mod's row.\n\n" +
					"Settings page (settings.ui.json): a row per number in settings.json — label, description, type (slider with min / max / step, toggle, or choice with options { value, label } in order). The preview is the page as the game's row model draws it (ModSettingsUi): each row's caption at settings.json's value, stepped with ◀ ▶.\n\n" +
					"The same HUD placement editor and menu editor are the player's tools (the HUD Layout and UI Menus docks of the hud_editor plugin), pointed at user://hud/layout.json and user://ui/templates.json: they write only the pieces and menus you changed, and keep every other entry and field of those files.\n\n" +
					"Script calls behind the tab: SetHudPiece, SetHudText, SetHudClone, HideHudPiece / ShowHudPiece, SetHudScale / RemoveHudScale, RemoveHudEntry, SetHudLayout / RemoveHudLayout / GetHudLayout; SetMenu (whole), SetMenuPatch (a whole patch, in order — a remove then a re-insert of one name moves a row), PatchMenu (merge entries by name), RemoveMenu, SetMenuRow / RemoveMenuRow, OpensCommand, LuaCommand, ParseMenuCommand; SetScreen / SetScreenPatch / RemoveScreen, OpenScreenCommand, ListRowVerbs, ListRowConditions, ListPageProviders, ValuesSource, CheckBinding; SetModal / ReleaseModal / RemoveModal, ShippedModals; SetSettingRow / SetSettingChoice / RemoveSettingRow. BeginChanges() … EndChanges() around many calls re-mounts the mods once."),
				new ModGuideCard(
					"⚙ Hard mods — native code",
					1.0f, 0.60f, 0.40f,
					"A hard mod ships C++ under native/. The launcher compiles it into the game code when the mod is enabled, after the player allows that mod's native code once per version.\n\n" +
					"SetNativeSource(fileName, source) — writes native/<fileName>.\n" +
					"GetNativeOverrides() — the game functions the mod overrides, read from its sources.\n" +
					"CheckNativeConflicts() — functions this mod replaces (its hook never calls the original) while refii or another enabled mod hooks them too; any one refuses the build.\n" +
					"GetNativeChains() — functions shared with other owners and the order their hooks run in.\n" +
					"GetNativeHeaderPath() — core/refii_mod.h, the header native mods compile against.\n\n" +
					"In the source:\n" +
					"  #include \"core/refii_mod.h\"\n" +
					"  REFII_MOD_ORIGINAL(rex_CPlayer_SetLookAtButtonDown);   // the game's own body, as __imp__<name>\n" +
					"  REX_EXTERN(rex_CPlayer_SetLookAtButtonDown) { REX_FUNC_PROLOGUE(); ...; __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base); }\n" +
					"  static void Tick(PPCContext* ctx, uint8_t* base, float seconds) { ... }\n" +
					"  REFII_MOD_ON_TICK(Tick);   // runs Tick once per game step\n\n" +
					"  • Hook functions by their config names (versions/*/refii_config.toml), never sub_ addresses — the names hold for both game builds.\n" +
					"  • Shared functions chain. When two mods (or a mod and refii) hook one function, the launcher compiles each hook under its own symbol and links them in load order: the higher mod's __imp__<name> call reaches the lower mod's hook, and the lowest reaches the game. A hook that returns without calling the original stops the chain, which is what a gate means. A hook that never calls the original replaces the body and must be the only owner of that function.\n" +
					"  • Depend on another mod with requires = [\"souls_combat >= 0.1\"] in mod.toml. The required mod is placed beneath this one and its native/include/ is on this mod's include path, so its exported C functions and its published values (refii::mods::Published) are directly usable. after = [\"hd-ui\"] orders without requiring.\n" +
					"  • Helpers: refii_mod_player_hero, refii_mod_local_hero, refii_mod_component, refii_mod_run_game_lua, refii_mod_game_lua_int, refii_mod_lua_generation, refii::mods::LoadU32/LoadF32/StoreU8/StoreU32/StoreF32, IsPlayerHero, CallGuest, CallVirtual.\n" +
					"  • Values join native code to the UI: a value that is each player's goes in the player store (refii_mod_player_set(p, \"my_mod.mana\", v)) and is read as \"player:my_mod.mana\" for whichever player a HUD piece or page belongs to; one value for everyone is published (refii::mods::Publish) and read as \"mod:my_mod.mana\". Settings (refii::mods::Setting(key, fallback)) are tunables, never a command channel: a choice the UI makes is an engine pick or event."),
				new ModGuideCard(
					"🗡️ Worked example: Souls Combat",
					0.60f, 0.90f, 0.60f,
					"The bundled Souls Combat mod is built from exactly these pieces — mod/init.lua (enemy pacing), native/ (stamina, roll, guard, lock-on, mana), ui/hud.json (the mana and stamina copies of the health bar, one record each for both players), ui/abilities.json (the Stamina ability, run by the engine), its Babel text and its icons. " +
					"Its source folder is mods-src/souls_combat in the refii tree; to take it apart or start a mod from it:\n\n" +
					"  var api := RefiiModApi.new()\n" +
					"  api.CreateMod(\"my_combat\", \"My Combat\", \"Me\", \"Souls Combat, my way\")\n" +
					"  api.ImportFolder(\"/path/to/mods-src/souls_combat\")\n" +
					"  api.SetText(\"SOULS_COMBAT_STAMINA_NAME\", \"Endurance\")\n" +
					"  print(api.ReadText(\"ui/hud.json\"))   # a Set* call replaces the whole record it names, so start from what is there\n\n" +
					"Then enable it in the launcher's Mods panel; the game code is rebuilt with its native sources on the next Play."),
			},
			Array.Empty<ModGuideCard>()),
		new ModGuidePage(EventsKey, "⚡ 7. Events & the Mod API",
			new[]
			{
				new ModGuideCard(
					$"⚡ The mod API, version {ModApi.Version} — events first, hooks second",
					1.0f, 0.85f, 0.40f,
					"The game's seams are named EVENTS a mod subscribes to: a hit, a swing, a block, a spell launch, experience changing, the tick. " +
					"An event never conflicts with another mod and needs no game function's name; refii hooks the function once, reads the fields, and hands every subscribed mod a struct (native) or a table (Lua). " +
					"Where the seam allows a refusal the event says what cancelling means; writable fields change what the game gets. Every handler sees every event, in priority order then load order (refii's own last); the state after the last handler is the verdict.\n\n" +
					"Native (native/*.cpp, compiled into the game code):\n" +
					"  #include \"core/refii_mod.h\"\n" +
					"  static void OnHit(refii::mods::HitEvent& e) {\n" +
					"    if (e.attacker_player != 0) e.damage *= 2.0f;                 // either hero hits twice as hard\n" +
					"    if (e.victim_player != 0 && e.spell_type == 2) e.cancel = true; // heroes ignore slow-time hits\n" +
					"  }\n" +
					"  REFII_MOD_ON(Hit, OnHit);            // REFII_MOD_ON_PRIORITY(Hit, OnHit, 10) runs earlier\n\n" +
					"Lua (mod/init.lua, no compiler needed):\n" +
					"  Refii.on(\"hit\", function(e)\n" +
					"    if e.victim_player ~= 0 and e.spell_type == 2 then return true end   -- true cancels\n" +
					"  end)\n\n" +
					"In the editor: RefiiModApi.ListEvents(), DescribeEvent(name), NewNativeHandler(event, \"file.cpp\") writes a filled-in native handler, AddLuaHandler(event) appends a Lua one to init.lua. " +
					"A hook on a game function (tab 6) stays for a seam no event covers; it chains with other owners as before.\n\n" +
					"The whole API, chapter by chapter: the 📘 Mod API Guide page (" + ModGuide.ProjectFolder + "/ in the SDK)."),
				new ModGuideCard(
					"🤝 Both players, always",
					0.60f, 0.90f, 0.60f,
					ModApi.PlayerAgnostic + "\n\n" +
					"In practice: key per-player state by the event's player (or Refii.player_of / refii_mod_player_of), never by \"the hero\"; ask Refii.player_for(e) when an NPC's or a dog's \"hero\" matters; " +
					"make a world effect where Refii.world_here() holds and a hero's own effect where Refii.player_here(p) holds; and skip world and body effects in a copy (Refii.event_replica()). " +
					"The events marked RELAYED below reach the other machine as copies, so the machine that needs them (the host's AI, the joiner's looks) gets them without a mod sending anything."),
			},
			new[]
			{
				new ModGuideCard(
					"🗡️ Worked examples: Souls Combat and Spell Weaving",
					0.60f, 0.90f, 0.60f,
					"Both bundled hard mods are built on these events (mods-src/souls_combat, mods-src/spell_weaving in the refii tree): stamina spends on MeleeSwing and RollStarted and refuses on AbilityRequest and Roll; the guard breaks through Block; poise absorbs through HitResponse; the lock-on toggles through GameAction; mana charges through SpellCharge, SpellChargeLevel and SpellLaunched; the weave stands its base in through SpellLaunch and casts the partner from HitLanded with refii_mod_cast_spell. " +
					"Neither hooks a game function by name: the look-at writer, the vortex and the fireball's impact are events too (look_at_button, vortex_started / vortex_ended, fireball_impact), so both mods are Lua on the API alone."),
			}),
		new ModGuidePage(FxKey, "✨ 8. Effects (FX)",
			new[]
			{
				new ModGuideCard(
					"✨ Effects: the guest says which and where, Godot draws",
					1.0f, 0.75f, 0.45f,
					"Every effect on screen is a FableFx node drawing the particle bank (art/particles/particle_bank.bnk). A mod changes effects three ways, all through that one bank and that one node:\n" +
					"  • RECIPES (fx/recipes.json) dress the live effects the game plays while a mod value holds — a tint, intensity and size, a light, attached effects, authored pieces.\n" +
					"  • EFFECTS (fx/effects/<name>.json) are the bank's own model as JSON, overlaid on the shipped bank when it loads: new effects, replaced effects, replaced emitters.\n" +
					"  • play_fx draws any bank or mod effect where a mod says, from native code, Lua or a Godot script.\n\n" +
					"The FX Lab (Mod Studio, the FX Lab tab) edits recipes and effects against a live turntable; selecting a FableFx in a scene adds 'Save as mod effect' and 'Edit in FX Lab' to the inspector."),
				new ModGuideCard(
					"🧪 fx/effects/<name>.json — the bank's own model",
					0.55f, 0.85f, 1.0f,
					"One effect object per file (or { \"effects\": [ ... ] }):\n" +
					"  { \"format\": 1, \"name\": \"my_mod_green_torch\", \"updateFrequency\": 1, \"scale\": 1, \"tint\": [1, 1, 1],\n" +
					"    \"parts\": [ { \"emitter\": 0, \"system\": 0, \"group\": 1, \"vector\": 0 } ],\n" +
					"    \"emitters\": [ { \"typeId\": 0, \"subType\": 1, \"maxActive\": 40, \"spawnRadius\": 0.2,\n" +
					"                    \"params\": { \"rate\": [[0, 12]], \"speed\": [[0, 1.5], [1, 0.2]] } } ],\n" +
					"    \"systems\": [ { \"mode\": 1, \"visuals\": [ { \"texture\": \"art\\\\fx\\\\pfx_textures\\\\fxt_flames_double_03.tex\", ... } ] } ] }\n\n" +
					"Every field of the model is a key in camelCase; a key left out is the model's default. parts index the file's own emitters and systems (-1 for none); \"vector\" is the chain an entity belongs to (each chain parents its entities in order), \"group\" its update bucket. " +
					"A generic parameter is params.<name> (rate, rateSpread, speed, speedSpread, accelDirScale, gravityScale, globalWindScale, velX, velY, velZ, wind, lightRange, lightIntensity, lightCutoff, lightFadeRamp, lightConeAngle, attractRadius, attractStrength, or p0..p27) as [[seconds, value], ...]; " +
					"a vector parameter is vectors.vec0|vec1|vec2|lightColour as [[seconds, x, y, z], ...]. The FIRST key starts a timeline and one key is a constant, as the bank's loader reads them.\n\n" +
					"THE OVERLAY, in load order (the highest mod last, and winning):\n" +
					"  • a new effect name ADDS an effect; a shipped name REPLACES it — every placement and every live spawn of it;\n" +
					"  • an emitter whose entityId the bank holds REPLACES that emitter wherever the bank uses it, so the game's own spawns draw the change; one without an id is new;\n" +
					"  • a system whose id the bank holds is replaced the same way;\n" +
					"  • two mods stating the same effect, emitter or system are a conflict, shown in the Mods panel beside file and text conflicts.\n" +
					"Script: CloneEffect(source, name) (fresh ids — nothing shipped changes), OverrideEffect(name) (ids kept — replaces), GetEffectJson, SetEffectJson, SetEmitterField(effect, emitter, key, value), SetVisualField(effect, system, visual, key, value), SetEmitterKeys(effect, emitter, param, keys), RemoveEffect."),
				new ModGuideCard(
					"▶ play_fx — on every surface",
					0.60f, 0.90f, 0.60f,
					"Native:  refii_mod_fx_options o = refii_mod_fx_default_options(); o.duration = 2.0f;\n" +
					"         uint32_t id = refii_mod_play_fx(\"my_mod_green_torch\", xyz, &o);   // or refii_mod_play_fx_on(name, entity, &o)\n" +
					"         refii_mod_stop_fx(id);\n" +
					"Lua:     local id = Refii.play_fx(\"my_mod_green_torch\", x, y, z, { duration = 2, scale = 1.5, tint = {1, 0.5, 0.2} })\n" +
					"         Refii.play_fx_on(e.victim, \"fxspl_blast_fire_ring\", { offset = {0, 0, 1}, loop = false })\n" +
					"         Refii.stop_fx(id)\n" +
					"Godot:   var host = Engine.get_singleton(\"RefiiHost\")\n" +
					"         var id = host.play_fx(\"my_mod_green_torch\", x, y, z, {\"duration\": 2.0, \"tint\": Color(1, 0.5, 0.2)})\n" +
					"         host.play_fx_on(\"fxspl_blast_fire_ring\", handle, {\"offset\": Vector3(0, 0, 1)})\n" +
					"         host.stop_fx(id)\n\n" +
					"Options: duration (seconds; 0 plays until stop_fx, or with loop false until the effect has run out), scale, tint, intensity, loop (true), offset (from the entity, the guest's axes). " +
					"Positions are the guest's world axes, as every event states them; an entity is a handle in Lua and Godot and an address natively. An effect on an entity starts at the entity's drawn node and rides it. " +
					"Every surface's id stops through any surface's stop_fx. Godot spawns the same FableFx node it draws every bank effect with; the game spawns nothing."),
				new ModGuideCard(
					"🎛 The FX Lab",
					0.85f, 0.65f, 1.0f,
					"Recipes: + New recipe, then every field — id, when and equals, instances and their radius, base, anchors, entities, every node, tint, intensity, size, the light, attached effects (with their own tint) and pieces (embers, arcs, ring, blades with their numbers, textures, meshes and colours). The turntable shows the base effect dressed as the game dresses it.\n\n" +
					"Effects: pick a bank effect (★ marks the active mod's own) — it opens as an OVERRIDE draft (saved as is, it replaces the shipped effect and its emitters); 'Clone into mod' makes it a new effect under a new name instead. " +
					"Edit each entity's fields for its type, its parameter timelines and vector timelines as key lists, its system and visuals (texture, sheet, colour, size, spin, trail, mesh, blend), and watch the change on the turntable. Save writes fx/effects/<name>.json, checked by the same reader the game uses."),
			},
			Array.Empty<ModGuideCard>()),
		new ModGuidePage(GuideKey, "📘 9. Mod API Guide", Array.Empty<ModGuideCard>(), Array.Empty<ModGuideCard>()),
	};

	/// <summary>The page with <paramref name="key"/>, or null.</summary>
	public static ModGuidePage? Find(string key) => All.FirstOrDefault(p => p.Key == key);

	/// <summary>The navigation index of the page with <paramref name="key"/>, or -1.</summary>
	public static int IndexOf(string key)
	{
		for (int i = 0; i < All.Count; i++)
		{
			if (All[i].Key == key)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>One event's card on the events page: its summary, how to subscribe, cancel, co-op, seam and fields.</summary>
	public static ModGuideCard EventCard(ModApiEvent e)
	{
		var body = new System.Text.StringBuilder();
		body.Append(e.Summary).Append("\n\n");
		body.Append("Native: refii::mods::").Append(e.Name).Append("Event — REFII_MOD_ON(").Append(e.Name).Append(", fn)\n");
		body.Append("Lua: Refii.on(\"").Append(e.LuaName).Append("\", function(e) ... end)\n");
		body.Append("Cancel: ").Append(e.Cancellable ? e.Cancel : "not cancellable").Append('\n');
		if (e.IsRelayed)
		{
			body.Append("Co-op: ").Append(e.Relayed).Append('\n');
		}

		body.Append("Seam: ").Append(e.Seam).Append("\n\nFields:\n");
		foreach (ModApiField f in e.Fields)
		{
			body.Append("  • ").Append(f.Name).Append(" (").Append(f.Type).Append(f.Writable ? ", writable" : string.Empty)
				.Append(") — ").Append(f.Meaning).Append('\n');
		}

		body.Append("  • ctx, base — the guest context (a copy) and memory (native only); cancel — see above");
		return e.Cancellable
			? new ModGuideCard($"{e.Kind}. {e.Name}  ·  \"{e.LuaName}\"", 0.95f, 0.70f, 0.45f, body.ToString())
			: new ModGuideCard($"{e.Kind}. {e.Name}  ·  \"{e.LuaName}\"", 0.55f, 0.80f, 0.95f, body.ToString());
	}
}
