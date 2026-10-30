namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Host;
using Refii.Ui;

/// <summary>
/// Fable 3's UI statements, for the shared UI: what its host bridge states, forwarded. Every
/// member here is an export the bridge answers (Fable3/src/hostbridge/src/hud_feed.cpp); the rest
/// keep the base answer of "nothing" until Fable 3 states them.
/// </summary>
public sealed class Fable3UiSource : Engine_UiSource
{
	public static void Install() => Current = new Fable3UiSource();

	public override bool IsLive => true;

	public override Engine_UiModel.ScreenFade GetScreenFade() => HostBridge.GetScreenFade();

	public override Dictionary<uint, float> GetHudMeters() => HostBridge.GetHudMeters();

	public override Dictionary<uint, string> GetHudTexts() => HostBridge.GetHudTexts();
}
