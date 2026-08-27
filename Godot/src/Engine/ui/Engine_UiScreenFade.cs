using Godot;

namespace Refii.Ui;

/// <summary>
/// The screen fade, drawn. The guest's own fade is a render-command submission
/// (`sub_822C7A80`) and the renderer is off, so nothing else puts it on screen.
///
/// The guest owns the state: it starts the fade and advances it at 15 Hz. This never writes —
/// snapping the progress from the host would hang a level change, whose only way out is
/// `CmdLevelChange_Update`'s lock. What the guest draws is a full-screen axis-aligned quad,
/// alpha blended, depth test off, white source modulated by the lerped colour, so a viewport
/// `ColorRect` is the same thing. It sits at <see cref="LayerIndex"/>: above <c>GameUi</c>'s
/// layer 5, below the loading screen at 8 and videos at 10.
/// </summary>
public sealed partial class Engine_UiScreenFade : Node
{
	/// <summary>Over the UI, under the loading screen. See the class note.</summary>
	public const int LayerIndex = 7;

	private CanvasLayer? _layer;
	private ColorRect? _rect;

	/// <summary>What the last poll saw, so the report is one line per change.</summary>
	private string _reported = string.Empty;

	/// <summary>True while the fade is covering anything — for a caller that wants to know.</summary>
	public bool IsCovering { get; private set; }

	public override void _Ready()
	{
		_layer = new CanvasLayer { Name = "FadeLayer", Layer = LayerIndex };
		AddChild(_layer);

		// Full-rect anchors, not a size set once: the guest states the quad as the whole screen
		// every frame (`0,0` to `*(0x834A5FE8)`, `*(0x834A5FEC)`), so it must track a resize.
		_rect = new ColorRect
		{
			Name = "ScreenFade",
			Color = new Color(0f, 0f, 0f, 0f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(_rect);
	}

	/// <summary>
	/// One frame. Reads the guest's fade and puts the colour up. Per frame, not on the sampling
	/// cadence — a fade is a continuous ramp and would stair-step otherwise.
	/// </summary>
	public void Drive()
	{
		if (_rect is null)
		{
			return;
		}

		Engine_UiModel.ScreenFade fade = Engine_UiSource.Current.GetScreenFade();

		// `Covers` is the guest's own early-out; unknown (no GUI object yet) blanks the same way.
		bool covering = fade.Known && fade.Covers;

		_rect.Color = covering ? fade.Colour : new Color(0f, 0f, 0f, 0f);
		_rect.Visible = covering;
		IsCovering = covering;

		// Report the lock too: `CmdLevelChange_Update` proceeds on `!IsFading() || locked`, so it
		// is what separates a wedged level change from a fade still running.
		string now = covering
			? $"{fade.Colour.R:0.00},{fade.Colour.G:0.00},{fade.Colour.B:0.00} " +
				$"a{fade.Colour.A:0.00} t{fade.Progress:0.00}{(fade.Locked ? " LOCKED" : string.Empty)}"
			: fade.Known ? "clear" : "no fade object";

		if (_reported != now)
		{
			_reported = now;
			GD.Print($"[screen-fade] {now}");
		}
	}
}
