namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE HUD'S METERS AND TEXTS AS THE GUEST'S WRITERS STATE THEM (hooks/gui_registry_hooks.cpp,
/// Engine_UiHudRegistryWrites): `@Gui.Meter registry hash op a b c` from the meter registry's four
/// writers and `@Gui.Text registry hash text` from the text registry's, posted at each writer's
/// entry from its arguments.
/// </summary>
public partial class GameUi
{
	private bool _hudRegistryRoutesReady;

	private void EnsureHudRegistryRoutes()
	{
		if (_hudRegistryRoutesReady || _hud is null)
		{
			return;
		}

		_hudRegistryRoutesReady = true;
		_guiNatives.Register("@Gui.Meter", call =>
		{
			if (_hud is { } hud && int.TryParse(call.Argument(0).Text, out int registry) &&
				uint.TryParse(call.Argument(1).Text, out uint hash))
			{
				Engine_UiHudRegistryWrites.StateMeter(hud.Values, registry, hash,
					call.Argument(2).Text ?? string.Empty,
					Engine_UiHudRegistryWrites.Number(call.Argument(3).Text),
					Engine_UiHudRegistryWrites.Number(call.Argument(4).Text),
					Engine_UiHudRegistryWrites.Number(call.Argument(5).Text));
			}
		});

		_guiNatives.Register("@Gui.Text", call =>
		{
			if (_hud is { } hud && int.TryParse(call.Argument(0).Text, out int registry) &&
				registry == Engine_UiHudRegistryWrites.TextRegistry &&
				uint.TryParse(call.Argument(1).Text, out uint hash) &&
				Engine_UiHudRegistryWrites.TakesText(hash))
			{
				hud.Values.StateText(hash, call.Argument(2).Text ?? string.Empty);
			}
		});
	}
}
