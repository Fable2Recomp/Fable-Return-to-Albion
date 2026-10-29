namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The targeted-citizen readout's slide set and toggles, re-derived from the guest's own record.
/// </summary>
/// <remarks>
/// `sub_82176808` rebuilds the record at `*(*(GUI+32)+112)` every HUD tick and `sub_823FD410`
/// publishes it. Registries 1 (`TargetedGift`, `TargetedDivorce`, `TargetedMarry`,
/// `TargetedQueryOpinion`) and 6 (`TargetedQueryTitle`) are walkable and already arrive; 9 and 10
/// are WRITE-THROUGH — `sub_82426BA8` dispatches into the slide controller and stores nothing —
/// so they are restated here from the same fields the guest's publisher reads.
///
/// THE OPINION SLIDER IS REGISTRY 10, and that is the whole reason this file exists.
/// `LoveSlider` carries both `MeterName` and `SlideSetName` for `TargetedQuery`: the meter
/// scrubs the pip along the authored `position.x` track, and the SLIDE SET picks which readout
/// — it raises `grp` from its authored opacity 50 to 100 and activates one mood-face pair out of
/// three overlaid at the same place. With no registry 10 the slider drew at half opacity with
/// all three pairs on top of each other, which is exactly what shipped.
/// </remarks>
public static class Engine_UiHudTargetedQuery
{
	private static bool _wasUp;

	private static bool _known;

	public static void Publish(Refii.Host.HostBridge.TargetedQuery query, Engine_HudValues values)
	{
		if (!query.Known)
		{
			return;
		}

		// THE GUEST'S OWN RAISE BYTE, not a channel heuristic. `sub_823FD8E8` stores 1 at
		// 0x823fd910 and `sub_823FD9B0` stores 0 — and `sub_823FD8E8`'s single caller is
		// `sub_821787D0` at 0x8217898c, gated on the TopBox front record being channel 7. So
		// this byte is both authoritative and complete, which the channel-7 proxy this replaces
		// was not: that proxy also LOWERED the readout on every non-7 channel, which the guest
		// never does.
		bool up = query.Present;
		State(values, "TargetedQuery", up);
		if (up != _wasUp || !_known)
		{
			// SlideFader2 `TargetedQuery`: 2 in (0x823fd954), 3 out (0x823fda1c). The fold adds
			// the second channel's shift, so state the first channel's index here.
			values.StateFade(Refii.Core.Engine_GameHash.Fnv1("TargetedQuery"), up ? 1 : 2);
			_wasUp = up;
			_known = true;
		}

		// The four registry-9 toggles, from the record's own bytes. THE NAMES ARE CROSSED
		// against the obvious reading: block+36 publishes under `TargetedDivorce` (0x823fd884)
		// and block+44 under `TargetedMarry` (0x823fd8c4), and the predicates agree —
		// `sub_822684A8` asks whether this person is already in the hero's family, `sub_8226B0D8`
		// runs the marriage-eligibility test. The bridge hands them over already untangled.
		State(values, "TargetedGift", query.Gift);
		State(values, "TargetedQueryButton", query.Button);
		State(values, "TargetedDivorce", query.Divorce);
		State(values, "TargetedMarry", query.Marry);

		// REGISTRY 10, VERBATIM. The guest publishes `axis + 1` (0x82176f10): 1 love, 2 fear,
		// 3 ugly, matching `LoveSlider`'s three slide names. Axes 3 and 4 state 4 and 5, which
		// that element has no slides for — state them anyway, because the guest does and the
		// resolver no-ops an index the owner does not carry. Clamping would be a host heuristic.
		if (query.Entity != 0u && query.SlideSet > 0)
		{
			values.StateSlideSet(Refii.Core.Engine_GameHash.Fnv1("TargetedQuery"), query.SlideSet);
		}
	}

	private static void State(Engine_HudValues values, string name, bool on) =>
		values.StateVisible(Refii.Core.Engine_GameHash.Fnv1(name), on);
}
