#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.EditorTools;
using Refii.Fx;

namespace Refii.Assets.UI;

/// <summary>
/// THE FX LAB — the active mod's <c>fx/recipes.json</c> edited against a live preview. Pick a
/// recipe, pick one of its base effects, turn the tint, brightness, size and the attached
/// effects, and watch the base effect dressed exactly as the game dresses it
/// (<see cref="Engine_FxRecipes.DressPreview"/>): the same node, the same code path, the same
/// bank. Save writes the file back into the mod package; in a running game
/// <c>refii_action fx-recipes-reload</c> picks it up without a restart.
/// </summary>
public partial class FxLabView : MarginContainer
{
	private readonly RefiiModApi _api = new();
	private readonly List<Engine_FxRecipes.Recipe> _all = new();
	private int _selected = -1;
	private bool _filling;

	private ItemList _list = null!;
	private OptionButton _base = null!;
	private SpinBox _tintR = null!, _tintG = null!, _tintB = null!, _intensity = null!, _size = null!;
	private VBoxContainer _attachRows = null!;
	private Label _reference = null!;
	private Label _status = null!;
	private CheckBox _spin = null!;
	private HSlider _distance = null!;

	private SubViewport _viewport = null!;
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

	public override void _Ready()
	{
		AddThemeConstantOverride("margin_left", 8);
		AddThemeConstantOverride("margin_right", 8);
		AddThemeConstantOverride("margin_top", 8);
		AddThemeConstantOverride("margin_bottom", 8);

		var split = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild(split);
		split.AddChild(BuildEditor());
		split.AddChild(BuildPreview());
		LoadFromMod();
	}

	public override void _ExitTree()
	{
		if (_effect is not null && GodotObject.IsInstanceValid(_effect))
		{
			Engine_FxRecipes.Undress(_effect);
		}
	}

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

	private Control BuildEditor()
	{
		var column = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
		column.AddThemeConstantOverride("separation", 6);

		var head = new HBoxContainer();
		column.AddChild(head);
		var title = new Label { Text = "FX Lab — recipes of the active mod", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		head.AddChild(title);
		var reload = new Button { Text = "↺ Reload", TooltipText = "Read fx/recipes.json from the active mod again" };
		reload.Pressed += LoadFromMod;
		head.AddChild(reload);

		_list = new ItemList { CustomMinimumSize = new Vector2(0, 200), SizeFlagsVertical = SizeFlags.ExpandFill };
		_list.ItemSelected += index => Select((int)index);
		column.AddChild(_list);

		_reference = new Label { AutowrapMode = TextServer.AutowrapMode.Word, CustomMinimumSize = new Vector2(0, 40) };
		_reference.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.45f));
		column.AddChild(_reference);

		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride("h_separation", 8);
		column.AddChild(grid);

		grid.AddChild(new Label { Text = "Base effect shown" });
		_base = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_base.ItemSelected += _ => Preview();
		grid.AddChild(_base);

		grid.AddChild(new Label { Text = "Tint R / G / B" });
		var tint = new HBoxContainer();
		_tintR = Spin(tint, 0, 4, 0.05);
		_tintG = Spin(tint, 0, 4, 0.05);
		_tintB = Spin(tint, 0, 4, 0.05);
		grid.AddChild(tint);

		grid.AddChild(new Label { Text = "Intensity" });
		var intensityRow = new HBoxContainer();
		_intensity = Spin(intensityRow, 0, 4, 0.05);
		grid.AddChild(intensityRow);

		grid.AddChild(new Label { Text = "Size" });
		var sizeRow = new HBoxContainer();
		_size = Spin(sizeRow, 0.2, 3, 0.05);
		grid.AddChild(sizeRow);

		var attachHead = new HBoxContainer();
		column.AddChild(attachHead);
		attachHead.AddChild(new Label { Text = "Attached effects", SizeFlagsHorizontal = SizeFlags.ExpandFill });
		var add = new Button { Text = "+ Attach" };
		add.Pressed += () =>
		{
			AddAttachRow(new Engine_FxRecipes.Attachment("fxspl_blast_fire_ring", 1f, 1, 0f, 0f, 0f, 1f, 1, 0.6f, 0f));
			Changed();
		};
		attachHead.AddChild(add);

