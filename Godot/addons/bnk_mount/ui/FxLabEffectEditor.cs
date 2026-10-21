#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Godot;
using Refii.EditorTools;
using Refii.Fx;

namespace Refii.Assets.UI;

/// <summary>
/// THE FX LAB'S EFFECT EDITOR: an effect of the bank (or of the active mod) opened as an
/// <see cref="Engine_FxEffectJson.Definition"/>, its emitters, parameter timelines, systems and
/// visuals edited, previewed on the lab's turntable through <see cref="FableFx"/> drawing a
/// preview bank (<see cref="Engine_FxModEffects.PreviewBank"/>), and saved as the active mod's
/// <c>fx/effects/&lt;name&gt;.json</c>.
/// </summary>
/// <remarks>
/// A bank effect opens as an OVERRIDE (its name and its emitters' ids kept: saved, it replaces the
/// shipped effect and those emitters wherever the bank uses them, the guest's own spawns included).
/// "Clone into mod" turns the draft into a NEW effect under a new name, every id cleared so nothing
/// shipped changes. The fields shown are the ones the renderer reads for the selected entity's type.
/// </remarks>
public partial class FxLabEffectEditor : ScrollContainer
{
	private FxLabView? _lab;
	private RefiiModApi _api = new();

	private Engine_FxEffectJson.Definition? _effect;
	private int _emitter = -1;
	private int _visual = -1;
	private bool _dirty;
	private double _sinceEdit;
	private bool _filling;

	private LineEdit _filter = null!;
	private ItemList _sources = null!;
	private readonly List<(string Name, bool Mine)> _sourceRows = new();
	private LineEdit _newName = null!;
	private Label _mode = null!;
	private LineEdit _name = null!;
	private CheckBox _zSort = null!;
	private OptionButton _rate = null!;
	private SpinBox _scale = null!;
	private ColorPickerButton _tint = null!;
	private ItemList _emitters = null!;
	private HFlowContainer _emitterFields = null!;
	private OptionButton _param = null!;
	private KeyList _paramKeys = null!;
	private OptionButton _vector = null!;
	private KeyList _vectorKeys = null!;
	private VBoxContainer _splineBox = null!;
	private KeyList _splinePoints = null!;
	private Label _systemLabel = null!;
	private HFlowContainer _systemFields = null!;
	private ItemList _visuals = null!;
	private HFlowContainer _visualFields = null!;

	/// <summary>The scalar and vector fields each entity type's renderer path reads.</summary>
	private static readonly (string Field, string Label, double Min, double Max, double Step)[] Common =
	{
		(nameof(FxEmitter.Pos), "position", -100, 100, 0.01), (nameof(FxEmitter.Quat), "rotation (quat)", -1, 1, 0.001),
		(nameof(FxEmitter.UseLife), "use life", 0, 1, 1), (nameof(FxEmitter.LifetimeFrames), "lifetime frames", 0, 100000, 1),
		(nameof(FxEmitter.ProcessRadius), "process radius", 0, 1000, 0.5), (nameof(FxEmitter.PositionalInteractable), "positional interactable", 0, 1, 1),
	};

	private static readonly (string Field, string Label, double Min, double Max, double Step)[] Emitting =
	{
		(nameof(FxEmitter.SubType), "shape (0 point 1 sphere 2 disc 3 mesh)", 0, 3, 1), (nameof(FxEmitter.MaxActive), "max active", -1, 100000, 1),
		(nameof(FxEmitter.InitialNum), "initial", 0, 10000, 1), (nameof(FxEmitter.SpawnRadius), "spawn radius", 0, 100, 0.01),
		(nameof(FxEmitter.EmitOnSurface), "on surface / edge", 0, 1, 1), (nameof(FxEmitter.UseShapeSpawnDir), "radial direction", 0, 1, 1),
		(nameof(FxEmitter.AttachToRoot), "attach to root", 0, 1, 1), (nameof(FxEmitter.Fades), "fades (in from, in to, out from, out to)", 0, 10000, 0.1),
		(nameof(FxEmitter.ReleaseTrail), "release trail", 0, 1, 1), (nameof(FxEmitter.GlobalCollider), "global collider", 0, 1, 1),
		(nameof(FxEmitter.CollisionRestitution), "restitution", 0, 10, 0.01), (nameof(FxEmitter.SortParticles), "sort", 0, 1, 1),
		(nameof(FxEmitter.Spin), "spin (rad/s about the emitter's Z)", -100, 100, 0.01),
	};

	private static readonly (string Field, string Label, double Min, double Max, double Step)[] SplineEmitting =
	{
		(nameof(FxEmitter.SplineRateEmitter), "spline rate", -100, 100, 0.001), (nameof(FxEmitter.AnimationMode), "animation mode", 0, 8, 1),
		(nameof(FxEmitter.NumSplineSegments), "segments", 1, 256, 1), (nameof(FxEmitter.RandomiseAnimMagnitude), "anim magnitude", 0, 100, 0.01),
		(nameof(FxEmitter.SmoothAnimationSpeed), "smooth speed", 0, 100, 0.01), (nameof(FxEmitter.JerkyAnimationRate), "jerky rate", 0, 100, 0.01),
		(nameof(FxEmitter.RandomiseAnimMagnitudeFlag), "randomise magnitude", 0, 1, 1), (nameof(FxEmitter.RandomiseAnimationRates), "randomise rates", 0, 1, 1),
	};

