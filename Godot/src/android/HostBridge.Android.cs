using System.Runtime.InteropServices;

namespace Refii.Host;

/// <summary>
/// The host bridge entry points only the Android host calls: the single-player controller's
/// tracked phases and their read receipts, and the embedded movie's size probe.
/// </summary>
public static partial class HostBridge
{
	/// <summary>Publishes an Android physical phase tag without conflating current analog packet updates with consumption.</summary>
	/// <param name="userIndex">The existing guest controller slot.</param>
	/// <param name="connected">Whether the selected pad is connected.</param>
	/// <param name="buttons">Already-filtered XInput button bits.</param>
	/// <param name="leftTrigger">Current left trigger, in Xbox units.</param>
	/// <param name="rightTrigger">Current right trigger, in Xbox units.</param>
	/// <param name="thumbLx">Current left-stick X.</param>
	/// <param name="thumbLy">Current left-stick Y.</param>
	/// <param name="thumbRx">Current right-stick X.</param>
	/// <param name="thumbRy">Current right-stick Y.</param>
	/// <param name="receiptToken">Stable physical phase identity; zero requests no acknowledgment.</param>
	internal static void SetControllerStateTracked(int userIndex, bool connected, uint buttons,
		uint leftTrigger, uint rightTrigger, int thumbLx, int thumbLy, int thumbRx, int thumbRy,
		ulong receiptToken) => RefiiHostSetControllerStateTracked((uint)userIndex, connected ? 1 : 0,
			buttons, leftTrigger, rightTrigger, thumbLx, thumbLy, thumbRx, thumbRy, receiptToken);

	/// <summary>Returns the exact phase last copied by a successful non-null guest XInput state read.</summary>
	/// <param name="userIndex">The existing guest controller slot.</param>
	/// <returns>A monotonically acknowledged phase tag; selecting or stepping alone never acknowledges it.</returns>
	internal static ulong GetControllerReadReceipt(int userIndex) => RefiiHostGetControllerReadReceipt((uint)userIndex);

	/// <summary>Probes an embedded Bink request without allocating or copying its guest-memory buffer.</summary>
	/// <param name="generation">The shared request generation, including unnamed startup logos.</param>
	/// <returns>The live embedded buffer size, or zero for no buffer/name-only requests.</returns>
	public static uint GetRequestedVideoSize(out uint generation)
	{
		int ok = RefiiHostGetRequestedVideo(null, 0, out uint size, out generation);
		return ok != 0 ? size : 0;
	}

	[DllImport(Library)]
	private static extern void RefiiHostSetControllerStateTracked(uint userIndex, int connected,
		uint buttons, uint leftTrigger, uint rightTrigger, int thumbLx, int thumbLy,
		int thumbRx, int thumbRy, ulong receiptToken);

	[DllImport(Library)]
	private static extern ulong RefiiHostGetControllerReadReceipt(uint userIndex);
}
