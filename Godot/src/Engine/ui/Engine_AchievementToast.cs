using System.Collections.Generic;
using Godot;


namespace Refii.Ui;

/// <summary>
/// The achievement toast, drawn by Godot: a dark brown panel with a thin gold rule, the
/// achievement's icon at the left, gold over cream beside it — the menus' palette.
///
/// TWO PHASES, TEN SECONDS: the name over its gamerscore for five, then the description over the
/// name for five, cross-fading over <see cref="MessageFadeSeconds"/>. In over 0.22 s and out over
/// 0.55 s, combined by MINIMUM so a short toast never reaches full opacity, with an entry slide
/// of <c>(1 - fadeIn) * 22</c> units upward into place. The timing is
/// `AchievementToastDialog::DrawCurrentToast`'s, from the ImGui overlay this replaced.
///
/// THIS IS THE ONLY LOOK, BY THE PLAYER'S CHOICE. Tried on screen against it and refused: the
/// HUD quest box's yellow-and-white slab; the theme's `Style1Frame`, which is plain shape art the
/// game tints, not a frame; and a shipped menu row, whose name phase suited it and whose
/// description phase did not — a row is made for a label, and two or three lines of text spilled
/// through its rims or were shrunk past reading. The panel grows with its text, which is why it
/// can carry a description at all.
///
/// The queue is fed by <see cref="Refii.Achievements.Engine_Achievements.TakeNewUnlocks"/>,
/// which owns the store — an unlock loaded from disk is not news, and a write the guest repeats
/// for an achievement already earned never reaches here.
/// </summary>
public sealed partial class Engine_AchievementToast : Node3D
{
	/// <summary>How long the unlock line is up before the description takes over.</summary>
	public const float UnlockPhaseSeconds = 5.0f;

	/// <summary>How long the description is up afterwards.</summary>
	public const float DescriptionPhaseSeconds = 5.0f;

	public const float ToastDurationSeconds = UnlockPhaseSeconds + DescriptionPhaseSeconds;

	public const float FadeInSeconds = 0.22f;
	public const float MessageFadeSeconds = 0.32f;
	public const float FadeOutSeconds = 0.55f;

	/// <summary>The entry slide, in HUD canvas units.</summary>
	public const float BaseSlide = 22.0f;

	/// <summary>
	/// THE PALETTE. The fill is the old overlay's backing colour, RGBA(41,24,12) — the menu
	/// rows' own dark brown; the first line and the rule are the menus' live label gold
	/// (<see cref="Engine_FrontEndMenuView.LabelColour"/>); the second line is cream.
	/// </summary>
	private static readonly Color Fill = new(41f / 255f, 24f / 255f, 12f / 255f);

	private static readonly Color SecondLine = new(0.86f, 0.80f, 0.66f);

	/// <summary>
	/// THE LOOK'S NUMBERS, in HUD canvas units (the 720-tall space the HUD is authored in),
	/// measured off the screenshot the player chose. Writable over the debug channel
	/// (`achievements do=dial`) so a change can be judged from a shot without a rebuild; the
	/// defaults ARE the look and nothing else reads these.
	/// </summary>
	public static readonly Dictionary<string, float> Dials = new()
	{
		["width"] = 324f,        // the panel
		["padX"] = 8f,           // the icon's inset from the panel's left edge, the text's from its right
		["padY"] = 9f,           // above and below the taller of the icon and the text
		["icon"] = 31.5f,        // the icon square
		["gap"] = 13f,           // icon to text
		["lineGap"] = 4f,        // first line to second
		["bottom"] = 52f,        // the panel's bottom edge above the screen's
		["titleSize"] = 13f,     // first line: the name, then the description
		["bodySize"] = 11.5f,    // second line: the gamerscore, then the name
		["fill"] = 0.88f,        // the brown's alpha
		["ruleAlpha"] = 0.55f,   // the gold rule
		["ruleWidth"] = 1.5f,
	};

	private static int _dialsVersion;

	/// <summary>Sets one dial; false when there is no such dial. The next frame re-lays the toast.</summary>
	public static bool SetDial(string name, float value)
	{
		if (!Dials.ContainsKey(name))
		{
			return false;
		}

		Dials[name] = value;
		++_dialsVersion;
		return true;
	}

