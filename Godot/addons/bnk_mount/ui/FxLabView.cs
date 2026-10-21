#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.EditorTools;
using Refii.Fx;

namespace Refii.Assets.UI;

/// <summary>
/// THE FX LAB — the active mod's effects edited against a live preview, inside the Mod Studio hub.
/// Two editors share one turntable:
/// <list type="bullet">
/// <item>RECIPES (<c>fx/recipes.json</c>): every field of every recipe — when/equals, with,
/// base/anchor/entities, everyNode, tint, intensity, size, the light, the attached effects and the
/// authored pieces — previewed on the base effect dressed exactly as the game dresses it
/// (<see cref="Engine_FxRecipes.DressPreview"/>).</item>
/// <item>EFFECTS (<c>fx/effects/*.json</c>, <see cref="FxLabEffectEditor"/>): a bank effect cloned
/// into the mod or overridden, its emitters and visuals edited, previewed through
/// <see cref="FableFx"/> on a preview bank (<see cref="Engine_FxModEffects.PreviewBank"/>).</item>
/// </list>
/// Save writes through <see cref="RefiiModApi"/> (validated, re-mounted); in a running game
/// <c>refii_action fx-recipes-reload</c> re-reads the recipes.
/// </summary>
public partial class FxLabView : MarginContainer
{
	private readonly RefiiModApi _api = new();
	private readonly List<Engine_FxRecipes.Recipe> _all = new();
	private int _selected = -1;
	private bool _filling;

	private TabContainer _tabs = null!;
	private FxLabEffectEditor _effects = null!;

	private ItemList _list = null!;
	private LineEdit _id = null!, _when = null!, _baseNames = null!, _anchor = null!, _entities = null!, _with = null!;
	private SpinBox _equals = null!, _withRadius = null!;
	private CheckBox _everyNode = null!;
	private OptionButton _base = null!;
	private SpinBox _tintR = null!, _tintG = null!, _tintB = null!, _intensity = null!, _size = null!;
	private CheckBox _lightOn = null!;
	private ColorPickerButton _lightColour = null!;
	private SpinBox _lightEnergy = null!, _lightRange = null!, _lightHeight = null!, _lightFlicker = null!;
	private VBoxContainer _attachRows = null!;
	private VBoxContainer _pieceRows = null!;
	private Label _reference = null!;
	private Label _status = null!;
	private CheckBox _spin = null!;
	private HSlider _distance = null!;

	private Node3D _rig = null!;
	private Camera3D _camera = null!;
	private FableFx? _effect;
	private float _yaw;

	/// <summary>What Fable 3 made of each pair, as the target to aim the look at.</summary>
	private static readonly Dictionary<string, string> Reference = new(StringComparer.Ordinal)
	{
		["spell_weaving.vortex_blades"] = "Fable 3 Blades + Vortex: a windstorm lifts enemies while blades impale them.",
		["spell_weaving.vortex_fire"] = "Fable 3 Fireball + Vortex: a fiery tornado that lifts enemies and sets them alight.",
		["spell_weaving.vortex_shock"] = "Fable 3 Shock + Vortex: an electrical tornado.",
		["spell_weaving.vortex_push"] = "Fable 3 Vortex + Force Push: a crowd-control vortex with a knockback.",
		["spell_weaving.vortex_chaos"] = "No Fable 3 pair (Chaos stands where Ice Storm was): chaos running through the vortex.",
		["spell_weaving.blades_fire"] = "Fable 3 Fireball + Blades: flaming swords that impale and burn.",
		["spell_weaving.blades_shock"] = "Fable 3 Shock + Blades: blades that hit and stun.",
		["spell_weaving.blades_push"] = "Fable 3 Force Push + Blades: blades with a forceful shockwave.",
		["spell_weaving.blades_chaos"] = "No Fable 3 pair: chaotic blades.",
		["spell_weaving.fire_shock"] = "Fable 3 Fireball + Shock: a fire projectile wrapped in lightning; shocks, stuns and burns.",
		["spell_weaving.fire_push"] = "Fable 3 Fireball + Force Push: a fireball that pushes enemies backward.",
		["spell_weaving.fire_chaos"] = "No Fable 3 pair: chaotic fire.",
		["spell_weaving.shock_push"] = "Fable 3 Shock + Force Push: a stun blast that pushes enemies back.",
		["spell_weaving.shock_chaos"] = "No Fable 3 pair: chaotic shock.",
		["spell_weaving.push_chaos"] = "No Fable 3 pair: a chaotic push.",
	};

