namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The HUD timing meter's toggles and pip, from the guest's own meter object.
/// </summary>
/// <remarks>
/// The guest runs the meter (see <see cref="Refii.Host.HostBridge.GetExpressionMeter"/>); the
/// fill's position and size already arrive through MeterName registry 1 as `ExpressionFillPos`
/// — the sweet spot's CENTRE — and `ExpressionFillSize` — its WIDTH, both percent of the bar,
/// as the hit test `sub_823D21C0` reads them back (`pos < FillPos - FillSize*0.5` → miss).
/// What the host cannot read is registry 9, which the guest toggles: `ExpressionMeter` on
/// TurnOn/TurnOff, `ExpressionFill` on any fill start and off at TurnOff or the pip-mode finish,
/// `FlashExpressionMeter` while the band is high in a bar-job fill (`sub_823D22A8`).
///
/// The pip IS an element, named by binding rather than by node name: `sub_823D0E10` resolves
/// `HeroPip` / `HenchmanPip` / `BothPip` out of GUI registry 11, and `hud.bgf` authors those
/// three `PrimDescName`s on `white_pip`, `black_pip` and `blackwhite_pip` at opacity 0.
/// `sub_823D0F10` then swings the resolved quad round an arc of radius 160 — see
/// <see cref="Engine_HudView"/> for the instructions.
/// </remarks>
public static class Engine_UiHudExpressionMeter
{
	/// <summary>The `PrimDescName` bindings the guest resolves out of registry 11, not node names.</summary>
	public const string HeroPip = "HeroPip";
	public const string HenchmanPip = "HenchmanPip";
	public const string BothPip = "BothPip";

	/// <param name="mine">
	/// Whether the meter that is up belongs on THIS screen. The guest replicates an expression's
	/// meter to the peer: `sub_823D1A48` sends `off_820A6084` in a session (0x823d1b7c) and the
	/// peer's packet handler, case 136, starts its own meter from it — the shipped game shared
	/// one screen. Over the network each player has their own, so the caller says whether the
	/// armed pip is the local player's (hero pip A on the host, henchman pip B on a client) or
	/// whether both belong here (local co-op, a job's bar fill).
	/// </param>
	/// <param name="ownPip">
	/// Over the network, the local player's pip — 1 pip A (player one, the host), 2 pip B (player
	/// two, a joiner) — else 0. Two expressions at once arm both pips (`sub_823D1A48` keeps the
	/// running meter's pip and adds the new one), which the shared screen drew as `BothPip`; each
	/// player's own screen shows their own marker.
	/// </param>
	public static void Publish(Refii.Host.HostBridge.ExpressionMeter meter, Engine_HudView hud,
		bool mine = true, int ownPip = 0)
	{
		Engine_HudValues values = hud.Values;
		// NOT THE ON BYTE EITHER. Gating on the mode hid the meter for every emote; gating on
		// +0 hid it just as completely, because `sub_823D1A48` — the path a plain expression
		// takes — calls `TurnOnExpressionMeter` at 0x823d1c1c and clears +0 at 0x823d1c20, one
		// instruction later, having already zeroed the mode at its head. The state that says
		// the meter is open is the union the teardown clears; see `ExpressionMeter.Up`.
		//
		// The expression's release timing is the FILL, not the pip: `sub_823D1A48` reads
		// SweetSpotInitialSizePercentage, SweetSpotMinSizePercentage,
		// SweetSpotShrinkPixelsPerSecond and SweetSpotMovementPixelsPerSecond and hands them to
		// `sub_823D1F98`, which publishes ExpressionFillPos/Size — a sweet spot that moves and
		// shrinks. Modes 1 and 2 are the job pip.
		bool on = meter.Up && mine;
		State(values, "ExpressionMeter", on);

		float fillSize = values.Meter(Engine_HudValues.Handle.For("ExpressionFillSize")) ?? 0f;
		State(values, "ExpressionFill", on && (meter.Mode == 3 || meter.Live || fillSize > 0f));

		// The guest's own predicate, both halves: `sub_823D22A8` raises the flash only for
		// `band == 2 && mode == 3` at 0x823D23E0, so a plain emote — whose mode is always 0 —
		// never flashes.
		State(values, "FlashExpressionMeter", on && meter.Mode == 3 && meter.Band == 2);

		// The fill's colour rides registry 8 (`sub_82804CC0` per band change), unreadable here;
		// the band itself is on the meter object, and the guest's default band colours are
		// green, yellow, red (`sub_823D1C50`).
		hud.ExpressionBand = on ? meter.Band : -1;

		// THE PIP GOES WITH THE METER. The pip is lit and swung on its own (Engine_HudView.DrivePip),
		// not under the meter's toggle, so a meter that is not this screen's left its pip
		// travelling over the dark frame — the partner's emote showed its pip on both screens.
		if (!meter.PipRunning || !on)
		{
			hud.ExpressionPip = null;
			return;
		}

		// The guest's own selection order, which tests the HENCHMAN byte first: `sub_823D0E10`
		// reads `pip+1` at 0x823D0E20 and takes HeroPip whenever it is clear, whatever `pip+0`
		// says; only with it set does `pip+0` choose between BothPip and HenchmanPip.
		string binding = ownPip == 1 ? HeroPip
			: ownPip == 2 ? HenchmanPip
			: meter.ArmedA && meter.ArmedB ? BothPip : meter.ArmedB ? HenchmanPip : HeroPip;

		// DEGREES, stated: `(pos * 0.01 * 130) - 65` at 0x823D1050..0x823D1054, so 0 is the arc's
		// left end, 50 its apex and 100 its right end — a 130° sweep.
		hud.ExpressionPip = (binding, (Smoothed(meter) * 1.3f) - 65f);
	}

	private static float _lastPosition = float.NaN;
	private static ulong _lastChangeUsec;

	/// <summary>
	/// The pip between guest steps. The guest advances +16 by `speed · dt` once per 30 Hz step
	/// (`sub_822ACBF8`: bounce at 0 and 100 by negating the speed), and a display running
	/// faster sees it hop. Carry the guest's own speed forward from the last stated value for
	/// the time since it changed; a stated value always wins the moment it arrives.
	/// </summary>
	private static float Smoothed(Refii.Host.HostBridge.ExpressionMeter meter)
	{
		ulong now = Godot.Time.GetTicksUsec();
		if (meter.Position != _lastPosition)
		{
			_lastPosition = meter.Position;
			_lastChangeUsec = now;
			return meter.Position;
		}

		float elapsed = (now - _lastChangeUsec) * 1e-6f;
		if (elapsed > 0.1f)
		{
			return meter.Position;  // a held pip is held, not extrapolated
		}

		float ahead = meter.Position + meter.Speed * elapsed;
		return Godot.Mathf.Clamp(ahead, 0f, 100f);
	}

	private static void State(Engine_HudValues values, string name, bool on) =>
		values.StateVisible(Refii.Core.Engine_GameHash.Fnv1(name), on);
}