		var attachScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 180), SizeFlagsVertical = SizeFlags.ExpandFill };
		column.AddChild(attachScroll);
		_attachRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		attachScroll.AddChild(_attachRows);

		var buttons = new HBoxContainer();
		column.AddChild(buttons);
		var save = new Button { Text = "💾 Save to mod", TooltipText = "Write fx/recipes.json into the active mod package" };
		save.Pressed += Save;
		buttons.AddChild(save);
		_spin = new CheckBox { Text = "Turntable", ButtonPressed = true };
		buttons.AddChild(_spin);
		buttons.AddChild(new Label { Text = "Distance" });
		_distance = new HSlider { MinValue = 2, MaxValue = 14, Step = 0.5, Value = 6, CustomMinimumSize = new Vector2(120, 0) };
		buttons.AddChild(_distance);

		_status = new Label { AutowrapMode = TextServer.AutowrapMode.Word };
		column.AddChild(_status);
		return column;
	}

	private Control BuildPreview()
	{
		var container = new SubViewportContainer { Stretch = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		_viewport = new SubViewport { OwnWorld3D = true, HandleInputLocally = false, Msaa3D = Viewport.Msaa.Msaa2X };
		container.AddChild(_viewport);

		_rig = new Node3D { Name = "FxLabRig" };
		_viewport.AddChild(_rig);

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

	private static SpinBox Spin(Container parent, double min, double max, double step)
	{
		var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, CustomMinimumSize = new Vector2(80, 0) };
		parent.AddChild(spin);
		return spin;
	}

	private void LoadFromMod()
	{
		string mod = _api.GetActiveMod();
		_all.Clear();
		_list.Clear();
		if (string.IsNullOrEmpty(mod))
		{
			_status.Text = "No active mod. Pick one in the Mod Studio first.";
			return;
		}

		string text = _api.ReadText(Engine_FxRecipes.RecipesPath);
		if (text.Length == 0)
		{
			_status.Text = $"'{mod}' has no {Engine_FxRecipes.RecipesPath} yet.";
			return;
		}

		_all.AddRange(Engine_FxRecipes.Parse(mod, text));
		foreach (Engine_FxRecipes.Recipe recipe in _all)
		{
			_list.AddItem(recipe.Id);
		}

		_status.Text = $"{_all.Count} recipe(s) from '{mod}'.";
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
		_base.Clear();
		foreach (string name in recipe.BaseNames)
		{
			_base.AddItem(name);
		}

		if (_base.ItemCount != 0)
		{
			_base.Select(0);
		}

		_tintR.Value = recipe.Tint.R;
		_tintG.Value = recipe.Tint.G;
		_tintB.Value = recipe.Tint.B;
		_intensity.Value = recipe.Intensity;
		_size.Value = recipe.Size;
		foreach (Node child in _attachRows.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Engine_FxRecipes.Attachment attachment in recipe.Attach)
		{
			AddAttachRow(attachment);
		}

		_filling = false;
		WireChanges();
		Preview();
	}

	private bool _wired;

	private void WireChanges()
	{
		if (_wired)
		{
			return;
		}

		_wired = true;
		foreach (SpinBox spin in new[] { _tintR, _tintG, _tintB, _intensity, _size })
		{
			spin.ValueChanged += _ => Changed();
		}
	}

	private void AddAttachRow(Engine_FxRecipes.Attachment attachment)
	{
		var row = new HBoxContainer { Name = "Attach" };
		var effect = new LineEdit { Text = attachment.Effect, CustomMinimumSize = new Vector2(170, 0), TooltipText = "effect name" };
		effect.TextChanged += _ => Changed();
		row.AddChild(effect);
		foreach ((string tip, double value, double min, double max, double step) in new (string, double, double, double, double)[]
		{
			("count", attachment.Count, 1, 12, 1),
			("radius", attachment.Radius, 0, 6, 0.05),
			("height", attachment.Height, -2, 4, 0.05),
			("turns per second", attachment.TurnsPerSecond, -4, 4, 0.05),
			("scale", attachment.Scale, 0.1, 4, 0.05),
			("intensity", attachment.Intensity, 0, 4, 0.05),
			("stack", attachment.Stack, 1, 12, 1),
			("stack step", attachment.StackStep, 0, 3, 0.05),
			("stagger seconds", attachment.Stagger, 0, 5, 0.05),
			("radius per level", attachment.RadiusStep, -1, 2, 0.02),
			("scale per level", attachment.ScaleStep, 0.5, 1.5, 0.01),
			("fade in seconds", attachment.FadeIn, 0, 3, 0.05),
		})
		{
			var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, TooltipText = tip, CustomMinimumSize = new Vector2(64, 0) };
			spin.ValueChanged += _ => Changed();
			row.AddChild(spin);
		}

		var remove = new Button { Text = "✕" };
		remove.Pressed += () =>
		{
			row.QueueFree();
			CallDeferred(nameof(Changed));
		};
		row.AddChild(remove);
		_attachRows.AddChild(row);
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
			if (child is not HBoxContainer row || row.IsQueuedForDeletion())
			{
				continue;
			}

			string effect = (row.GetChild(0) as LineEdit)?.Text.Trim() ?? string.Empty;
			if (effect.Length == 0)
			{
				continue;
			}

			double Value(int at) => (row.GetChild(at) as SpinBox)?.Value ?? 0;
			attach.Add(new Engine_FxRecipes.Attachment(
				effect, (float)Value(5), (int)Value(1), (float)Value(2), (float)Value(3), (float)Value(4), (float)Value(6),
				(int)Value(7), (float)Value(8), (float)Value(9), null, (float)Value(10), (float)Value(11), (float)Value(12)));
		}

		return Engine_FxRecipes.Make(
			was.Id, was.Mod, was.When, was.Match, was.BaseNames,
			new Color((float)_tintR.Value, (float)_tintG.Value, (float)_tintB.Value),
			(float)_intensity.Value, (float)_size.Value, attach, was.AnchorNames, was.Glow, was.Pieces, was.EveryNode);
	}

	private void Changed()
	{
		if (_filling || Current() is not { } recipe)
		{
			return;
		}

		_all[_selected] = recipe;
		Preview();
	}

	private void Preview()
	{
		if (_filling || Current() is not { } recipe)
		{
			return;
		}

		if (_effect is not null && GodotObject.IsInstanceValid(_effect))
		{
			Engine_FxRecipes.Undress(_effect);
			_effect.QueueFree();
			_effect = null;
		}

		if (_base.Selected < 0 || _base.Selected >= recipe.BaseNames.Count)
		{
			return;
		}

		string name = recipe.BaseNames[_base.Selected];
		if (FableFx.SharedBank() is null)
		{
			_status.Text = "The FX bank is not mounted; open the game data first.";
			return;
		}

		_effect = new FableFx { Name = "LabEffect", EffectName = name, Loop = true, Playing = true, Position = new Vector3(0f, 0.5f, 0f) };
		_rig.AddChild(_effect);
		Engine_FxRecipes.DressPreview(_effect, recipe);
		_status.Text = $"{recipe.Id}: {name} dressed with {recipe.Attach.Count} attachment(s).";
	}

	private void Save()
	{
		if (Current() is not { } recipe)
		{
			_status.Text = "Nothing selected.";
			return;
		}

		_all[_selected] = recipe;
		string json = Engine_FxRecipes.Serialize(_all);
		string error = _api.WriteText(Engine_FxRecipes.RecipesPath, json);
		if (error.Length != 0)
		{
			_status.Text = error;
			return;
		}

		_status.Text = $"Saved {_all.Count} recipe(s) to '{_api.GetActiveMod()}'. In a running game: refii_action fx-recipes-reload.";
	}
}
#endif