	/// <summary>Engine_FxRecipePieces.Build's kinds and Billboard's blends.</summary>
	private static readonly string[] PieceKinds = { "embers", "arcs", "ring", "blades" };
	private static readonly string[] PieceBlends = { "add", "mix" };

	/// <summary>A piece's numbers: (key, label, min, max, step).</summary>
	private static readonly (string Key, string Label, double Min, double Max, double Step)[] PieceNumbers =
	{
		("height", "height", 0, 20, 0.05), ("radius", "radius", 0, 20, 0.05), ("radiusTop", "radius top", 0, 20, 0.05),
		("rate", "rate", 0, 500, 1), ("speed", "speed", -20, 20, 0.05), ("size", "size", 0, 10, 0.01),
		("sizeEnd", "size end", 0, 10, 0.01), ("count", "count", 1, 1024, 1), ("period", "period", 0.01, 30, 0.01),
		("width", "width", 0, 5, 0.01), ("tilt", "tilt", -90, 90, 1), ("intensity", "intensity", 0, 10, 0.05),
	};

	public override void _Ready()
	{
		AddThemeConstantOverride("margin_left", 8);
		AddThemeConstantOverride("margin_right", 8);
		AddThemeConstantOverride("margin_top", 8);
		AddThemeConstantOverride("margin_bottom", 8);

		var split = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild(split);

		var column = new VBoxContainer { CustomMinimumSize = new Vector2(480, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
		split.AddChild(column);
		_tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		column.AddChild(_tabs);
		Control recipes = BuildRecipeEditor();
		recipes.Name = "Recipes";
		_tabs.AddChild(recipes);
		_effects = new FxLabEffectEditor(this, _api) { Name = "Effects" };
		_tabs.AddChild(_effects);

		var buttons = new HBoxContainer();
		column.AddChild(buttons);
		_spin = new CheckBox { Text = "Turntable", ButtonPressed = true };
		buttons.AddChild(_spin);
		buttons.AddChild(new Label { Text = "Distance" });
		_distance = new HSlider { MinValue = 2, MaxValue = 30, Step = 0.5, Value = 6, CustomMinimumSize = new Vector2(140, 0) };
		buttons.AddChild(_distance);
		_status = new Label { AutowrapMode = TextServer.AutowrapMode.Word };
		column.AddChild(_status);

		split.AddChild(BuildPreview());
		LoadFromMod();
	}

	public override void _ExitTree() => ClearPreview();

	public override void _Process(double delta)
	{
		if (_camera is null || !GodotObject.IsInstanceValid(_camera))
		{
			return;
		}

		if (_spin.ButtonPressed)
		{
			_yaw += (float)delta * 0.4f;
		}

		float distance = (float)_distance.Value;
		_camera.Position = new Vector3(Mathf.Sin(_yaw) * distance, 2.2f, Mathf.Cos(_yaw) * distance);
		_camera.LookAt(new Vector3(0f, 1f, 0f), Vector3.Up);
	}

	/// <summary>Shows <paramref name="effect"/> (a bank or mod effect) in the effect editor, switching to it.</summary>
	public void OpenEffect(string effect)
	{
		_tabs.CurrentTab = _effects.GetIndex();
		_effects.Open(effect);
	}

	internal void SetStatus(string text) => _status.Text = text;

	// ---------------------------------------------------------------------------------------
	// The preview
	// ---------------------------------------------------------------------------------------

	private Control BuildPreview()
	{
		var container = new SubViewportContainer { Stretch = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		var viewport = new SubViewport { OwnWorld3D = true, HandleInputLocally = false, Msaa3D = Viewport.Msaa.Msaa2X };
		container.AddChild(viewport);

		_rig = new Node3D { Name = "FxLabRig" };
		viewport.AddChild(_rig);

		var environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = new Color(0.07f, 0.07f, 0.09f),
			AmbientLightSource = Godot.Environment.AmbientSource.Color,
			AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
			AmbientLightEnergy = 0.6f,
			GlowEnabled = true,
			GlowIntensity = 0.6f,
		};
		_rig.AddChild(new WorldEnvironment { Environment = environment });

		var sun = new DirectionalLight3D { LightEnergy = 0.8f };
		sun.RotationDegrees = new Vector3(-50f, 30f, 0f);
		_rig.AddChild(sun);

		var floor = new MeshInstance3D
		{
			Mesh = new PlaneMesh { Size = new Vector2(12f, 12f) },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.15f, 0.13f), Roughness = 1f },
		};
		_rig.AddChild(floor);

		_camera = new Camera3D { Current = true, Fov = 55f };
		_rig.AddChild(_camera);
		return container;
	}