	/// <summary>The toast that is standing, for the debug channel; null outside a game window.</summary>
	public static Engine_AchievementToast? Current { get; private set; }

	public override void _EnterTree() => Current = this;

	public override void _ExitTree()
	{
		if (Current == this)
		{
			Current = null;
		}
	}

	/// <summary>The shipped fonts — the same theme the menus draw with.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>How many unlocks are waiting behind the one on screen.</summary>
	public int Pending => _pending.Count;

	/// <summary>One queued toast, with everything it needs already resolved.</summary>
	private readonly record struct Queued(
		uint AchievementId, string Name, string Description, uint Gamerscore, Texture2D? Icon);

	private readonly Queue<Queued> _pending = new();
	private Queued? _current;
	private float _age;

	/// <summary>
	/// The WINDOW, in display pixels — written every frame by the mount's owner, whose scale
	/// carries these pixels onto the HUD canvas. See <see cref="HudScale"/>.
	/// </summary>
	public Vector2 Canvas { get; set; } = new(1280f, 720f);

	private Engine_UiRoundedPanel? _panel;
	private MeshInstance3D? _icon;
	private StandardMaterial3D? _iconMaterial;
	private MeshInstance3D? _headline;
	private MeshInstance3D? _subtitle;

	/// <summary>What is currently laid out: the toast, its phase, the scale and the dials it was built at.</summary>
	private (uint Id, bool Description, float Scale, int Dials)? _built;

	/// <summary>The panel's laid-out size, in this node's own units.</summary>
	private Vector2 _panelSize;

	/// <summary>Toasts shown this session, and the last one — for the debug channel.</summary>
	public int Shown { get; private set; }

	public string LastShown { get; private set; } = "none";

	/// <summary>Whether one is on screen now, and how far through it is.</summary>
	public string State => _current is { } toast
		? $"showing {toast.AchievementId} '{toast.Name}' at {_age:0.0}/{ToastDurationSeconds:0}s, {_pending.Count} queued"
		: $"idle, {_pending.Count} queued";

	/// <summary>
	/// Queues a toast for an achievement WITHOUT unlocking it — the debug channel's way to look
	/// at the presentation alone. <paramref name="now"/> drops whatever is up and shows it at once.
	/// </summary>
	public void ShowNow(uint achievementId, bool now)
	{
		if (now)
		{
			_pending.Clear();
			_current = null;
			_age = 0f;
		}

		_pending.Enqueue(Resolve(achievementId));
	}

	private void Poll()
	{
		foreach (uint achievementId in Refii.Achievements.Engine_Achievements.TakeNewUnlocks())
		{
			_pending.Enqueue(Resolve(achievementId));
		}
	}

	private static Queued Resolve(uint achievementId)
	{
		Refii.Achievements.AchievementDefinition? definition =
			Refii.Achievements.Engine_Achievements.Definition(achievementId);

		string name = definition?.Name is { Length: > 0 } stated ? stated : "Achievement Unlocked";
		string description = definition?.UnlockedDescription ?? string.Empty;
		Texture2D? icon = Refii.Achievements.Engine_Achievements.Icon(achievementId);

		return new Queued(achievementId, name, description, definition?.Gamerscore ?? 0, icon);
	}

	private void Advance(float delta)
	{
		if (_current is null)
		{
			if (_pending.Count == 0)
			{
				return;
			}

			_current = _pending.Dequeue();
			_age = 0f;
			_built = null;
			++Shown;
			LastShown = $"{_current.Value.AchievementId} '{_current.Value.Name}'" +
				$"{(_current.Value.Icon is null ? " (NO ICON)" : string.Empty)}";
			GD.Print($"[achievements] toast up — {LastShown}, {_pending.Count} behind it");
		}

		// Not while nobody can see it: the UI viewport is concealed under a fullscreen movie.
		if (Held)
		{
			return;
		}

		// The original floors the step at 1/240s so a stalled frame cannot freeze the toast.
		_age += Mathf.Max(delta, 1f / 240f);
		if (_age >= ToastDurationSeconds)
		{
			_current = null;
			_age = 0f;
		}
	}

