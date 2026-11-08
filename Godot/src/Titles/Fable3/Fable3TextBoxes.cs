namespace Refii.Titles.Fable3;

using System.Collections.Generic;
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

	/// <summary>
	/// The meters a box's fit states its size on (sub_82921110 at 0x829205C8): the X meter from its
	/// width, the Y meter from its height — hud.bgf's `MeterName` bindings `CornerX` [424] /
	/// `CornerY` [425], `SubtitlesX` [503] / `SubtitlesY` [504].
	/// </summary>
	public static (string X, string Y) MetersOf(Engine_UiTextBoxStack.Box box) =>
		box.Name == Subtitles.Name ? ("SubtitlesX", "SubtitlesY") : ("CornerX", "CornerY");

	/// <summary>
	/// The percent sub_82921110 states for a size: <c>(size − min) / (max − min) × 100</c> (a range
	/// with max = min states nothing, `+20 != +12`), held to 0..100 by the meter's store sub_8291F8C0
	/// (the `fsel` pair over the {1, −1} table unk_82073D10).
	/// </summary>
	public static float? MeterPercent(Engine_UiTextBoxStack.Range range, float size) =>
		range.Max == range.Min ? null : System.Math.Clamp((size - range.Min) / (range.Max - range.Min) * 100f, 0f, 100f);

	/// <summary>
	/// A meter's value as one box's fit sees it — ONE FIT LATE. The fit states its meters through
	/// sub_8291F8C0, which only stores the target (+28) with no rate (+12 = 0, +24 = 0); the registry
	/// tick sub_8221D780 applies it on its next run (rate 0 takes the target, 0x8221D8C4, then
	/// sub_8291F6E8's SetTime at 0x8221D8D0), and the element's tracks follow its time after that. The
	/// next fit's chain getter sub_82818760 reads the attribute values (sub_82818688 → sub_821C9ED0),
	/// so it places this box's lines on the track at the PREVIOUS fit's meter. A refit that states no
	/// meter (the corner after each subtitle change, sub_825E7BA0 → sub_82920610 at 0x825E7BF8, from
	/// 0x825E5F64 / 0x825E611C / 0x825E61DC) sees the latest one.
	/// </summary>
	public sealed class MeterLag
	{
		private float? _applied;

		/// <summary>The meter the last fit placed by; null before any fit applied one.</summary>
		public float? AtFit { get; private set; }

		/// <summary>A fit that states <paramref name="percent"/>: it places by the one before.</summary>
		public void Laid(float? percent)
		{
			AtFit = _applied;
			if (percent is not null)
			{
				_applied = percent;
			}
		}

		/// <summary>A refit that states no meter: it places by the last one stated.</summary>
		public void Refit() => AtFit = _applied;
	}

	/// <summary>
	/// The position tracks a meter element's own slide (relative slide 1, `Slide1`, flags 13) plays on
	/// its time: per element, the `position.x` and `position.y` tracks it switches on (CornerX: the
	/// `BoxMiddle` [425] x −152 → −153, track 446).
	/// </summary>
	public static Dictionary<int, (BgfAnimation? X, BgfAnimation? Y)> PositionTracks(Engine_BgfDocument document, int meter)
	{
		var tracks = new Dictionary<int, (BgfAnimation? X, BgfAnimation? Y)>();
		int relative = 0;
		foreach (BgfSlide slide in document.Slides)
		{
			if (slide.OwnerElement != meter || relative++ != 1)
			{
				continue;
			}

			foreach (BgfSlideEntry entry in slide.Entries)
			{
				foreach (int index in entry.ActivatedAnimations ?? System.Array.Empty<int>())
				{
					if (index < 0 || index >= document.Animations.Length)
					{
						continue;
					}

					BgfAnimation track = document.Animations[index];
					bool x = track.AttributeId == PositionX;
					if (!x && track.AttributeId != PositionY)
					{
						continue;
					}

					tracks.TryGetValue(track.ElementOrdinal, out (BgfAnimation? X, BgfAnimation? Y) pair);
					tracks[track.ElementOrdinal] = x ? (track, pair.Y) : (pair.X, track);
				}
			}
		}

		return tracks;
	}

	/// <summary>
	/// A meter track's value at <paramref name="percent"/>: the meter's time is percent × 10 cut to a
	/// whole tick (sub_8291F6E8: `fmuls` by flt_820E7004 0x8291F714, `fctiwz` 0x8291F718).
	/// </summary>
	public static float? At(BgfAnimation? track, float percent) => track?.Evaluate((int)(percent * 10f));

	private static readonly uint PositionX = Refii.Core.Engine_GameHash.AttributeId("position.x");
	private static readonly uint PositionY = Refii.Core.Engine_GameHash.AttributeId("position.y");
}