	internal void ClearPreview()
	{
		if (_effect is not null && GodotObject.IsInstanceValid(_effect))
		{
			Engine_FxRecipes.Undress(_effect);
			_effect.QueueFree();
		}

		_effect = null;
	}

	/// <summary>A fresh FableFx on the turntable: the game's node, from the shared bank or <paramref name="bank"/>.</summary>
	internal FableFx? ShowEffect(string name, FxBank? bank = null)
	{
		ClearPreview();
		if (FableFx.SharedBank() is null)
		{
			_status.Text = "The FX bank is not mounted; open the game data first.";
			return null;
		}

		_effect = new FableFx { Name = "LabEffect", EffectName = name, Loop = true, Playing = true, Position = new Vector3(0f, 0.5f, 0f) };
		if (bank is not null)
		{
			_effect.BankOverride = bank;
		}

		_rig.AddChild(_effect);
		return _effect;
	}

	// ---------------------------------------------------------------------------------------
	// Shared widgets
	// ---------------------------------------------------------------------------------------

	internal static SpinBox Spin(Container parent, double min, double max, double step, double value = 0, string tip = "")
	{
		var spin = new SpinBox
		{
			MinValue = min, MaxValue = max, Step = step, Value = value, TooltipText = tip,
			AllowGreater = true, AllowLesser = true, CustomMinimumSize = new Vector2(78, 0),
		};
		parent.AddChild(spin);
		return spin;
	}

	/// <summary>A "label value" pair in a flow, the label small, for rows of many numbers.</summary>
	internal static T Labeled<T>(Container flow, string label, T control) where T : Control
	{
		var pair = new HBoxContainer();
		pair.AddThemeConstantOverride("separation", 3);
		var text = new Label { Text = label };
		text.AddThemeFontSizeOverride("font_size", 12);
		text.Modulate = new Color(0.78f, 0.82f, 0.9f);
		pair.AddChild(text);
		pair.AddChild(control);
		flow.AddChild(pair);
		return control;
	}

	internal static Label Heading(Container parent, string text)
	{
		var label = new Label { Text = text };
		label.AddThemeColorOverride("font_color", new Color(0.55f, 0.84f, 1.0f));
		parent.AddChild(label);
		return label;
	}

	internal static List<string> CommaList(string text) =>
		text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

	// ---------------------------------------------------------------------------------------
	// The recipe editor
	// ---------------------------------------------------------------------------------------

