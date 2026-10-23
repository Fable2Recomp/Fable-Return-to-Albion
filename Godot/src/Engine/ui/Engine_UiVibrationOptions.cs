using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// The game's own rumble switch as a host options page under the Controls fold, driven by THE
/// GUEST'S BYTE — no host copy. Fable 2 shipped the switch without a page behind it: the only
/// reader of <c>byte_8331924F</c> is the rumble dispatcher <c>sub_823EAA58</c>
/// (<c>0x823eaa74 lbz r10, byte_8331924F</c> — zero and no effect reaches a pad), its only
/// writer is the Lua native <c>SetRumbleEnabled</c> (<c>sub_823B3A78</c>, <c>stb r3</c>), no
/// shipped script calls it, and the byte is 1 in <c>.data</c>. So the page reads the byte for its
/// row and writes it on commit, and the game's default is the byte's: on.
/// </summary>
/// <remarks>
/// The byte is not in the profile or the save, so the guest forgets it between runs — the page
/// is per session, and the launcher's <c>controller_vibration</c> is the persistent switch. Both
/// gate: the guest emits only while its byte is set, the host applies only while the launcher
/// setting is on.
/// </remarks>
public sealed class Engine_UiVibrationOptions : Engine_UiHostOptionsPage
{
	public const string PageName = "ControllerVibration";
	public const string OpenCommand = "options:vibration";
	public const string PageTitle = "Controller Vibration";

	/// <summary>
	/// The guest's rumble switch: TU1 <c>byte_8331924F</c>, unpatched <c>byte_833191E2</c> (its one
	/// writer, the `SetRumbleEnabled` native, at 0x823A548C; its one reader, the dispatcher's
	/// `lbz` at 0x823C949C; 1 in .data on both).
	/// </summary>
	public static uint RumbleEnabledByte => Refii.Host.HostBridge.PickByBuild(0x8331924Fu, 0x833191E2u);

	/// <summary>The word holding it; big-endian, so the byte's lane in the word is 3 minus its offset.</summary>
	private static uint RumbleEnabledWord => RumbleEnabledByte & ~3u;

	private static int LaneShift => 8 * (3 - (int)(RumbleEnabledByte & 3u));

	private static uint ByteLane => 0xFFu << LaneShift;

	private static uint On => 1u << LaneShift;

	public static readonly Engine_UiVibrationOptions Page = new();

	public override string Name => PageName;

	public override string Title => PageTitle;

	public override int Count => 1;

	public override string RowTitle(int row) => "Controller vibration";

	public override int Choices(int row) => 2;

	public override string Caption(int row, int index) => index != 0 ? "On" : "Off";

	/// <summary>The guest's byte now; the game's default (on) while no guest is up to ask.</summary>
	public static bool GuestEnabled() =>
		!HostBridge.TryPeekWord(RumbleEnabledWord, out uint word) || (word & ByteLane) != 0;

	public override int[] Read() => new[] { GuestEnabled() ? 1 : 0 };

	/// <summary>A on the page writes the byte, as the Lua native would.</summary>
	public override bool Commit(System.Func<int, int> index)
	{
		if (HostBridge.TryPeekWord(RumbleEnabledWord, out uint word))
		{
			HostBridge.Poke(RumbleEnabledWord, (word & ~ByteLane) | (index(0) != 0 ? On : 0u));
		}

		return true;
	}
}
