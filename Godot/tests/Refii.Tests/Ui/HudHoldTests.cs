namespace Refii.Tests.Ui;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// A held HUD piece (<see cref="Engine_UiHudHold"/>, <c>hud_hold</c>) against the shipped
/// <c>hud.bgf</c>: the health readout is faded by the guest's stated opacity on
/// <c>PlayerHealth.HealthAndMana.Health</c> (<c>CoopHealth</c>'s for player two), which no slide
/// commands, so a hold draws it at its authored opacity; an element a slide fades (the orb's
/// <c>Base2</c> pulse) is left to its slide. No Godot type is built.
/// </summary>
[TestSuite]
public class HudHoldTests
{
	/// <summary>The bank holding the presentations, beside the tree's packaged install; null without one.</summary>
	private static string? ShippedBank()
	{
		string? env = Environment.GetEnvironmentVariable(Engine_GameData.EnvironmentVariable);
		if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "data", "guiscripts.bnk")))
		{
			return Path.Combine(env, "data", "guiscripts.bnk");
		}

		for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			string direct = Path.Combine(at, "assets", "Fable2", "game", "data", "guiscripts.bnk");
			if (File.Exists(direct))
			{
				return direct;
			}
		}

		return null;
	}

	private static Engine_UiScreen? Hud()
	{
		string? bank = ShippedBank();
		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (string.Equals(Path.GetFileName(entry.RelativePath.Replace('\\', '/')), "hud.bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse("hud", archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	/// <summary>The element named <paramref name="leaf"/> whose named ancestors include <paramref name="widget"/>.</summary>
	private static int Under(Engine_UiScreen screen, string widget, string leaf) =>
		screen.Nodes.First(n => n.Name == leaf && Ancestors(n).Any(a => a.Name == widget)).Ordinal;

	private static System.Collections.Generic.IEnumerable<UiNode> Ancestors(UiNode node)
	{
		for (UiNode? up = node.Parent; up is not null; up = up.Parent)
		{
			yield return up;
		}
	}

	[TestCase]
	public void AHeldHealthReadoutIsDrawnOverTheGuestsFade()
	{
		if (Hud() is not { } screen)
		{
			Console.WriteLine("[hud-hold] no packaged install beside the tree; the shipped health readout is not held here");
			return;
		}

		int health = Under(screen, "PlayerHealth", "Health");
		int coopHealth = Under(screen, "CoopHealth", "Health");
		int pulse = Under(screen, "PlayerHealth", "Base2");
		var commanded = Engine_UiHudHold.OpacityCommanded(screen.Document);
		AssertThat(screen.Nodes[health].Opacity).IsEqual(100f);
		AssertThat(commanded.Contains(health)).IsFalse();
		AssertThat(commanded.Contains(coopHealth)).IsFalse();
		AssertThat(commanded.Contains(pulse)).IsTrue();

		// The guest's fade at rest: Health stated 0, everything else as loaded.
		int count = screen.Nodes.Length;
		float?[] Stated()
		{
			var stated = new float?[count];
			for (int i = 0; i < count; i++)
			{
				stated[i] = screen.Nodes[i].Opacity;
			}

			stated[health] = 0f;
			stated[coopHealth] = 0f;
			stated[pulse] = 0f;
			return stated;
		}

		int[] slides = Engine_UiRestState.Slides(screen);
		var active = Enumerable.Repeat(true, count).ToArray();

		float?[] one = Stated();
		var hold = new Engine_UiHudHold(screen, new[] { "PlayerHealth" });
		AssertThat(hold.OwnerCount > 0).IsTrue();
		AssertThat(hold.FadedCount > 0).IsTrue();
		hold.Apply((int[])slides.Clone(), active, one);
		AssertThat(one[health]).IsEqual(100f);
		// The pulse's slide decides it (the hold releases it to Slide1's track); it is never lifted.
		AssertThat(one[pulse] ?? 0f).IsEqual(0f);
		AssertThat(one[coopHealth]).IsEqual(0f);

		// Player two's readout is its own piece.
		float?[] two = Stated();
		new Engine_UiHudHold(screen, new[] { "CoopHealth" }).Apply((int[])slides.Clone(), active, two);
		AssertThat(two[coopHealth]).IsEqual(100f);
		AssertThat(two[health]).IsEqual(0f);

		// Only the piece's trunk is lifted: the D-pad's slot sparkles and press glow, past its first
		// branch, keep the opacity the guest states.
		var dpad = new Engine_UiHudHold(screen, new[] { "dpad" });
		AssertThat(dpad.Faded.Any(o => screen.Nodes[o].Name is "sparkles" or "glow")).IsFalse();
		AssertThat(hold.Faded.Contains(health)).IsTrue();
		AssertThat(hold.Faded.Any(o => screen.Nodes[o].Name is "Bar" or "Icon" or "BarMax" or "Base" or "Heart")).IsFalse();
		Console.WriteLine("[hud-hold] trunk lifts — PlayerHealth: " + string.Join(", ", hold.Faded.Select(o => o + ":" + screen.Nodes[o].Name)) +
			"; dpad: " + string.Join(", ", dpad.Faded.Select(o => o + ":" + screen.Nodes[o].Name)));
	}
}