	private Control BuildRecipeEditor()
	{
		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
		var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(column);

		var head = new HBoxContainer();
		column.AddChild(head);
		head.AddChild(new Label { Text = "Recipes of the active mod (fx/recipes.json)", SizeFlagsHorizontal = SizeFlags.ExpandFill });
		var reload = new Button { Text = "↺ Reload", TooltipText = "Read fx/recipes.json from the active mod again" };
		reload.Pressed += LoadFromMod;
		head.AddChild(reload);
		var create = new Button { Text = "+ New recipe", TooltipText = "A recipe of the active mod, switched on by a mod value" };
		create.Pressed += NewRecipe;
		head.AddChild(create);
		var delete = new Button { Text = "Delete", TooltipText = "Remove the selected recipe (saved on Save to mod)" };
		ModStudioUiStyles.ApplyRedButtonStyle(delete);
		delete.Pressed += DeleteRecipe;
		head.AddChild(delete);

		_list = new ItemList { CustomMinimumSize = new Vector2(0, 130) };
		_list.ItemSelected += index => Select((int)index);
		column.AddChild(_list);

		_reference = new Label { AutowrapMode = TextServer.AutowrapMode.Word };
		_reference.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.45f));
		column.AddChild(_reference);

		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride("h_separation", 8);
		column.AddChild(grid);

		LineEdit Text(string label, string tip)
		{
			grid.AddChild(new Label { Text = label });
			var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = tip };
			edit.TextChanged += _ => Changed();
			grid.AddChild(edit);
			return edit;
		}

		SpinBox Number(string label, double min, double max, double step, string tip)
		{
			grid.AddChild(new Label { Text = label });
			var row = new HBoxContainer();
			SpinBox spin = Spin(row, min, max, step, 0, tip);
			spin.ValueChanged += _ => Changed();
			grid.AddChild(row);
			return spin;
		}

		_id = Text("Id", "the recipe's id: <mod>.<name>; a later mod's recipe with the same id replaces it");
		_when = Text("When (mod values)", "comma-separated mod values (mod:<mod>.<name>); the recipe holds while any equals the number below");
		_equals = Number("Equals", -1e6, 1e6, 0.01, "the value the 'when' keys must equal");
		_with = Text("With (effect)", "an effect the mod plays per cast (play_fx_on): the recipe holds where it plays; empty for none");
		_withRadius = Number("With radius", 0, 1000, 0.1, "a base effect binds to a play of that effect riding its entity, or within this many metres");
		_baseNames = Text("Base effects", "comma-separated effect names the recipe dresses (.pfx names or FX_XXXXXXXX)");
		grid.AddChild(new Label { Text = "Base effect shown" });
		_base = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_base.ItemSelected += _ => Preview();
		grid.AddChild(_base);
		_anchor = Text("Anchors", "comma-separated base effects that may carry the pieces and the light; empty: the first live one");
		_entities = Text("Entities", "comma-separated entity definitions (names or #hashes): every live one carries the pieces");
		grid.AddChild(new Label { Text = "Every node" });
		_everyNode = new CheckBox { Text = "dress every live anchor, not one per recipe" };
		_everyNode.Toggled += _ => Changed();
		grid.AddChild(_everyNode);

		grid.AddChild(new Label { Text = "Tint R / G / B" });
		var tint = new HBoxContainer();
		_tintR = Spin(tint, 0, 4, 0.05);
		_tintG = Spin(tint, 0, 4, 0.05);
		_tintB = Spin(tint, 0, 4, 0.05);
		grid.AddChild(tint);
		_intensity = Number("Intensity", 0, 4, 0.05, "a multiplier on the dressed effect's intensity");
		_size = Number("Size", 0.05, 5, 0.05, "a multiplier on the dressed effect's size");

		Heading(column, "Light");
		var light = new HFlowContainer();
		column.AddChild(light);
		_lightOn = new CheckBox { Text = "a light in the overlay's colour" };
		_lightOn.Toggled += _ => Changed();
		light.AddChild(_lightOn);
		_lightColour = Labeled(light, "colour", new ColorPickerButton { CustomMinimumSize = new Vector2(48, 24), EditAlpha = false });
		_lightColour.ColorChanged += _ => Changed();
		_lightEnergy = Labeled(light, "energy", NewSpin(0, 64, 0.1));
		_lightRange = Labeled(light, "range", NewSpin(0, 100, 0.1));
		_lightHeight = Labeled(light, "height", NewSpin(-10, 20, 0.05));
		_lightFlicker = Labeled(light, "flicker", NewSpin(0, 1, 0.01));
		foreach (SpinBox spin in new[] { _tintR, _tintG, _tintB, _lightEnergy, _lightRange, _lightHeight, _lightFlicker })
		{
			spin.ValueChanged += _ => Changed();
		}

		var attachHead = new HBoxContainer();
		column.AddChild(attachHead);
		Heading(attachHead, "Attached effects").SizeFlagsHorizontal = SizeFlags.ExpandFill;
		var add = new Button { Text = "+ Attach" };
		add.Pressed += () =>
		{
			AddAttachRow(new Engine_FxRecipes.Attachment("fxspl_blast_fire_ring", 1f, 1, 0f, 0f, 0f, 1f, 1, 0.6f, 0f));
			Changed();
		};
		attachHead.AddChild(add);
		_attachRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		column.AddChild(_attachRows);

		var pieceHead = new HBoxContainer();
		column.AddChild(pieceHead);
		Heading(pieceHead, "Pieces (authored looks)").SizeFlagsHorizontal = SizeFlags.ExpandFill;
		var addPiece = new Button { Text = "+ Piece" };
		addPiece.Pressed += () =>
		{
			AddPieceRow(new Engine_FxRecipePieces.Piece(
				"embers", string.Empty, Colors.Orange, Colors.Orange with { A = 0f }, 2f, 1f, 1f, 10f, 1f, 0.5f, 0.2f, 64, 1f, 0.1f,
				string.Empty, 20f, 1f));
			Changed();
		};
		pieceHead.AddChild(addPiece);
		_pieceRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		column.AddChild(_pieceRows);

		var save = new Button { Text = "💾 Save recipes to mod", TooltipText = "Write fx/recipes.json into the active mod package" };
		save.Pressed += Save;
		column.AddChild(save);
		return scroll;
	}

	internal static SpinBox NewSpin(double min, double max, double step) =>
		new() { MinValue = min, MaxValue = max, Step = step, AllowGreater = true, AllowLesser = true, CustomMinimumSize = new Vector2(70, 0) };

	private void LoadFromMod()
	{
		string mod = _api.GetActiveMod();
		_all.Clear();
		_list.Clear();
		_selected = -1;
		if (string.IsNullOrEmpty(mod))
		{
			_status.Text = "No active mod. Pick one in the Mod Studio first.";
			return;
		}

		string text = _api.ReadText(Engine_FxRecipes.RecipesPath);
		if (text.Length != 0)
		{
			_all.AddRange(Engine_FxRecipes.Parse(mod, text));
		}

		foreach (Engine_FxRecipes.Recipe recipe in _all)
		{
			_list.AddItem(recipe.Id);
		}

		_status.Text = text.Length == 0
			? $"'{mod}' has no {Engine_FxRecipes.RecipesPath} yet: + New recipe starts one."
			: $"{_all.Count} recipe(s) from '{mod}'.";
		if (_all.Count != 0)
		{
			_list.Select(0);
			Select(0);
		}

		_effects.Reload();
	}

	private void NewRecipe()
	{
		string mod = _api.GetActiveMod();
		if (mod.Length == 0)
		{
			_status.Text = "No active mod. Pick one in the Mod Studio first.";
			return;
		}

		int n = 1;
		while (_all.Any(r => r.Id == $"{mod}.look{n}"))
		{
			n++;
		}

		_all.Add(Engine_FxRecipes.Make(
			$"{mod}.look{n}", mod, new[] { $"mod:{mod}.look" }, n, new[] { "fxspl_blast_fire_ring" }, Colors.White, 1f, 1f,
			Array.Empty<Engine_FxRecipes.Attachment>()));
		_list.AddItem(_all[^1].Id);
		_list.Select(_all.Count - 1);
		Select(_all.Count - 1);
		_status.Text = $"New recipe {_all[^1].Id}: it holds while mod:{mod}.look equals {n}. Save to keep it.";
	}

	private void DeleteRecipe()
	{
		if (_selected < 0 || _selected >= _all.Count)
		{
			return;
		}

		string id = _all[_selected].Id;
		_all.RemoveAt(_selected);
		_list.RemoveItem(_selected);
		_selected = -1;
		ClearPreview();
		_status.Text = $"Removed {id}; Save to write the file without it.";
		if (_all.Count != 0)
		{
			_list.Select(0);
			Select(0);
		}
	}

	private void Select(int index)
	{
		if (index < 0 || index >= _all.Count)
		{
			return;
		}

		_selected = index;
		Engine_FxRecipes.Recipe recipe = _all[index];
		_filling = true;
		_reference.Text = Reference.TryGetValue(recipe.Id, out string? text) ? text : string.Empty;
		_id.Text = recipe.Id;
		_when.Text = string.Join(", ", recipe.When);
		_equals.Value = recipe.Match;
		_with.Text = recipe.With;
		_withRadius.Value = recipe.WithRadius;
		_baseNames.Text = string.Join(", ", recipe.BaseNames);
		FillBaseChoices(recipe.BaseNames);
		_anchor.Text = string.Join(", ", recipe.AnchorNames);
		_entities.Text = recipe.EntityNames is { } entities ? string.Join(", ", entities) : string.Empty;
		_everyNode.ButtonPressed = recipe.EveryNode;
		_tintR.Value = recipe.Tint.R;
		_tintG.Value = recipe.Tint.G;
		_tintB.Value = recipe.Tint.B;
		_intensity.Value = recipe.Intensity;
		_size.Value = recipe.Size;
		Engine_FxRecipes.Light glow = recipe.Glow ?? new Engine_FxRecipes.Light(Colors.White, 4f, 6f, 1f, 0f);
		_lightOn.ButtonPressed = recipe.Glow is not null;
		_lightColour.Color = glow.Colour;
		_lightEnergy.Value = glow.Energy;
		_lightRange.Value = glow.Range;
		_lightHeight.Value = glow.Height;
		_lightFlicker.Value = glow.Flicker;
		foreach (Node child in _attachRows.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Engine_FxRecipes.Attachment attachment in recipe.Attach)
		{
			AddAttachRow(attachment);
		}

		foreach (Node child in _pieceRows.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Engine_FxRecipePieces.Piece piece in recipe.Pieces ?? Array.Empty<Engine_FxRecipePieces.Piece>())
		{
			AddPieceRow(piece);
		}

		_filling = false;
		Preview();
	}

	private void FillBaseChoices(IReadOnlyList<string> names)
	{
		string was = _base.Selected >= 0 && _base.Selected < _base.ItemCount ? _base.GetItemText(_base.Selected) : string.Empty;
		_base.Clear();
		foreach (string name in names)
		{
			_base.AddItem(name);
		}

		int keep = names.ToList().IndexOf(was);
		if (_base.ItemCount != 0)
		{
			_base.Select(keep >= 0 ? keep : 0);
		}
	}

	private void AddAttachRow(Engine_FxRecipes.Attachment attachment)
	{
		var box = new VBoxContainer { Name = "Attach" };
		var head = new HBoxContainer();
		box.AddChild(head);
		var effect = new LineEdit { Text = attachment.Effect, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "effect name" };
		effect.TextChanged += _ => Changed();
		head.AddChild(effect);
		var tinted = new CheckBox { Text = "tint", ButtonPressed = attachment.Tint is not null, TooltipText = "colour this attachment's particles" };
		tinted.Toggled += _ => Changed();
		head.AddChild(tinted);
		var colour = new ColorPickerButton { Color = attachment.Tint ?? Colors.White, CustomMinimumSize = new Vector2(40, 24), EditAlpha = false };
		colour.ColorChanged += _ => Changed();
		head.AddChild(colour);
		var remove = new Button { Text = "✕" };
		ModStudioUiStyles.ApplyRedButtonStyle(remove);
		remove.Pressed += () =>
		{
			box.QueueFree();
			CallDeferred(nameof(Changed));
		};
		head.AddChild(remove);

		var flow = new HFlowContainer();
		box.AddChild(flow);
		var spins = new Dictionary<string, SpinBox>();
		foreach ((string key, string label, double value, double min, double max, double step) in new (string, string, double, double, double, double)[]
		{
			("count", "count", attachment.Count, 1, 64, 1),
			("radius", "radius", attachment.Radius, 0, 20, 0.05),
			("height", "height", attachment.Height, -5, 20, 0.05),
			("turnsPerSecond", "turns/s", attachment.TurnsPerSecond, -8, 8, 0.05),
			("scale", "scale", attachment.Scale, 0.05, 10, 0.05),
			("intensity", "intensity", attachment.Intensity, 0, 10, 0.05),
			("stack", "stack", attachment.Stack, 1, 32, 1),
			("stackStep", "stack step", attachment.StackStep, 0, 5, 0.05),
			("stagger", "stagger s", attachment.Stagger, 0, 10, 0.05),
			("radiusStep", "radius/level", attachment.RadiusStep, -2, 4, 0.02),
			("scaleStep", "scale/level", attachment.ScaleStep, 0.2, 2, 0.01),
			("fadeIn", "fade in s", attachment.FadeIn, 0, 5, 0.05),
		})
		{
			SpinBox spin = Labeled(flow, label, NewSpin(min, max, step));
			spin.Value = value;
			spin.ValueChanged += _ => Changed();
			spins[key] = spin;
		}

		_attachRows.AddChild(box);
		_attachEditors[box] = (effect, tinted, colour, spins);
	}

	private readonly Dictionary<VBoxContainer, (LineEdit Effect, CheckBox Tinted, ColorPickerButton Colour, Dictionary<string, SpinBox> Spins)> _attachEditors = new();

	private readonly Dictionary<VBoxContainer, (OptionButton Kind, OptionButton Blend, LineEdit Texture, LineEdit Mesh, ColorPickerButton Colour, ColorPickerButton ColourEnd, Dictionary<string, SpinBox> Spins)> _pieceEditors = new();

	private void AddPieceRow(Engine_FxRecipePieces.Piece piece)
	{
		var box = new VBoxContainer { Name = "Piece" };
		var head = new HFlowContainer();
		box.AddChild(head);
		var kind = Labeled(head, "kind", new OptionButton());
		foreach (string k in PieceKinds)
		{
			kind.AddItem(k);
		}

		kind.Select(Math.Max(0, Array.IndexOf(PieceKinds, piece.Kind)));
		kind.ItemSelected += _ => Changed();
		var blend = Labeled(head, "blend", new OptionButton());
		foreach (string b in PieceBlends)
		{
			blend.AddItem(b);
		}

		blend.Select(Math.Max(0, Array.IndexOf(PieceBlends, piece.Blend)));
		blend.ItemSelected += _ => Changed();
		var colour = Labeled(head, "colour", new ColorPickerButton { Color = piece.Colour, CustomMinimumSize = new Vector2(40, 24) });
		colour.ColorChanged += _ => Changed();
		var colourEnd = Labeled(head, "to", new ColorPickerButton { Color = piece.ColourEnd, CustomMinimumSize = new Vector2(40, 24) });
		colourEnd.ColorChanged += _ => Changed();
		var remove = new Button { Text = "✕" };
		ModStudioUiStyles.ApplyRedButtonStyle(remove);
		remove.Pressed += () =>
		{
			box.QueueFree();
			CallDeferred(nameof(Changed));
		};
		head.AddChild(remove);

		var paths = new HBoxContainer();
		box.AddChild(paths);
		var texture = new LineEdit { Text = piece.Texture, PlaceholderText = "texture (art/fx/pfx_textures leaf)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		texture.TextChanged += _ => Changed();
		paths.AddChild(texture);
		var mesh = new LineEdit { Text = piece.Mesh, PlaceholderText = "mesh (blades)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		mesh.TextChanged += _ => Changed();
		paths.AddChild(mesh);

		var flow = new HFlowContainer();
		box.AddChild(flow);
		var values = new Dictionary<string, double>
		{
			["height"] = piece.Height, ["radius"] = piece.Radius, ["radiusTop"] = piece.RadiusTop, ["rate"] = piece.Rate,
			["speed"] = piece.Speed, ["size"] = piece.Size, ["sizeEnd"] = piece.SizeEnd, ["count"] = piece.Count,
			["period"] = piece.Period, ["width"] = piece.Width, ["tilt"] = piece.Tilt, ["intensity"] = piece.Intensity,
		};
		var spins = new Dictionary<string, SpinBox>();
		foreach ((string key, string label, double min, double max, double step) in PieceNumbers)
		{
			SpinBox spin = Labeled(flow, label, NewSpin(min, max, step));
			spin.Value = values[key];
			spin.ValueChanged += _ => Changed();
			spins[key] = spin;
		}

		box.AddChild(new HSeparator());
		_pieceRows.AddChild(box);
		_pieceEditors[box] = (kind, blend, texture, mesh, colour, colourEnd, spins);
	}

	/// <summary>The recipe as the fields state it now.</summary>
	private Engine_FxRecipes.Recipe? Current()
	{
		if (_selected < 0 || _selected >= _all.Count)
		{
			return null;
		}

		Engine_FxRecipes.Recipe was = _all[_selected];
		var attach = new List<Engine_FxRecipes.Attachment>();
		foreach (Node child in _attachRows.GetChildren())
		{
			if (child is not VBoxContainer box || box.IsQueuedForDeletion() || !_attachEditors.TryGetValue(box, out var row))
			{
				continue;
			}

			string effect = row.Effect.Text.Trim();
			if (effect.Length == 0)
			{
				continue;
			}

			float V(string key) => (float)row.Spins[key].Value;
			attach.Add(new Engine_FxRecipes.Attachment(
				effect, V("scale"), (int)V("count"), V("radius"), V("height"), V("turnsPerSecond"), V("intensity"),
				(int)V("stack"), V("stackStep"), V("stagger"), row.Tinted.ButtonPressed ? row.Colour.Color : null,
				V("radiusStep"), V("scaleStep"), V("fadeIn")));
		}

		var pieces = new List<Engine_FxRecipePieces.Piece>();
		foreach (Node child in _pieceRows.GetChildren())
		{
			if (child is not VBoxContainer box || box.IsQueuedForDeletion() || !_pieceEditors.TryGetValue(box, out var row))
			{
				continue;
			}

			float V(string key) => (float)row.Spins[key].Value;
			pieces.Add(new Engine_FxRecipePieces.Piece(
				PieceKinds[Math.Max(0, row.Kind.Selected)], row.Texture.Text.Trim(), row.Colour.Color, row.ColourEnd.Color,
				V("height"), V("radius"), V("radiusTop"), V("rate"), V("speed"), V("size"), V("sizeEnd"), (int)V("count"),
				V("period"), V("width"), row.Mesh.Text.Trim(), V("tilt"), V("intensity"), PieceBlends[Math.Max(0, row.Blend.Selected)]));
		}

		Engine_FxRecipes.Light? glow = _lightOn.ButtonPressed
			? new Engine_FxRecipes.Light(_lightColour.Color, (float)_lightEnergy.Value, (float)_lightRange.Value, (float)_lightHeight.Value, (float)_lightFlicker.Value)
			: null;
		string id = _id.Text.Trim().Length != 0 ? _id.Text.Trim() : was.Id;
		List<string> baseNames = CommaList(_baseNames.Text);
		return Engine_FxRecipes.Make(
			id, was.Mod, CommaList(_when.Text), (float)_equals.Value, baseNames,
			new Color((float)_tintR.Value, (float)_tintG.Value, (float)_tintB.Value),
			(float)_intensity.Value, (float)_size.Value, attach, CommaList(_anchor.Text), glow, pieces, _everyNode.ButtonPressed,
			CommaList(_entities.Text), _with.Text.Trim(), (float)_withRadius.Value);
	}

	private void Changed()
	{
		if (_filling || Current() is not { } recipe)
		{
			return;
		}

		_all[_selected] = recipe;
		_list.SetItemText(_selected, recipe.Id);
		_filling = true;
		FillBaseChoices(recipe.BaseNames);
		_filling = false;
		Preview();
	}

	private void Preview()
	{
		if (_filling || Current() is not { } recipe)
		{
			return;
		}

		if (_base.Selected < 0 || _base.Selected >= recipe.BaseNames.Count)
		{
			ClearPreview();
			return;
		}

		string name = recipe.BaseNames[_base.Selected];
		if (ShowEffect(name) is not { } node)
		{
			return;
		}

		Engine_FxRecipes.DressPreview(node, recipe);
		_status.Text = $"{recipe.Id}: {name} dressed with {recipe.Attach.Count} attachment(s), {recipe.Pieces?.Count ?? 0} piece(s).";
	}

	private void Save()
	{
		if (Current() is { } recipe)
		{
			_all[_selected] = recipe;
		}

		var problems = new List<string>();
		foreach (Engine_FxRecipes.Recipe r in _all)
		{
			if (r.Id.Length == 0 || r.BaseNames.Count == 0 || (r.When.Count == 0 && r.With.Length == 0))
			{
				problems.Add(r.Id.Length != 0 ? r.Id : "(no id)");
			}
		}

		if (problems.Count != 0)
		{
			_status.Text = $"Not saved: {string.Join(", ", problems)} need an id, base effects and when (or with).";
			return;
		}

		string error = _api.WriteText(Engine_FxRecipes.RecipesPath, Engine_FxRecipes.Serialize(_all));
		_status.Text = error.Length != 0
			? error
			: $"Saved {_all.Count} recipe(s) to '{_api.GetActiveMod()}'. In a running game: refii_action fx-recipes-reload.";
	}
}
#endif