	/// <summary>Set by the owner while the UI is concealed; the toast's clock stands still.</summary>
	public bool Held { get; set; }

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("achievementtoast");
		Poll();
		Advance((float)delta);
		Render();
	}

	/// <summary>
	/// Toast pixels per HUD canvas unit. The mount above this node converts display pixels to
	/// canvas units; this is the inverse, so a size quoted in the HUD's units lands at the same
	/// height on screen whatever the window is.
	/// </summary>
	private float HudScale => Mathf.Max(Canvas.Y, 1f) / 720f * PieceScale;

	/// <summary>
	/// A mod's size for the toast (<c>ui/hud.json</c> <c>scale</c> on <c>widget.AchievementToast</c>,
	/// <see cref="Engine_HudWidgets"/>): drawn this much larger, still standing on its bottom edge.
	/// </summary>
	public float PieceScale { get; set; } = 1f;

	/// <summary>
	/// Puts the current toast on screen through the same glyph and quad path as every other owned
	/// surface — not `Control._Draw`: the shipped fonts are bitmap fonts with an atlas, laid out
	/// by <see cref="Engine_UiTheme.Glyphs"/> into a mesh.
	/// </summary>
	private void Render()
	{
		if (_current is not { } toast || Theme is null)
		{
			Visible = false;
			return;
		}

		Visible = true;

		float s = HudScale;
		float fadeIn = Mathf.Clamp(_age / FadeInSeconds, 0f, 1f);
		float fadeOut = Mathf.Clamp((ToastDurationSeconds - _age) / FadeOutSeconds, 0f, 1f);

		// Minimum, not a product: a toast shorter than both fades must never reach full opacity.
		float alpha = Mathf.Min(fadeIn, fadeOut);

		bool showingDescription = _age >= UnlockPhaseSeconds;
		float messageAlpha = Mathf.Clamp(
			showingDescription
				? (_age - UnlockPhaseSeconds) / MessageFadeSeconds
				: (UnlockPhaseSeconds - _age) / MessageFadeSeconds,
			0f, 1f);

		// LAID OUT ONCE PER PHASE, FADED EVERY FRAME. Layout — a panel, an icon and two text
		// meshes through the glyph path — used to be rebuilt every frame for ten seconds a toast;
		// only the opacity and the entry slide move between layouts.
		if (_built != (toast.AchievementId, showingDescription, s, _dialsVersion))
		{
			_built = (toast.AchievementId, showingDescription, s, _dialsVersion);
			Layout(toast, showingDescription, s);
		}

		// Canvas space is centre-origin with +Y up. The bottom edge is the fixed one: a taller
		// description grows the panel UP, and the entry slide rises into place from below.
		float slide = (1f - fadeIn) * BaseSlide * s;
		Position = new Vector3(
			0f, (-Canvas.Y * 0.5f) + (Dials["bottom"] * s) + (_panelSize.Y * 0.5f) - slide, 0f);

		// `Fit` pads the rectangle it is given; the panel's outer size is what was laid out.
		_panel!.Fit(
			new Rect2(
				(-_panelSize.X * 0.5f) + Engine_UiRoundedPanel.PaddingX,
				(-_panelSize.Y * 0.5f) + Engine_UiRoundedPanel.PaddingY,
				_panelSize.X - (2f * Engine_UiRoundedPanel.PaddingX),
				_panelSize.Y - (2f * Engine_UiRoundedPanel.PaddingY)),
			0f, alpha);

		if (_iconMaterial is not null)
		{
			_iconMaterial.AlbedoColor = new Color(1f, 1f, 1f, alpha);
		}

		Fade(_headline, alpha * messageAlpha);
		Fade(_subtitle, alpha * messageAlpha);
	}

	/// <summary>
	/// Builds the pieces for one toast in one phase, around the group's own origin: the name
	/// over its gamerscore, then the description over the name — first line gold, second cream.
	/// </summary>
	private void Layout(Queued toast, bool showingDescription, float s)
	{
		if (_panel is null)
		{
			_panel = new Engine_UiRoundedPanel { Name = "Panel" };
			AddChild(_panel);
		}

		Color gold = Engine_FrontEndMenuView.LabelColour;

		// The panel's radius is in ITS local units, which here are display pixels: stated, or
		// the corners come out half as round at 1440p as they are at 720p.
		_panel.Corner(Engine_UiRoundedPanel.HudCornerRadius * s);
		_panel.Style(
			Fill with { A = Dials["fill"] },
			gold with { A = Dials["ruleAlpha"] },
			Dials["ruleWidth"] * s);

		float width = Dials["width"] * s;
		float padX = Dials["padX"] * s;
		float padY = Dials["padY"] * s;
		float iconSize = Dials["icon"] * s;
		int titleSize = Mathf.Max(8, Mathf.RoundToInt(Dials["titleSize"] * s));
		int bodySize = Mathf.Max(8, Mathf.RoundToInt(Dials["bodySize"] * s));

		float left = -width * 0.5f;
		float textLeft = left + padX + iconSize + (Dials["gap"] * s);
		float textWidth = (width * 0.5f) - padX - textLeft;

		_headline ??= Attach("Headline");
		_subtitle ??= Attach("Subtitle");

		string first = showingDescription ? toast.Description : toast.Name;
		string second = showingDescription
			? toast.Name
			: toast.Gamerscore == 0 ? string.Empty : $"{toast.Gamerscore} G";

		// Painted OPAQUE: the fade is the material's `opacity`, written per frame by Fade. Both
		// lines wrap — a name a language makes long takes a second line rather than the edge.
		SetText(_headline, first, titleSize, gold with { A = 1f }, Vector3.Zero, textWidth, wrap: true);
		SetText(_subtitle, second, bodySize, SecondLine with { A = 1f }, Vector3.Zero, textWidth, wrap: true);

		// STACKED FROM WHAT WAS LAID OUT, and the panel sized to it. Fixed offsets are right for
		// one line and wrong for a description that wraps: its second line ran into the name
		// beneath it, and a third had nowhere to go.
		Aabb head = _headline.Mesh is null ? default : _headline.GetAabb();
		Aabb sub = _subtitle.Mesh is null ? default : _subtitle.GetAabb();
		float gap = sub.Size.Y > 0f ? Dials["lineGap"] * s : 0f;
		float block = head.Size.Y + gap + sub.Size.Y;
		_panelSize = new Vector2(width, Mathf.Max(block, iconSize) + (2f * padY));

		float top = block * 0.5f;
		_headline.Position = new Vector3(
			textLeft - head.Position.X, top - (head.Size.Y * 0.5f) - head.GetCenter().Y, 1f);
		_subtitle.Position = new Vector3(
			textLeft - sub.Position.X,
			top - head.Size.Y - gap - (sub.Size.Y * 0.5f) - sub.GetCenter().Y, 1f);

		_icon ??= Attach("Icon");
		_icon.Position = new Vector3(left + padX + (iconSize * 0.5f), 0f, 1f);
		if (toast.Icon is { } art)
		{
			_icon.Mesh = new QuadMesh { Size = new Vector2(iconSize, iconSize) };
			_iconMaterial = Flat(Colors.White);
			_iconMaterial.AlbedoTexture = art;
			_icon.MaterialOverride = _iconMaterial;
		}
		else
		{
			_icon.Mesh = null;
			_iconMaterial = null;
		}
	}

	/// <summary>One label's fade — the text shader's `opacity`, and its inline icons with it.</summary>
	private static void Fade(MeshInstance3D? label, float opacity)
	{
		if (label?.MaterialOverride is ShaderMaterial material)
		{
			material.SetShaderParameter("opacity", opacity);
			Engine_UiTheme.FadeInlineIcons(label, opacity);
		}
	}

	private MeshInstance3D Attach(string name)
	{
		var node = new MeshInstance3D { Name = name };
		AddChild(node);
		return node;
	}

	/// <summary>An unlit, two-sided, alpha-blended material — the canvas draws no lights.</summary>
	private static StandardMaterial3D Flat(Color colour) => new()
	{
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		AlbedoColor = colour,
	};

	/// <summary>Lays one string out in the shipped font, or clears the node when empty.</summary>
	private void SetText(MeshInstance3D node, string text, int pointSize, Color colour,
		Vector3 position, float boxWidth, bool wrap)
	{
		node.Position = position;
		if (text.Length == 0 || Theme is null)
		{
			node.Mesh = null;
			node.MaterialOverride = null;
			return;
		}

		Theme.Paint(
			node, text, pointSize, colour,
			horizontalAlign: 0, verticalAlign: 1,
			boxWidth: boxWidth, boxHeight: 0f, wrapMode: wrap ? 1 : 0);
	}
}