	private static readonly (string Field, string Label, double Min, double Max, double Step)[] Spline =
	{
		(nameof(FxEmitter.SplineRate), "rate (t/s)", -100, 100, 0.001), (nameof(FxEmitter.SplineLooped), "looped", 0, 1, 1),
		(nameof(FxEmitter.SplineReverseFlow), "ping-pong", 0, 1, 1), (nameof(FxEmitter.SplineRandomStart), "random start", 0, 1, 1),
		(nameof(FxEmitter.SplineStraight), "straight", 0, 1, 1), (nameof(FxEmitter.SplineRandomGenerator), "random point", 0, 1, 1),
		(nameof(FxEmitter.SplineScaleEmissionByLength), "scale emission by length", 0, 1, 1),
	};

	private static readonly (string Field, string Label, double Min, double Max, double Step)[] SystemFields =
	{
		(nameof(FxSystemDef.Mode), "mode (1 sprite 2/3 ribbon 4 mesh)", 1, 4, 1), (nameof(FxSystemDef.LoopStartFrames), "loop start", 0, 100000, 1),
		(nameof(FxSystemDef.LoopEndFrames), "loop end", 0, 100000, 1), (nameof(FxSystemDef.LoopCount), "loops (-1 forever)", -1, 10000, 1),
		(nameof(FxSystemDef.LastStartFrames), "life frames", 0, 100000, 1),
	};

	private static readonly (string Field, string Label, double Min, double Max, double Step)[] Visual =
	{
		(nameof(FxVisual.Texture), "texture", 0, 0, 0), (nameof(FxVisual.TexCols), "cols", 1, 64, 1), (nameof(FxVisual.TexRows), "rows", 1, 64, 1),
		(nameof(FxVisual.FrameSpeed), "frame speed", 0, 240, 0.1), (nameof(FxVisual.RandomFrame), "random frame", 0, 1, 1),
		(nameof(FxVisual.Alpha), "alpha", 0, 4, 0.01), (nameof(FxVisual.SelfIllum), "self illum", 0, 4, 0.01),
		(nameof(FxVisual.SizeX), "size x", 0, 100, 0.01), (nameof(FxVisual.SizeY), "size y", 0, 100, 0.01),
		(nameof(FxVisual.GlobalScale), "global scale", 0, 100, 0.01), (nameof(FxVisual.RotSpeed), "spin", -100, 100, 0.01),
		(nameof(FxVisual.RotSpread), "spin spread", 0, 100, 0.01), (nameof(FxVisual.RotInitialRandom), "random angle", 0, 1, 1),
		(nameof(FxVisual.TrailWidth), "trail width / mesh scale", 0, 100, 0.01), (nameof(FxVisual.TrailLen), "trail segments", 1, 16, 1),
		(nameof(FxVisual.MeshPath), "mesh", 0, 0, 0), (nameof(FxVisual.SrcBlend), "src blend", -1, 20, 1), (nameof(FxVisual.DstBlend), "dst blend", -1, 20, 1),
		(nameof(FxVisual.BlendOp), "blend op", 0, 8, 1), (nameof(FxVisual.SoftDepthContrast), "soft depth", 0, 100, 0.01),
		(nameof(FxVisual.FlickerPeriod), "flicker period", 0, 30, 0.01), (nameof(FxVisual.FlickerSpreadX), "flicker x", 0, 10, 0.01),
		(nameof(FxVisual.FlickerSpreadY), "flicker y", 0, 10, 0.01), (nameof(FxVisual.Displacement), "displacement", 0, 1, 1),
		(nameof(FxVisual.OrSpeed), "mesh turn (rad/frame)", -10, 10, 0.001), (nameof(FxVisual.OrSpread), "mesh turn spread", 0, 10, 0.001),
		(nameof(FxVisual.OrAxis), "mesh turn axis", -1, 1, 0.001), (nameof(FxVisual.OrRandAngle), "mesh random angle", 0, 1, 1),
		(nameof(FxVisual.OrRandAxis), "mesh random axis", 0, 1, 1), (nameof(FxVisual.MeshAlign), "mesh rest orientation (quat)", -1, 1, 0.001),
	};

