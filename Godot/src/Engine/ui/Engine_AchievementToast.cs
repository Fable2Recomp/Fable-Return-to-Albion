using System.Collections.Generic;
using Godot;


namespace Refii.Ui;

/// <summary>
/// The achievement toast, drawn by Godot — the presentation only, ported from
/// `achievement_imgui.cpp`. The achievement runtime stays native: what crosses the bridge is the
/// model, an unlock with its definition and icon.
///
/// Every number below is `AchievementToastDialog::DrawCurrentToast`'s:
///
/// <list type="bullet">
/// <item>Two phases, ten seconds — five for the unlock, five for the description, cross-fading
/// over <see cref="MessageFadeSeconds"/>. The toast fades in over 0.22s and out over 0.55s,
/// combined by MINIMUM so a short toast never reaches full opacity.</item>
/// <item>Entry offset <c>(1 - fadeIn) * 22</c> units downward, so it rises into place.</item>
/// <item>Scale <c>clamp(height / 1080, 0.85, 2.0)</c>, multiplying every "base" dimension.</item>
/// </list>
///
/// The queue is a SEQUENCE, not a pop: `Engine_UiSource.Current.GetAchievementUnlocks` asks for
/// everything newer than the last sequence seen, so a reload or a missed frame loses nothing.
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

	/// <summary>Geometry at a 1080-tall display; everything scales from here.</summary>
	public const float BaseToastHeight = 106.0f;
	public const float BaseBottomMargin = 78.0f;
	public const float BaseIconRadius = 23.0f;
	public const float BaseIconRingRadius = 31.0f;
	public const float BaseSlide = 22.0f;

	/// <summary>The width band: never wider than 486, never narrower than 340, inset 48.</summary>
	public const float BaseMaxWidth = 486.0f;
	public const float BaseMinWidth = 340.0f;
	public const float BaseSideInset = 48.0f;

	/// <summary>The frame's inset from the toast, and the parchment's from the frame.</summary>
	public const float BaseFrameInset = 8.0f;
	public const float BaseParchmentInset = 5.0f;
	public const float BaseCornerRadius = 4.0f;

	/// <summary>The backing fill the original draws under the frame art — RGBA(41,24,12,170).</summary>
	public static readonly Color BackingColour = new(41f / 255f, 24f / 255f, 12f / 255f, 170f / 255f);

	/// <summary>The shipped look — the same theme the menus draw with.</summary>
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
	/// The canvas this is laid out in — the same 1280x720 space every presentation uses. The
	/// original scales against the DISPLAY height; here the canvas is fixed and Godot fits it to
	/// the window, so the scale sits at the clamp floor (0.85).
	/// </summary>
	public Vector2 Canvas { get; set; } = new(1280f, 720f);

	private MeshInstance3D? _backing;
	private MeshInstance3D? _icon;
	private MeshInstance3D? _headline;
	private MeshInstance3D? _subtitle;

	/// <summary>Takes whatever the guest has unlocked since the last look.</summary>
	/// <summary>
	/// New unlocks come from <see cref="Refii.Achievements.Engine_Achievements"/>, which owns the
	/// store: an unlock loaded from disk is not news, and a write the guest repeats for an
	/// achievement already earned never reaches here.
	/// </summary>
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
		}

		// The original floors the step at 1/240s so a stalled frame cannot freeze the toast.
		_age += Mathf.Max(delta, 1f / 240f);
		if (_age >= ToastDurationSeconds)
		{
			_current = null;
			_age = 0f;
		}
	}

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("achievementtoast");
		Poll();
		Advance((float)delta);
		Render();
	}

	/// <summary>The display scale — every base dimension is multiplied by this.</summary>
	private float Scale => Mathf.Clamp(Canvas.Y / 1080f, 0.85f, 2.0f);

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

		float s = Scale;
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

		float height = BaseToastHeight * s;
		float width = Mathf.Min(BaseMaxWidth * s,
			Mathf.Max(BaseMinWidth * s, Canvas.X - (BaseSideInset * s)));
		float slide = (1f - fadeIn) * BaseSlide * s;

		// Canvas space is centre-origin with +Y up, so the bottom margin subtracts and the
		// entry slide — downward in the original's screen space — subtracts too.
		float centreY = (-Canvas.Y * 0.5f) + (BaseBottomMargin * s) + (height * 0.5f) - slide;

		_backing ??= Attach("Backing");
		_backing.Mesh = new QuadMesh
		{
			Size = new Vector2(width - (BaseFrameInset * s * 2f), height - (BaseFrameInset * s * 2f)),
		};
		_backing.Position = new Vector3(0f, centreY, 0f);
		_backing.MaterialOverride = Flat(BackingColour with { A = BackingColour.A * alpha });

		float ringRadius = BaseIconRingRadius * s;
		float iconRadius = BaseIconRadius * s;
		float iconX = (-width * 0.5f) + (BaseFrameInset * s) + (BaseParchmentInset * s) + ringRadius;

		_icon ??= Attach("Icon");
		_icon.Position = new Vector3(iconX, centreY, 1f);
		if (toast.Icon is { } art)
		{
			_icon.Mesh = new QuadMesh { Size = new Vector2(iconRadius * 2f, iconRadius * 2f) };
			var material = Flat(new Color(1f, 1f, 1f, alpha));
			material.AlbedoTexture = art;
			_icon.MaterialOverride = material;
		}
		else
		{
			_icon.Mesh = null;
		}

		// Phase one names the achievement, phase two describes it; the two cross-fade via
		// `messageAlpha` and the strings swap.
		float textLeft = iconX + ringRadius + (BaseParchmentInset * s * 2f);
		float textWidth = (width * 0.5f) - textLeft - (BaseFrameInset * s) -
			(BaseParchmentInset * s);

		_headline ??= Attach("Headline");
		_subtitle ??= Attach("Subtitle");

		SetText(_headline, showingDescription ? toast.Description : toast.Name,
			HeadlineSize, new Color(1f, 224f / 255f, 128f / 255f, alpha * messageAlpha),
			new Vector3(textLeft, centreY + (10f * s), 1f), textWidth, wrap: showingDescription);

		string subtitle = showingDescription
			? toast.Name
			: toast.Gamerscore == 0 ? string.Empty : $"{toast.Gamerscore}G";

		SetText(_subtitle, subtitle, SubtitleSize,
			new Color(0.86f, 0.80f, 0.66f, alpha * messageAlpha),
			new Vector3(textLeft, centreY - (18f * s), 1f), textWidth, wrap: false);
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

	/// <summary>Point sizes at 1080; scaled with everything else.</summary>
	public const int HeadlineSize = 20;
	public const int SubtitleSize = 15;
}
