namespace Refii.Ui;

/// <summary>
/// The HUD TopBox's slide toggles and fader, re-derived from the box the host holds.
/// </summary>
/// <remarks>
/// The guest's TopBox manager (`*(*(GUI+32)+88)`) still runs — a taken `GUI.*` call is not
/// suppressed — so its publisher `sub_821787D0` writes the strings into TextName registry 6 and
/// the hold progress into MeterName registry 1, both of which the host reads. What it cannot
/// read are registry 9 (SlideToggleName) and registry 3 (SlideFader2Name), and those carry the
/// box's visibility: `TopBox`, `ButtonOnly`, `TopTitle`, `TopMain`, the six button toggles and
/// the fade. This states exactly the toggles that publisher computes, from the same record —
/// read out of the guest's manager (it also holds records no Lua native made, the quest
/// objective for one) — and the held flags from the pad,
/// which the guest's `sub_8230D408` flips on press (phase 13) and release (phase 15).
///
/// Channels: style 1 → HeroJobBoxes (1), style 2 → QuestAcceptanceBoxes (2), else ScriptInfo
/// (3); the front record is the LAST of the LOWEST non-empty channel (`sub_8230D4F0`).
/// </remarks>
public static class Engine_UiHudTopBox
{
	private static bool _wasUp;

	private static uint _shown;

	/// <param name="front">The manager's front record, read out of the guest.</param>
	/// <param name="strings">Babel, for `TextManager_HasKey` on the title tag.</param>
	public static void Publish(
		Refii.Host.HostBridge.TopBoxFront front, bool aHeld, bool yHeld,
		Engine_HudValues values, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (!front.Known)
		{
			return;
		}

		bool up = front.Present;
		State(values, "TopBox", up);
		if (up != _wasUp)
		{
			// SlideFader2 `TopBox`: 2 in, 3 out fast — stated as the SlideFaderName index the
			// fold shifts by one for the second channel.
			values.StateFade(Refii.Core.Engine_GameHash.Fnv1("TopBox"), up ? 1 : 2);
			_wasUp = up;
		}

		if (!up)
		{
			foreach (string name in new[]
				{
					"ButtonOnly", "TopTitle", "TopMain", "RightTopButtonVisible",
					"LeftTopButtonVisible", "RightTopButtonIsY", "RightTopButtonMeterVisible",
					"LeftTopButtonMeterVisible", "RightTopButtonHeld", "LeftTopButtonHeld",
				})
			{
				State(values, name, false);
			}

			_shown = 0u;
			return;
		}

		// CHANNEL 7 IS THE INTERACTION PROMPT AND HAS NO TEXT. The publisher returns for it
		// before any text work (0x82178978): the box is up, the targeted-query slide is set
		// (`sub_823FD8E8(+112)`), and TopTitle/TopMain/ButtonOnly are never stated. Composing
		// them here from the registry is what put the previous box's body under a vendor's
		// name.
		if (front.Channel == 7)
		{
			foreach (string name in new[]
				{
					"ButtonOnly", "TopTitle", "TopMain", "RightTopButtonVisible",
					"LeftTopButtonVisible", "RightTopButtonMeterVisible", "LeftTopButtonMeterVisible",
				})
			{
				State(values, name, false);
			}

			values.StateText(Refii.Core.Engine_GameHash.Fnv1("TopMain"), string.Empty);
			if (_shown != front.NameHash)
			{
				_shown = front.NameHash;
				Godot.GD.Print($"[hud-topbox] front record 0x{front.NameHash:X8} on channel 7 — interaction prompt, no text");
			}

			return;
		}

		// `TextManager_HasKey(title tag)` decides the title; the body is the FRONT RECORD's own
		// provider, composed here the way `sub_821787D0` composes it. Registry 6 is not read
		// for it: a record with no title and no body only sets ButtonOnly and never rewrites
		// TopMain, so the registry string outlives its record — "Poppy the Stall Vendor" over
		// "Find the abbot in Oakfield." was the previous box's body under the new title.
		bool hasTitle = strings is not null && strings.TryGetString(front.TitleTag, out _);
		string body = ComposeBody(Refii.Host.HostBridge.GetTopBoxBody(), strings);
		bool buttonOnly = !hasTitle && body.Length == 0;
		State(values, "ButtonOnly", buttonOnly);
		State(values, "TopTitle", !buttonOnly && hasTitle);
		State(values, "TopMain", !buttonOnly && body.Length != 0);
		values.StateText(Refii.Core.Engine_GameHash.Fnv1("TopMain"), body);

		bool showA = front.ShowA;
		bool showY = front.ShowY;
		bool holdA = front.HoldA;
		bool holdY = front.HoldY;
		State(values, "RightTopButtonVisible", showA || showY);
		State(values, "LeftTopButtonVisible", showA && showY);
		State(values, "RightTopButtonIsY", !showA && showY);
		State(values, "RightTopButtonMeterVisible", (showA && holdA) || (!showA && showY && holdY));
		State(values, "LeftTopButtonMeterVisible", showA && showY && holdY);
		State(values, "RightTopButtonHeld", showA ? aHeld : showY && yHeld);
		State(values, "LeftTopButtonHeld", showA && showY && yHeld);

		if (_shown != front.NameHash)
		{
			_shown = front.NameHash;
			Godot.GD.Print($"[hud-topbox] front record 0x{front.NameHash:X8} on channel {front.Channel}" +
				$" — title {(hasTitle ? "yes" : "no")}, A {(showA ? (holdA ? "hold" : "press") : "off")}," +
				$" Y {(showY ? (holdY ? "hold" : "press") : "off")}, lifetime {front.LifetimeMs} ms");
		}
	}

	/// <summary>The body a provider object states, by its class — see RefiiHostGetTopBoxBody.</summary>
	private static string ComposeBody(Refii.Host.HostBridge.TopBoxBody body, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (!body.Known || !body.Present)
		{
			return string.Empty;
		}

		string Babel(uint key) =>
			strings is not null && strings.TryGetString(key, out string text) ? text : string.Empty;

		switch (body.Class)
		{
			case 0:
				return Babel(body.Key);
			case 1:
				return Babel(body.Key)
					.Replace("%1", ((int)body.Second).ToString())
					.Replace("%2", ((int)body.Third).ToString())
					.Replace("%3", ((int)body.Fourth).ToString());
			case 2:
				return Babel(body.Key)
					.Replace("%s", Babel(body.Second))
					.Replace("%1", Babel(body.Second))
					.Replace("%2", Babel(body.Third));
			case 3:
				return body.Literal;
			default:
				return string.Empty;
		}
	}

	private static void State(Engine_HudValues values, string name, bool on) =>
		values.StateVisible(Refii.Core.Engine_GameHash.Fnv1(name), on);
}
