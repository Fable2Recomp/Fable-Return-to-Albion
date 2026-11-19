namespace Refii.Titles.Fable3;

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Godot;
using Refii.Host;
using Refii.Ui;

/// <summary>
/// Fable 3's aim reticules — slot 0 the spell target, slot 1 the ranged aim, one record of each per
/// player in the HUD's reticule manager *(H+0x3C) — on the shared <see cref="Engine_HudReticule"/>.
/// The guest pushes each as a render-scene sprite (HudReticule_Draw sub_822906A8 →
/// RenderScene_AddObject at 0x82290a24), which nothing draws under the null device. Its setters state
/// the object (`@Reticule`) and the record as they left it (reticle_hooks.cpp), read back by
/// reticle_feed.cpp.
/// </summary>
public static class Fable3Reticles
{
	/// <summary>sub_822906A8: one sprite on the point, s = (1 − t)·8 + 8, k = (1 − t)·1.25 + 1, angle t·π/2
	/// for both players, no clamp of its own, `targetA` for modes 0..2 and `target` above (0x822907D8..FC).</summary>
	public static readonly Engine_HudReticule.Style Look = new(1, 0f, 0f, 8f, 8f, new[] { 0f, 0f }, null,
		mode => mode <= 2 ? "art\\gui\\persistant\\targeta.tex" : "art\\gui\\persistant\\target.tex");

	private static readonly uint[,] Objects = new uint[2, 2];
	private static readonly bool[,] Active = new bool[2, 2];
	private static readonly float[] Values = new float[11];
	private static readonly uint[] Words = new uint[2];

	public static void Register(Engine_UiGuiNatives router) =>
		router.Register("@Reticule", call =>
		{
			int player = call.Argument(0).Text == "0" ? 0 : 1;
			int slot = call.Argument(1).Text == "0" ? 0 : 1;
			uint.TryParse(call.Argument(2).Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint thing);
			Objects[player, slot] = thing;
			Active[player, slot] = call.Argument(3).Text == "1";
		});

	/// <summary>
	/// One player's slot this frame and the backbuffer height its pixels are in; null while down. The
	/// ranged aim is placed by its world anchor (aimer+0x80 as its setter projected it); the spell
	/// target by the point the guest stated (its projection is computed in a register and only
	/// stored in the record).
	/// </summary>
	public static Engine_HudReticule.Aim? Read(int player, int slot, out float guestHeight)
	{
		guestHeight = 720f;
		if (!Active[player, slot] || !HostBridge.IsGuestUp)
		{
			return null;
		}

		uint up;
		try
		{
			up = RefiiHostGetFable3Reticle((uint)player, (uint)slot, slot == 1 ? Objects[player, slot] : 0u,
				Values, (uint)Values.Length, Words, (uint)Words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		if (up == 0u)
		{
			return null;
		}

		guestHeight = Values[10] > 0f ? Values[10] : 720f;
		var colour = new Color(Values[3], Values[4], Values[5], Values[6]);
		int counter = unchecked((int)Words[1]);
		return slot == 1
			? new Engine_HudReticule.Aim(
				new Vector3(Values[7], Values[8], Values[9]), Values[2], colour, Words[0], counter,
				Screen: null, Object: Objects[player, slot], Tick: Refii.Core.Engine_TickClock.GuestGameTick,
				StepComplete: Refii.Core.Engine_GuestFrame.Changed)
			: new Engine_HudReticule.Aim(Vector3.Zero, Values[2], colour, Words[0], counter, new Vector2(Values[0], Values[1]));
	}

	[DllImport(HostBridge.Library)]
	private static extern uint RefiiHostGetFable3Reticle(uint player, uint slot, uint thing,
		[Out] float[] values, uint valueCapacity, [Out] uint[] words, uint wordCapacity);
}
