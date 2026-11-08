namespace Refii.Titles.Fable3;

using Refii.Ui;

/// <summary>
/// FABLE 3'S LAID-OUT HUD TEXT BOXES that Godot stacks (<see cref="Engine_UiTextBoxStack"/>): the
/// corner counters and the subtitles. Pure — the tests hold the figures against hud.bgf.
/// </summary>
/// <remarks>
/// <para>Each box is a `CHUDTextBox` record (ctor `CHUDTextBox_p_ctor` 0x824B4238, byte +8 cleared
/// at 0x824B429C so the fit places it, never at its bare position), filed by the HUD's build:
/// `Subtitles` by sub_825C18C0 (0x825C1960, `dword_8355906C`), `Corner` by sub_825C1B20
/// (0x825C1C08, `dword_8355908C`) — FNV-1 of the names (initialisers 0x831B9BDC / 0x831B9DDC).</para>
///
/// <para>THE LINES. The subtitler states one line, the page, under TextName `Subtitles`
/// (sub_825E5E90 0x825E5F1C → 0x825E5F60; its clear sub_825E6080 the empty line, 0x825E61A0 →
/// 0x825E61D8). The counter tick sub_825E7C28 states one line per SHOWN counter record (its +4,
/// sub_825E7278 0x825E72BC), the record's own TextName `Counter<n>` (+20, 0x825E72F4), in record
/// order (0x825E7CDC); no shown counter leaves the box as it was (sub_82920278 returns at
/// 0x829202B4 before the fit). Every caller pads 0 (f1 = flt_8206C95C).</para>
///
/// <para>THE SIZE RANGES are the box element's size with its meters at 100 and at 0 (sub_8227CC70:
/// meters 100 at 0x8227CCF0 / 0x8227CD0C, max kept at 0x8227CD70 / 0x8227CD74; meters 0 at
/// 0x8227CD80 / 0x8227CD9C, min at 0x8227CDEC / 0x8227CDF0), the size being 100 × the scale
/// product up the chain (sub_82818A60, element first, every element with a scale; the 100 is
/// the splat at 0x8227CD38). The meters scrub their own `Slide1` (flags 13): CornerX [424] the
/// `BoxMiddle` [425] scale.x 1.49 → 4.65, CornerY [425] the `Bar` [426] scale.y 0.3 → 1.98;
/// SubtitlesX [503] the `Component` [504] scale.x 0.2 → 1.2, SubtitlesY [504] the `Bar` [505]
/// scale.y 0.1 → 0.5. The Subtitles chain's `Group` [501] is at its widescreen width 1.12 (its
/// adapter [502]), since the 360 runs the adapters on a widescreen set: `GUI:SetToDisplayInWidescreen()`
/// at presentation init (0x825C7CB8..0x825C7CD8) and the HUD's own `setWidescreenMode` raise
/// (sub_825C1B20 0x825C1B40..0x825C1BA0) both test `dword_8330F054`, the `fIsWideScreen` of the
/// XVIDEO_MODE that `XGetVideoMode(&unk_8330F048)` fills (0x82D4A2C8).</para>
/// </remarks>
public static class Fable3TextBoxes
{
	/// <summary>The counters: `Layer.CornerBox…BoxMiddle.Bar` [426], lines at its position + 37.</summary>
	public static readonly Engine_UiTextBoxStack.Box Corner = new(
		"Corner", false,
		new Engine_UiTextBoxStack.Range(0.79f * 1.49f * 100f, 0.79f * 4.65f * 100f),
		new Engine_UiTextBoxStack.Range(0.3f * 100f, 1.98f * 100f),
		0f);

	/// <summary>The subtitles: `Layer.Subtitles…Component.Bar` [505], lines centred on its position.</summary>
	public static readonly Engine_UiTextBoxStack.Box Subtitles = new(
		"Subtitles", true,
		new Engine_UiTextBoxStack.Range(0.2f * 8.3f * 1.12f * 100f, 1.2f * 8.3f * 1.12f * 100f),
		new Engine_UiTextBoxStack.Range(0.1f * 3f * 100f, 0.5f * 3f * 100f),
		0f);

	/// <summary>The boxes Godot stacks.</summary>
	public static readonly Engine_UiTextBoxStack.Box[] Stacked = { Corner, Subtitles };
}