	/// <summary>The flag a visual's value is read under: editing the value states the flag.</summary>
	private static readonly Dictionary<string, string> VisualFlags = new(StringComparer.Ordinal)
	{
		[nameof(FxVisual.Alpha)] = nameof(FxVisual.HasAlpha), [nameof(FxVisual.SelfIllum)] = nameof(FxVisual.HasSelfIllum),
		[nameof(FxVisual.SizeX)] = nameof(FxVisual.HasSize), [nameof(FxVisual.SizeY)] = nameof(FxVisual.HasSize),
		[nameof(FxVisual.GlobalScale)] = nameof(FxVisual.HasGlobalScale), [nameof(FxVisual.RotSpeed)] = nameof(FxVisual.HasRotation),
		[nameof(FxVisual.RotSpread)] = nameof(FxVisual.HasRotation), [nameof(FxVisual.RotInitialRandom)] = nameof(FxVisual.HasRotation),
		[nameof(FxVisual.TrailWidth)] = nameof(FxVisual.HasTrailWidth), [nameof(FxVisual.TrailLen)] = nameof(FxVisual.HasTrailLen),
		[nameof(FxVisual.FlickerPeriod)] = nameof(FxVisual.HasFlicker), [nameof(FxVisual.FlickerSpreadX)] = nameof(FxVisual.HasFlicker),
		[nameof(FxVisual.FlickerSpreadY)] = nameof(FxVisual.HasFlicker),
		[nameof(FxVisual.OrSpeed)] = nameof(FxVisual.HasOrient), [nameof(FxVisual.OrSpread)] = nameof(FxVisual.HasOrient),
		[nameof(FxVisual.OrAxis)] = nameof(FxVisual.HasOrient), [nameof(FxVisual.OrRandAngle)] = nameof(FxVisual.HasOrient),
		[nameof(FxVisual.OrRandAxis)] = nameof(FxVisual.HasOrient), [nameof(FxVisual.MeshAlign)] = nameof(FxVisual.HasMeshAlign),
	};

	/// <summary>Set before the node enters the tree (the lab builds it).</summary>
	public FxLabView? Lab { get => _lab; set => _lab = value; }

	public RefiiModApi Api { get => _api; set => _api = value; }

	public FxLabEffectEditor()
	{
	}

	public FxLabEffectEditor(FxLabView lab, RefiiModApi api)
	{
		_lab = lab;
		_api = api;
	}

	public override void _Ready()
	{
		HorizontalScrollMode = ScrollMode.Disabled;
		SizeFlagsVertical = SizeFlags.ExpandFill;
		var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 6);
		AddChild(column);

		// Where an effect comes from.
		FxLabView.Heading(column, "Effect to edit — ★ the active mod's own, the rest the particle bank's");
		var filterRow = new HBoxContainer();
		column.AddChild(filterRow);
		_filter = new LineEdit { PlaceholderText = "filter effects…", ClearButtonEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_filter.TextChanged += _ => FillSources();
		filterRow.AddChild(_filter);
		var reload = new Button { Text = "↺", TooltipText = "List the bank's and the active mod's effects again" };
		reload.Pressed += Reload;
		filterRow.AddChild(reload);
		_sources = new ItemList { CustomMinimumSize = new Vector2(0, 140) };
		_sources.ItemActivated += index => OpenRow((int)index);
		_sources.ItemSelected += index => OpenRow((int)index);
		column.AddChild(_sources);

		var cloneRow = new HBoxContainer();
		column.AddChild(cloneRow);
		_newName = new LineEdit { PlaceholderText = "new effect name (letters, digits, _ - .)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		cloneRow.AddChild(_newName);
		var clone = new Button { Text = "Clone into mod", TooltipText = "The effect being edited as a NEW effect under this name: fresh ids, nothing shipped changes" };
		clone.Pressed += CloneAsNew;
		cloneRow.AddChild(clone);

		_mode = new Label { AutowrapMode = TextServer.AutowrapMode.Word };
		_mode.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.45f));
		column.AddChild(_mode);

		// The effect's own fields.
		var effectRow = new HFlowContainer();
		column.AddChild(effectRow);
		_name = FxLabView.Labeled(effectRow, "name", new LineEdit { CustomMinimumSize = new Vector2(200, 0) });
		_name.TextChanged += text =>
		{
			if (!_filling && _effect is not null)
			{
				_effect.Name = text.Trim();
				Changed();
			}
		};
		_zSort = new CheckBox { Text = "z-sort emitters" };
		_zSort.Toggled += on => Edit(() => _effect!.ZSortEmitters = on);
		effectRow.AddChild(_zSort);
		_rate = FxLabView.Labeled(effectRow, "update", new OptionButton());
		_rate.AddItem("15 Hz (0)", 0);
		_rate.AddItem("30 Hz (1)", 1);
		_rate.ItemSelected += index => Edit(() => _effect!.UpdateFrequency = (uint)index);
		_scale = FxLabView.Labeled(effectRow, "scale", new SpinBox { MinValue = 0.01, MaxValue = 100, Step = 0.01, AllowGreater = true });
		_scale.ValueChanged += value => Edit(() => _effect!.Scale = (float)value);
		_tint = FxLabView.Labeled(effectRow, "tint", new ColorPickerButton { CustomMinimumSize = new Vector2(48, 24), EditAlpha = false });
		_tint.ColorChanged += c => Edit(() => _effect!.Tint = new[] { c.R, c.G, c.B });

