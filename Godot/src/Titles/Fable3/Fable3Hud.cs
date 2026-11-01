namespace Refii.Titles.Fable3;

using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's in-game HUD as far as its bridge states it: the screen fade and the subtitles.
/// </summary>
/// <remarks>
/// THE SUBTITLE IS THE GUEST'S PAGE. The subtitler formats each line (speaker name included),
/// wraps it to its box and splits it into pages of four lines, then writes the current page into
/// the HUD text registry under FNV-1("Subtitles") and clears it to an empty string when the line
/// ends (sub_825E61F8, sub_825E5E90, sub_825E6080). So the text shown here is exactly the page the
/// guest is showing, for exactly as long; only the placement is ours — centred above the bottom
/// edge, scaled with the window so it stays legible.
/// </remarks>
public partial class Fable3Hud : Control
{
	private static readonly Engine_HudValues.Handle Subtitles = Engine_HudValues.Handle.For("Subtitles");

	/// <summary>Reference height the guest lays its HUD out in.</summary>
	private const float ReferenceHeight = 720f;

	/// <summary>The subtitle block's lowest baseline, in reference pixels above the bottom edge.</summary>
	private const float BottomMargin = 64f;

	private Engine_UiScreenFade _fade = null!;
	private UiFont? _font;
	private string _culture = string.Empty;
	private string _subtitle = string.Empty;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		_fade = new Engine_UiScreenFade { Name = "ScreenFade" };
		AddChild(_fade);
	}

	public override void _Process(double delta)
	{
		_fade.Drive();

		if (_font is null || _culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			_font = Engine_UiFontLibrary.Load(Refii.Core.Engine_GameData.Resolve(), _culture).Get(UiFontBucket.Normal);
		}

		string subtitle = Engine_UiSource.Current.GetHudTexts().TryGetValue(Subtitles.Hash, out string? text)
			? text
			: string.Empty;
		if (subtitle != _subtitle)
		{
			_subtitle = subtitle;
			QueueRedraw();
		}
	}

	public override void _Draw()
	{
		if (_font is null || _subtitle.Length == 0)
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / ReferenceHeight;
		string[] lines = _subtitle.Split('\n');
		float lineHeight = _font.Font.LineHeight * scale;
		float y = size.Y - (BottomMargin * scale) - (lines.Length * lineHeight);
		foreach (string line in lines)
		{
			float width = Engine_UiCanvasText.Measure(_font, line) * scale;
			Engine_UiCanvasText.DrawLine(this, _font, line, new Vector2((size.X - width) * 0.5f, y), scale, Colors.White);
			y += lineHeight;
		}
	}
}
