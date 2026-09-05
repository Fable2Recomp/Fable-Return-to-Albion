namespace Refii.Ui;

/// <summary>
/// The HUD timing meter's toggles and pip, from the guest's own meter object.
/// </summary>
/// <remarks>
/// The guest runs the meter (see <see cref="Refii.Host.HostBridge.GetExpressionMeter"/>); the
/// fill's position and size already arrive through MeterName registry 1 as `ExpressionFillPos`
/// and `ExpressionFillSize`. What the host cannot read is registry 9, which the guest toggles:
/// `ExpressionMeter` on TurnOn/TurnOff, `ExpressionFill` on any fill start and off at TurnOff or
/// the mode-2 judge, `FlashExpressionMeter` while the band is high in a bar-job fill
/// (`sub_823D2394`). The pip is not an element: `sub_823D0E10` places a quad at
/// `x = (pos · 0.01 · 130 − 65) · scale` on the transform of the element bound `HeroPip`,
/// `HenchmanPip` or `BothPip` (`PrimDescName` on `white_pip`, `black_pip`, `blackwhite_pip`,
/// authored at opacity 0) — the view shows that anchor's own art at the offset.
/// </remarks>
public static class Engine_UiHudExpressionMeter
{
	public const string HeroPip = "white_pip";
	public const string HenchmanPip = "black_pip";
	public const string BothPip = "blackwhite_pip";

	public static void Publish(Refii.Host.HostBridge.ExpressionMeter meter, Engine_HudView hud)
	{
		Engine_HudValues values = hud.Values;
		// The ON byte, not the mode: `TurnOnExpressionMeter` (`sub_823D12F0`) sets +0 and toggles
		// the meter's slide with the mode still 0; an expression charges with a static or
		// dynamic fill (`sub_823D1780`/`sub_823D18F0`) before any pip starts. Gating on the mode
		// hid the meter for every emote.
		bool on = meter.Known && meter.On;
		State(values, "ExpressionMeter", on);

		float fillSize = values.Meter(Engine_HudValues.Handle.For("ExpressionFillSize")) ?? 0f;
		State(values, "ExpressionFill", on && (meter.Mode == 3 || meter.Dynamic || fillSize > 0f));
		State(values, "FlashExpressionMeter", on && meter.Mode == 3 && meter.Band == 2);

		// The fill's colour rides registry 8 (`sub_82804CC0` per band change), unreadable here;
		// the band itself is on the meter object, and the guest's default band colours are
		// green, yellow, red (`sub_823D1C50`).
		hud.ExpressionBand = on ? meter.Band : -1;

		if (!meter.PipRunning)
		{
			hud.ExpressionPip = null;
			return;
		}

		string anchor = meter.ArmedA && meter.ArmedB ? BothPip : meter.ArmedB ? HenchmanPip : HeroPip;
		hud.ExpressionPip = (anchor, Smoothed(meter) * 1.3f - 65f);
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