		// Emitters.
		var emitterHead = new HBoxContainer();
		column.AddChild(emitterHead);
		FxLabView.Heading(emitterHead, "Emitters and entities").SizeFlagsHorizontal = SizeFlags.ExpandFill;
		var duplicate = new Button { Text = "Duplicate", TooltipText = "A copy of the selected entity in the same chain, with a fresh id" };
		duplicate.Pressed += DuplicateEmitter;
		emitterHead.AddChild(duplicate);
		var removeEmitter = new Button { Text = "Remove" };
		ModStudioUiStyles.ApplyRedButtonStyle(removeEmitter);
		removeEmitter.Pressed += RemoveEmitter;
		emitterHead.AddChild(removeEmitter);
		_emitters = new ItemList { CustomMinimumSize = new Vector2(0, 110) };
		_emitters.ItemSelected += index => SelectEmitter((int)index);
		column.AddChild(_emitters);
		_emitterFields = new HFlowContainer();
		column.AddChild(_emitterFields);

		_splineBox = new VBoxContainer();
		column.AddChild(_splineBox);
		FxLabView.Heading(_splineBox, "Spline points (the entity's own space)");
		_splinePoints = new KeyList(_splineBox, new[] { "x", "y", "z" }, SplinePointsChanged);

		FxLabView.Heading(column, "Parameter timeline — [seconds, value]; the first key starts it, one key is a constant");
		_param = new OptionButton();
		_param.ItemSelected += _ => FillParamKeys();
		column.AddChild(_param);
		_paramKeys = new KeyList(column, new[] { "seconds", "value" }, ParamKeysChanged);

		FxLabView.Heading(column, "Vector timeline — [seconds, x, y, z]");
		_vector = new OptionButton();
		foreach (string name in Engine_FxEffectJson.VectorNames)
		{
			_vector.AddItem(name);
		}

		_vector.ItemSelected += _ => FillVectorKeys();
		column.AddChild(_vector);
		_vectorKeys = new KeyList(column, new[] { "seconds", "x", "y", "z" }, VectorKeysChanged);

		// The selected emitter's system and its visuals.
		_systemLabel = FxLabView.Heading(column, "System");
		_systemFields = new HFlowContainer();
		column.AddChild(_systemFields);
		var visualHead = new HBoxContainer();
		column.AddChild(visualHead);
		FxLabView.Heading(visualHead, "Visuals (keyframes over a particle's life)").SizeFlagsHorizontal = SizeFlags.ExpandFill;
		var duplicateVisual = new Button { Text = "Duplicate" };
		duplicateVisual.Pressed += DuplicateVisual;
		visualHead.AddChild(duplicateVisual);
		var removeVisual = new Button { Text = "Remove" };
		ModStudioUiStyles.ApplyRedButtonStyle(removeVisual);
		removeVisual.Pressed += RemoveVisual;
		visualHead.AddChild(removeVisual);
		_visuals = new ItemList { CustomMinimumSize = new Vector2(0, 80) };
		_visuals.ItemSelected += index => SelectVisual((int)index);
		column.AddChild(_visuals);
		_visualFields = new HFlowContainer();
		column.AddChild(_visualFields);

		var saveRow = new HBoxContainer();
		column.AddChild(saveRow);
		var save = new Button { Text = "💾 Save effect to mod", TooltipText = "Write fx/effects/<name>.json into the active mod package" };
		save.Pressed += Save;
		saveRow.AddChild(save);
		var delete = new Button { Text = "Delete from mod", TooltipText = "Remove fx/effects/<name>.json from the active mod" };
		ModStudioUiStyles.ApplyRedButtonStyle(delete);
		delete.Pressed += DeleteFromMod;
		saveRow.AddChild(delete);

