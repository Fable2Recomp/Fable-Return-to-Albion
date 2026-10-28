namespace Refii.Tests.Modding;

using System.IO;
using System.Runtime.CompilerServices;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Both game hosts run the mods' frame work through <c>ModFrame</c>: the desktop's GameSession
/// and the Android host, which has no GameSession. The pumps need a live guest, so this holds the
/// wiring at the source, where a host that pumped its own list would drift from the other's.
/// </summary>
[TestSuite]
public class ModFrameTests
{
	private static string Source(string relative, [CallerFilePath] string here = "") =>
		File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../../src", relative)));

	private static readonly string[] Hosts = { "game/GameSession.cs", "android/AndroidGameHost.cs" };

	[TestCase]
	public void EveryHostPumpsAndStopsTheMods()
	{
		foreach (string host in Hosts)
		{
			string text = Source(host);
			AssertThat(text.Contains("ModFrame.Pump(this,")).OverrideFailureMessage(host + " does not pump the mods").IsTrue();
			AssertThat(text.Contains("ModFrame.Stop()")).OverrideFailureMessage(host + " does not stop the mods").IsTrue();
		}
	}

	[TestCase]
	public void NoHostPumpsAModPieceOfItsOwn()
	{
		foreach (string host in Hosts)
		{
			string text = Source(host);
			foreach (string piece in new[] { "ModSettings.Pump", "Engine_UiModAbilities.Pump", "ModErrors.Pump", "ModGodot.Pump", "ModGodot.Stop" })
			{
				AssertThat(text.Contains(piece)).OverrideFailureMessage(host + " calls " + piece + " itself").IsFalse();
			}
		}
	}

	[TestCase]
	public void TheFramePumpsEveryPiece()
	{
		string text = Source("Engine/mods/Engine_ModFrame.cs");
		foreach (string piece in new[] { "ModSettings.Pump(guestUp)", "Engine_UiModAbilities.Pump(guestUp)", "ModErrors.Pump()",
			"ModGodot.Pump(session, driven && guestUp)", "ModGodot.Stop()" })
		{
			AssertThat(text.Contains(piece)).OverrideFailureMessage("ModFrame lost " + piece).IsTrue();
		}
	}
}
