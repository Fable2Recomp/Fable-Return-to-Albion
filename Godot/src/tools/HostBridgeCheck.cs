using Godot;
using Refii.Host;

namespace Refii.Launcher.Tools;

/// <summary>
/// Proves the guest's address space can be reserved inside Godot's own process.
///
/// This is the gate for hosting the recompiled guest in-process. It is deliberately the
/// first thing built and the cheapest thing to run: if the reservation cannot be made
/// here, no amount of window, input or frame-loop work matters, and everything after it
/// is wasted.
/// </summary>
public partial class HostBridgeCheck : Node
{
	public override void _Ready()
	{
		int? abi = HostBridge.TryGetRawAbiVersion();
		if (abi is null)
		{
			GD.Print("HOST BRIDGE UNAVAILABLE — library not loadable.");
			GD.Print(HostBridge.ProbeGuestMemory().Message);
			GetTree().Quit(1);
			return;
		}

		GD.Print($"host bridge ABI {abi} (expected {HostBridge.ExpectedAbiVersion})");
		if (abi != HostBridge.ExpectedAbiVersion)
		{
			GD.Print("ABI MISMATCH — rebuild refii_hostbridge.");
			GetTree().Quit(1);
			return;
		}

		// Godot is fully up by _Ready: its own allocator, the Vulkan driver and every
		// loaded module already hold whatever address ranges they want. That is the
		// state the reservation has to survive, which is why this runs here rather than
		// from a standalone test binary.
		HostBridge.ProbeResult result = HostBridge.ProbeGuestMemory();
		GD.Print($"guest memory: {result.Message}");
		GD.Print(result.Success ? "HOST BRIDGE OK" : "HOST BRIDGE FAILED");

		GetTree().Quit(result.Success ? 0 : 1);
	}
}