		Reload();
	}

	public override void _Process(double delta)
	{
		if (!_dirty)
		{
			return;
		}

		_sinceEdit += delta;
		if (_sinceEdit >= 0.15)
		{
			_dirty = false;
			Preview();
		}
	}

	// ---------------------------------------------------------------------------------------
	// Sources
	// ---------------------------------------------------------------------------------------

	public void Reload()
	{
		if (_sources is null)
		{
			return;
		}

		_sourceRows.Clear();
		foreach (string name in _api.ListModEffects())
		{
			_sourceRows.Add((name, true));
		}

		var mine = new HashSet<string>(_sourceRows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
		foreach (string name in _api.ListBankEffects())
		{
			if (!mine.Contains(name))
			{
				_sourceRows.Add((name, false));
			}
		}

		FillSources();
	}

	private readonly List<int> _shown = new();

	private void FillSources()
	{
		_sources.Clear();
		_shown.Clear();
		string filter = _filter.Text.Trim();
		for (int i = 0; i < _sourceRows.Count; i++)
		{
			(string name, bool mine) = _sourceRows[i];
			if (filter.Length != 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}

			_sources.AddItem(mine ? "★ " + name : name);
			_shown.Add(i);
		}
	}

	private void OpenRow(int index)
	{
		if (index >= 0 && index < _shown.Count)
		{
			Open(_sourceRows[_shown[index]].Name);
		}
	}

	/// <summary>Opens an effect: the active mod's own file when it ships one, else the bank's as an override draft.</summary>
	public void Open(string name)
	{
		if (_api.SourceEffect(name) is not { } d)
		{
			_lab?.SetStatus($"No effect '{name}' in the bank or the active mod.");
			return;
		}

		bool mine = _api.ListModEffects().Contains(name, StringComparer.OrdinalIgnoreCase);
		Load(d, mine
			? $"The active mod's effect '{d.Name}'."
			: $"OVERRIDE draft of the bank's '{d.Name}': saved as it is, it REPLACES that effect and its emitters wherever the bank uses them " +
			  "(the guest's own spawns included). Clone into mod for a new effect instead.");
	}

	private void CloneAsNew()
	{
		string name = _newName.Text.Trim();
		if (_effect is null)
		{
			_lab?.SetStatus("Open an effect first.");
			return;
		}

		if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
		{
			_lab?.SetStatus("Give the new effect a name: letters, digits, '_', '-' and '.' only.");
			return;
		}

		Load(Engine_FxEffectJson.AsNew(_effect, name),
			$"NEW effect '{name}' (cloned from '{_effect.Name}'): every id is fresh, nothing shipped changes. Save to write it into the mod.");
	}

	private void Load(Engine_FxEffectJson.Definition d, string mode)
	{
		_effect = d;
		_mode.Text = mode;
		_filling = true;
		_name.Text = d.Name;
		_zSort.ButtonPressed = d.ZSortEmitters;
		_rate.Select(d.UpdateFrequency >= 1 ? 1 : 0);
		_scale.Value = d.Scale;
		_tint.Color = new Color(d.Tint[0], d.Tint[1], d.Tint[2]);
		_filling = false;
		FillEmitters();
		SelectEmitter(d.Emitters.Count != 0 ? 0 : -1);
		Preview();
	}

	// ---------------------------------------------------------------------------------------
	// Emitters
	// ---------------------------------------------------------------------------------------

	private static string Kind(uint type) => type switch
	{
		0 => "emitter", 3 => "orbital", 4 => "attractor", 5 => "spline", 6 => "root", 7 => "point light", 8 => "spot light",
		9 => "spline emitter", _ => $"type {type}",
	};

	private void FillEmitters()
	{
		_emitters.Clear();
		if (_effect is null)
		{
			return;
		}

		for (int i = 0; i < _effect.Emitters.Count; i++)
		{
			FxEmitter e = _effect.Emitters[i];
			string id = e.EntityId != 0 ? $"0x{e.EntityId:X8}" : "new";
			int chain = _effect.Parts.FirstOrDefault(p => p.Emitter == i).Vector;
			_emitters.AddItem($"#{i}  {Kind(e.TypeId)}  · chain {chain} · {id}");
		}
	}

	private void SelectEmitter(int index)
	{
		_emitter = index;
		if (_effect is null || index < 0 || index >= _effect.Emitters.Count)
		{
			ClearChildren(_emitterFields);
			_splineBox.Visible = false;
			return;
		}

		_emitters.Select(index);
		FxEmitter e = _effect.Emitters[index];
		ClearChildren(_emitterFields);
		IEnumerable<(string, string, double, double, double)> fields = Common;
		if (e.TypeId is 0 or 9)
		{
			fields = fields.Concat(Emitting);
		}

		if (e.TypeId == 9)
		{
			fields = fields.Concat(SplineEmitting).Concat(Spline.Take(6));
		}

		if (e.TypeId == 5)
		{
			fields = fields.Concat(Spline);
		}

		if (e.TypeId == 3)
		{
			fields = fields.Append((nameof(FxEmitter.AngularRotationRate), "turn rate", -100, 100, 0.01));
		}

		if (e.TypeId == 4)
		{
			fields = fields.Append((nameof(FxEmitter.GlobalAttractor), "global (read, not honoured)", 0, 1, 1));
		}

		foreach ((string field, string label, double min, double max, double step) in fields)
		{
			AddField(_emitterFields, e, field, label, min, max, step, () =>
			{
				// The shape flags are the two the standard emitter's loader states for these.
				e.ShapeFlagA = e.EmitOnSurface;
				e.ShapeFlagB = e.UseShapeSpawnDir;
			});
		}

		if (e.TypeId is 7 or 8)
		{
			var halo = FxLabView.Labeled(_emitterFields, "halo texture", new LineEdit { Text = e.HaloTextureHash != 0 ? $"0x{e.HaloTextureHash:X8}" : string.Empty, CustomMinimumSize = new Vector2(110, 0) });
			halo.TextChanged += text => Edit(() =>
			{
				string t = text.Trim();
				e.HaloTextureHash = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
					uint.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h) ? h : 0u;
			});
		}

		_splineBox.Visible = e.TypeId == 5;
		_splinePoints.Load(e.SplinePoints.Select(p => p.ToArray()).ToList());

		_filling = true;
		int was = _param.Selected;
		_param.Clear();
		for (int i = 0; i < Engine_FxEffectJson.ParamNames.Length; i++)
		{
			_param.AddItem((e.Generic[i].Keys.Count != 0 ? "• " : "  ") + Engine_FxEffectJson.ParamNames[i], i);
		}

		_param.Select(was >= 0 && was < _param.ItemCount ? was : FirstKeyed(e));
		_filling = false;
		FillParamKeys();
		FillVectorKeys();
		FillSystem();
	}

	private static int FirstKeyed(FxEmitter e)
	{
		for (int i = 0; i < e.Generic.Length; i++)
		{
			if (e.Generic[i].Keys.Count != 0)
			{
				return i;
			}
		}

		return 2;
	}

	private FxEmitter? Emitter => _effect is not null && _emitter >= 0 && _emitter < _effect.Emitters.Count ? _effect.Emitters[_emitter] : null;

	private void FillParamKeys()
	{
		if (_filling || Emitter is not { } e || _param.Selected < 0)
		{
			return;
		}

		_paramKeys.Load(e.Generic[_param.Selected].Keys.Select(k => new[] { k.Time, k.Value }).ToList());
	}

	private void ParamKeysChanged(List<float[]> keys)
	{
		if (Emitter is not { } e || _param.Selected < 0)
		{
			return;
		}

		int index = _param.Selected;
		Edit(() =>
		{
			e.Generic[index] = Engine_FxBank.MakeTimeline(index, keys.Select(k => (k[0], k[1])).ToList());
			Engine_FxBank.LinkParameters(e);
		});
		_param.SetItemText(index, (keys.Count != 0 ? "• " : "  ") + Engine_FxEffectJson.ParamNames[index]);
	}

	private static FxVectorTimeline VectorOf(FxEmitter e, int index) => index switch
	{
		0 => e.Vec0, 1 => e.Vec1, 2 => e.Vec2, _ => e.LightColour,
	};

	private void FillVectorKeys()
	{
		if (Emitter is not { } e || _vector.Selected < 0)
		{
			return;
		}

		_vectorKeys.Load(VectorOf(e, _vector.Selected).Keys.Select(k => new[] { k.Time, k.X, k.Y, k.Z }).ToList());
	}

	private void VectorKeysChanged(List<float[]> keys)
	{
		if (Emitter is not { } e || _vector.Selected < 0)
		{
			return;
		}

		int index = _vector.Selected;
		Edit(() =>
		{
			FxVectorTimeline made = Engine_FxBank.MakeVecTimeline(index, keys.Select(k => (k[0], k[1], k[2], k[3])).ToList());
			switch (index)
			{
				case 0: e.Vec0 = made; break;
				case 1: e.Vec1 = made; break;
				case 2: e.Vec2 = made; break;
				default: e.LightColour = made; break;
			}
		});
	}

	private void SplinePointsChanged(List<float[]> points)
	{
		if (Emitter is { } e)
		{
			Edit(() => e.SplinePoints = points.Select(p => p.ToArray()).ToArray());
		}
	}

	private void DuplicateEmitter()
	{
		if (_effect is null || Emitter is null)
		{
			return;
		}

		int source = _emitter;
		var probe = new Engine_FxEffectJson.Definition { Name = "copy" };
		probe.Emitters.Add(_effect.Emitters[source]);
		probe.Parts.Add(new FxEffectPart(0, -1, 0, 0));
		FxEmitter copy = Engine_FxEffectJson.Copy(probe).Emitters[0];
		copy.EntityId = 0;
		_effect.Emitters.Add(copy);
		int index = _effect.Emitters.Count - 1;
		foreach (FxEffectPart part in _effect.Parts.Where(p => p.Emitter == source).ToList())
		{
			_effect.Parts.Add(part with { Emitter = index });
		}

		FillEmitters();
		SelectEmitter(index);
		Changed();
	}

	private void RemoveEmitter()
	{
		if (_effect is null || Emitter is null || _effect.Emitters.Count <= 1)
		{
			return;
		}

		int removed = _emitter;
		_effect.Emitters.RemoveAt(removed);
		var parts = _effect.Parts
			.Where(p => p.Emitter != removed)
			.Select(p => p with { Emitter = p.Emitter > removed ? p.Emitter - 1 : p.Emitter })
			.ToList();
		_effect.Parts.Clear();
		_effect.Parts.AddRange(parts);
		FillEmitters();
		SelectEmitter(Math.Min(removed, _effect.Emitters.Count - 1));
		Changed();
	}

	// ---------------------------------------------------------------------------------------
	// System and visuals
	// ---------------------------------------------------------------------------------------

	private FxSystemDef? SystemOf()
	{
		if (_effect is null || _emitter < 0)
		{
			return null;
		}

		FxEffectPart part = _effect.Parts.FirstOrDefault(p => p.Emitter == _emitter && p.System >= 0, new FxEffectPart(-1, -1, 0, 0));
		return part.System >= 0 && part.System < _effect.Systems.Count ? _effect.Systems[part.System] : null;
	}

	private void FillSystem()
	{
		ClearChildren(_systemFields);
		_visuals.Clear();
		ClearChildren(_visualFields);
		if (SystemOf() is not { } system)
		{
			_systemLabel.Text = "System — this entity draws nothing of its own";
			return;
		}

		int at = _effect!.Systems.IndexOf(system);
		int users = _effect.Parts.Count(p => p.System == at);
		_systemLabel.Text = $"System #{at}" + (users > 1 ? $" (shared by {users} parts: an edit reaches them all)" : string.Empty);
		foreach ((string field, string label, double min, double max, double step) in SystemFields)
		{
			AddField(_systemFields, system, field, label, min, max, step, null);
		}

		for (int i = 0; i < system.Visuals.Count; i++)
		{
			FxVisual v = system.Visuals[i];
			string what = global::System.IO.Path.GetFileName((v.HasMesh ? v.MeshPath : v.Texture).Replace('\\', '/'));
			_visuals.AddItem($"#{i}  at {v.Delay:0.##}s  {what}");
		}

		SelectVisual(system.Visuals.Count != 0 ? Math.Clamp(_visual, 0, system.Visuals.Count - 1) : -1);
	}

	private void SelectVisual(int index)
	{
		_visual = index;
		ClearChildren(_visualFields);
		if (SystemOf() is not { } system || index < 0 || index >= system.Visuals.Count)
		{
			return;
		}

		_visuals.Select(index);
		FxVisual v = system.Visuals[index];
		var colour = FxLabView.Labeled(_visualFields, "colour", new ColorPickerButton
		{
			Color = new Color(v.Color[0], v.Color[1], v.Color[2]), CustomMinimumSize = new Vector2(48, 24), EditAlpha = false,
		});
		colour.ColorChanged += c => Edit(() =>
		{
			// The renderer reads the SQUARED colour (the loader squares the colour class).
			v.Color = new[] { c.R, c.G, c.B };
			v.ColorSq = new[] { c.R * c.R, c.G * c.G, c.B * c.B };
			v.HasColor = true;
		});
		SpinBox delay = FxLabView.Labeled(_visualFields, "starts at s", FxLabView.NewSpin(0, 600, 0.01));
		delay.Value = v.Delay;
		delay.ValueChanged += value => Edit(() =>
		{
			v.Delay = (float)value;
			v.DelayFrames = (int)Math.Round(value * Refii.Fx.Xex.XexConst.Fps);
		});

		foreach ((string field, string label, double min, double max, double step) in Visual)
		{
			AddField(_visualFields, v, field, label, min, max, step, () =>
			{
				if (VisualFlags.TryGetValue(field, out string? flag))
				{
					typeof(FxVisual).GetField(flag)!.SetValue(v, true);
				}

				v.HasTexture = v.Texture.Length != 0;
				v.HasMesh = v.MeshPath.Length != 0;
			});
		}
	}

	private void DuplicateVisual()
	{
		if (SystemOf() is not { } system || _visual < 0 || _visual >= system.Visuals.Count)
		{
			return;
		}

		var copy = new FxVisual();
		foreach (FieldInfo field in Engine_FxEffectJson.PlainFields(typeof(FxVisual)))
		{
			object? value = field.GetValue(system.Visuals[_visual]);
			field.SetValue(copy, value is float[] array ? array.Clone() : value);
		}

		system.Visuals.Insert(_visual + 1, copy);
		_visual++;
		FillSystem();
		Changed();
	}

	private void RemoveVisual()
	{
		if (SystemOf() is not { } system || _visual < 0 || _visual >= system.Visuals.Count)
		{
			return;
		}

		system.Visuals.RemoveAt(_visual);
		_visual = Math.Max(0, _visual - 1);
		FillSystem();
		Changed();
	}

	// ---------------------------------------------------------------------------------------
	// Fields by reflection over the model
	// ---------------------------------------------------------------------------------------

	/// <summary>One widget for a model field: a SpinBox per number, a CheckBox for a flag, a LineEdit for text.</summary>
	private void AddField(Container flow, object target, string fieldName, string label, double min, double max, double step, Action? after)
	{
		FieldInfo? field = target.GetType().GetField(fieldName);
		if (field is null)
		{
			return;
		}

		object? value = field.GetValue(target);
		switch (value)
		{
			case bool b:
			{
				var check = new CheckBox { Text = label, ButtonPressed = b };
				check.Toggled += on => Edit(() => { field.SetValue(target, on); after?.Invoke(); });
				flow.AddChild(check);
				break;
			}

			case string s:
			{
				var edit = FxLabView.Labeled(flow, label, new LineEdit { Text = s, CustomMinimumSize = new Vector2(220, 0) });
				edit.TextChanged += text => Edit(() => { field.SetValue(target, text.Trim()); after?.Invoke(); });
				break;
			}

			case float[] array:
			{
				var row = new HBoxContainer();
				for (int i = 0; i < array.Length; i++)
				{
					int lane = i;
					SpinBox spin = FxLabView.Spin(row, min, max, step, array[i]);
					spin.CustomMinimumSize = new Vector2(64, 0);
					spin.ValueChanged += v => Edit(() =>
					{
						var now = (float[])field.GetValue(target)!;
						now[lane] = (float)v;
						after?.Invoke();
					});
				}

				FxLabView.Labeled(flow, label, row);
				break;
			}

			case float or int or uint:
			{
				SpinBox spin = FxLabView.Labeled(flow, label, FxLabView.NewSpin(min, max, step));
				spin.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
				spin.ValueChanged += v => Edit(() =>
				{
					object converted = field.FieldType == typeof(float) ? (float)v
						: field.FieldType == typeof(int) ? (int)Math.Round(v)
						: (uint)Math.Max(0, Math.Round(v));
					field.SetValue(target, converted);
					after?.Invoke();
				});
				break;
			}
		}
	}

	private void Edit(Action change)
	{
		if (_filling || _effect is null)
		{
			return;
		}

		change();
		Changed();
	}

	private void Changed()
	{
		_dirty = true;
		_sinceEdit = 0;
	}

	private static void ClearChildren(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			node.RemoveChild(child);
			child.QueueFree();
		}
	}

	// ---------------------------------------------------------------------------------------
	// Preview and save
	// ---------------------------------------------------------------------------------------

	private void Preview()
	{
		if (_effect is null || _lab is null || _effect.Name.Length == 0)
		{
			return;
		}

		if (FableFx.SharedBank() is not { } shared)
		{
			_lab.SetStatus("The FX bank is not mounted; open the game data first.");
			return;
		}

		Engine_FxEffectJson.Definition snapshot;
		try
		{
			snapshot = Engine_FxEffectJson.Copy(_effect);
		}
		catch (InvalidOperationException exception)
		{
			_lab.SetStatus($"Not previewed: {exception.Message}");
			return;
		}

		_lab.ShowEffect(_effect.Name, Engine_FxModEffects.PreviewBank(shared, snapshot));
		_lab.SetStatus($"{_effect.Name}: {_effect.Emitters.Count} entities, {_effect.Systems.Count} systems, {_effect.Parts.Count} parts.");
	}

	private void Save()
	{
		if (_effect is null)
		{
			_lab?.SetStatus("Open an effect first.");
			return;
		}

		string error = _api.WriteEffect(_effect);
		_lab?.SetStatus(error.Length != 0
			? error
			: $"Saved {Engine_FxModEffects.PathOf(_effect.Name)} into '{_api.GetActiveMod()}'. The bank picks it up on the re-mount; a running game on its next mount.");
		if (error.Length == 0)
		{
			Reload();
		}
	}

	private void DeleteFromMod()
	{
		if (_effect is null)
		{
			return;
		}

		string error = _api.RemoveEffect(_effect.Name);
		_lab?.SetStatus(error.Length != 0 ? error : $"Removed {Engine_FxModEffects.PathOf(_effect.Name)} from '{_api.GetActiveMod()}'.");
		Reload();
	}

	/// <summary>
	/// A timeline (or point list) as editable rows of numbers: one row per key, a "+ key" button
	/// and a remove button per row; every edit hands the whole list back.
	/// </summary>
	private sealed class KeyList
	{
		private readonly VBoxContainer _rows = new();
		private readonly string[] _columns;
		private readonly Action<List<float[]>> _changed;
		private bool _loading;

		public KeyList(Container parent, string[] columns, Action<List<float[]>> changed)
		{
			_columns = columns;
			_changed = changed;
			parent.AddChild(_rows);
			var add = new Button { Text = "+ key" };
			add.Pressed += () =>
			{
				List<float[]> keys = Read();
				float[] next = keys.Count != 0 ? (float[])keys[^1].Clone() : new float[_columns.Length];
				if (keys.Count != 0)
				{
					next[0] += 0.5f;
				}

				keys.Add(next);
				Load(keys);
				_changed(keys);
			};
			parent.AddChild(add);
		}

		public void Load(List<float[]> keys)
		{
			_loading = true;
			foreach (Node child in _rows.GetChildren())
			{
				_rows.RemoveChild(child);
				child.QueueFree();
			}

			foreach (float[] key in keys)
			{
				var row = new HBoxContainer();
				for (int i = 0; i < _columns.Length; i++)
				{
					SpinBox spin = FxLabView.Spin(row, -1e6, 1e6, 0.001, i < key.Length ? key[i] : 0f, _columns[i]);
					spin.CustomMinimumSize = new Vector2(84, 0);
					spin.ValueChanged += _ =>
					{
						if (!_loading)
						{
							_changed(Read());
						}
					};
				}

				var remove = new Button { Text = "✕" };
				remove.Pressed += () =>
				{
					_rows.RemoveChild(row);
					row.QueueFree();
					_changed(Read());
				};
				row.AddChild(remove);
				_rows.AddChild(row);
			}

			_loading = false;
		}

		public List<float[]> Read()
		{
			var keys = new List<float[]>();
			foreach (Node child in _rows.GetChildren())
			{
				if (child is not HBoxContainer row || row.IsQueuedForDeletion())
				{
					continue;
				}

				keys.Add(row.GetChildren().OfType<SpinBox>().Select(s => (float)s.Value).ToArray());
			}

			return keys;
		}
	}
}
#endif
