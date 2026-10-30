namespace Refii.Tests.Lighting;

using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Models;
using static GdUnit4.Assertions;
using Group = Refii.Models.Engine_ClassicLights.Fable3Group;
using Light = Refii.Models.Engine_ClassicLights.Light;

/// <summary>
/// Fable 3's light group block (Engine_ClassicLights.Fable3Group) against values read off the
/// packers: point sub_82223FD8, spot sub_82C86E68, directional sub_82288FF0, the group walk
/// sub_821FC990, the constructor's defaults sub_82CEEAE0 and the selection sub_82CFCC08.
/// </summary>
[TestSuite]
public class ClassicLightGroupFable3Tests
{
	private static Light Make(int type, int index, int flags = 0, Vector3? direction = null) => new(
		new Vector3(1f, 2f, 3f), 10f, new Vector3(0.5f, 0.25f, 1f), 2f, direction ?? new Vector3(0f, 0f, -1f),
		type, 10f, 0.6f, 2f, flags, 0u, index, 0.75f);

	private static Group.Stated Stated(bool mapped = false) =>
		new(new Vector3(4f, 5f, 6f), mapped, new Engine_ClassicLights.Cone(0.8f, 5f, 0.1f));

	private static float[] Register(float[] block, int register) =>
		new[] { block[register * 4], block[register * 4 + 1], block[register * 4 + 2], block[register * 4 + 3] };

	private static float Lane(float[] block, int register, int lane) => block[register * 4 + lane];

	[TestCase]
	public void TestEmptyBlockIsTheConstructorDefaults()
	{
		var block = new float[Group.PixelRegisters * 4];
		Array.Fill(block, 9f);
		int count = Group.PackGroup(block, new List<Light>(), Array.Empty<Group.Stated>());

		AssertThat(count).IsEqual(0);
		for (int slot = 0; slot < Group.MaxLights; slot++)
		{
			AssertThat(Register(block, Group.Positions + slot)).ContainsExactly(0f, 0f, 0f, 1f);
			AssertThat(Register(block, Group.Colors + slot)).ContainsExactly(0f, 0f, 0f, 0f);
			AssertThat(Register(block, Group.SpotDirections + slot)).ContainsExactly(0f, 0f, 0f, 0f);
		}
		AssertThat(Register(block, Group.SpotCos)).ContainsExactly(-1f, -1f, -1f, -1f);
		AssertThat(Register(block, Group.SpotCos + 1)).ContainsExactly(-1f, -1f, -1f, -1f);
		AssertThat(Register(block, Group.SpotFalloff + 1)).ContainsExactly(1f, 1f, 1f, 1f);
		AssertThat(Register(block, Group.Type)).ContainsExactly(0f, 0f, 0f, 0f);
		AssertThat(Register(block, Group.InvRadii + 1)).ContainsExactly(0f, 0f, 0f, 0f);
		AssertThat(Register(block, Group.PrimaryLightDirection)).ContainsExactly(0f, 0f, 0f, 0f);
		AssertThat(Register(block, Group.ScreenSpaceShadowMask)).ContainsExactly(0f, 0f, 0f, 0f);
		AssertThat(Register(block, Group.SpotLightShadowMask + 1)).ContainsExactly(0f, 0f, 0f, 0f);
	}

	[TestCase]
	public void TestPointPacksSlotFive()
	{
		var block = new float[Group.PixelRegisters * 4];
		Group.Clear(block);
		Group.PackSlot(block, Make(Light.Point, 7), Stated(), 5);

		// Slot 5: quad 1, lane 1.
		AssertThat(Register(block, 5)).ContainsExactly(4f, 5f, 6f, 1f);
		AssertThat(Register(block, 8 + 5)).ContainsExactly(1f, 0.5f, 2f, 1.5f);
		AssertThat(Register(block, 20 + 5)).ContainsExactly(0f, 0f, 0f, 0f);
		AssertThat(Lane(block, 16 + 1, 1)).IsEqual(1f);
		// L+452 x L+428 = (1 / 2) x 0.1.
		AssertThat(Lane(block, 18 + 1, 1)).IsEqual(0.5f * 0.1f);
		AssertThat(Lane(block, 28 + 1, 1)).IsEqual(-1f);
		AssertThat(Lane(block, 30 + 1, 1)).IsEqual(1f);
		AssertThat(Lane(block, 36 + 1, 1)).IsEqual(0f);
		AssertThat(Lane(block, 38 + 1, 1)).IsEqual(0f);
		// Its neighbours' lanes are untouched.
		AssertThat(Lane(block, 16 + 1, 0)).IsEqual(0f);
		AssertThat(Lane(block, 18 + 1, 2)).IsEqual(0f);
	}

	[TestCase]
	public void TestSpotPacksConeAndShadowMasks()
	{
		var block = new float[Group.PixelRegisters * 4];
		Group.Clear(block);
		var direction = new Vector3(0f, 1f, 0f);
		Group.PackSlot(block, Make(Light.Spot, 4, flags: 0, direction), Stated(mapped: true), 2);

		AssertThat(Register(block, 2)).ContainsExactly(4f, 5f, 6f, 1f);
		AssertThat(Register(block, 20 + 2)).ContainsExactly(0f, -1f, 0f, 0f);
		AssertThat(Lane(block, 16, 2)).IsEqual(1f);
		AssertThat(Lane(block, 28, 2)).IsEqual(0.8f);
		AssertThat(Lane(block, 30, 2)).IsEqual(5f);
		// A mapped spot without L+341 and L+484 shadows from its own map.
		AssertThat(Lane(block, 36, 2)).IsEqual(0f);
		AssertThat(Lane(block, 38, 2)).IsEqual(1f);

		// With L+341 (bit 1) and L+484 (bit 8) it is the screen-space mask's instead.
		Group.PackSlot(block, Make(Light.Spot, 4, flags: 2 | 8, direction), Stated(mapped: true), 3);
		AssertThat(Lane(block, 36, 3)).IsEqual(1f);
		AssertThat(Lane(block, 38, 3)).IsEqual(0f);

		// No active shadow: neither.
		Group.PackSlot(block, Make(Light.Spot, 4, flags: 2 | 8, direction), Stated(mapped: false), 1);
		AssertThat(Lane(block, 36, 1)).IsEqual(0f);
		AssertThat(Lane(block, 38, 1)).IsEqual(0f);
	}

	[TestCase]
	public void TestDirectionalPacksDirectionAndUnscaledColour()
	{
		var block = new float[Group.PixelRegisters * 4];
		Group.Clear(block);
		Group.PackSlot(block, Make(Light.Directional, 3, flags: 2, new Vector3(0f, 0f, -1f)), Stated(), 0);

		AssertThat(Register(block, 0)).ContainsExactly(0f, 0f, 1f, 0f);
		AssertThat(Register(block, 8)).ContainsExactly(0.5f, 0.25f, 1f, 0.75f);
		AssertThat(Lane(block, 16, 0)).IsEqual(0f);
		AssertThat(Lane(block, 18, 0)).IsEqual(0f);
		AssertThat(Lane(block, 28, 0)).IsEqual(-1f);
		AssertThat(Lane(block, 30, 0)).IsEqual(1f);
		// L+341 sets its screen-space mask lane.
		AssertThat(Lane(block, 36, 0)).IsEqual(1f);
		AssertThat(Lane(block, 38, 0)).IsEqual(0f);
	}

	[TestCase]
	public void TestSelectionOrderCapAndKey()
	{
		var set = new List<Light>
		{
			Make(Light.Point, 20), Make(Light.Spot, 2), Make(Light.Directional, 10), Make(Light.Point, 1),
			Make(Light.Point, 40), Make(Light.Spot, 4), Make(Light.Point, 7), Make(Light.Directional, 3),
			Make(Light.Spot, 5), Make(Light.Point, 30),
		};
		var group = new List<Light>();
		Group.Select(set, group);

		// Directionals 3, 10; points 1, 7, 20, 30, 40; then the lowest spot, 2: eight. Slots
		// run highest index first.
		var indices = new List<int>();
		foreach (Light light in group)
		{
			indices.Add(light.Index);
		}
		AssertThat(indices).ContainsExactly(40, 30, 20, 10, 7, 3, 2, 1);

		Group.Key key = Group.Count(set);
		AssertThat(key.Points).IsEqual(5);
		AssertThat(key.Spots).IsEqual(3);
		AssertThat(key.Total).IsEqual(10);
		AssertThat(key.Bits).IsEqual((5u << 14) | (3u << 20) | (10u << 26));
	}

	[TestCase]
	public void TestBlockCountAndPrimaryDirection()
	{
		var block = new float[Group.PixelRegisters * 4];
		AssertThat(Group.BlockCount(new List<Light> { Make(Light.Point, 1) })).IsEqual(2);
		AssertThat(Group.BlockCount(new List<Light> { Make(Light.Directional, 1) })).IsEqual(1);

		var spotDirection = new Vector3(1f, 0f, 0f);
		var slots = new List<Light>
		{
			Make(Light.Directional, 5, direction: new Vector3(0f, 0f, -1f)),
			Make(Light.Spot, 2, flags: 8, spotDirection),
			Make(Light.Point, 1),
		};
		int count = Group.PackGroup(block, slots, new[] { Stated(), Stated(), Stated() });
		AssertThat(count).IsEqual(3);
		// Written after the directional's, the spot with L+484 holds c32.
		AssertThat(Register(block, Group.PrimaryLightDirection)).ContainsExactly(-1f, 0f, 0f, 0f);
		AssertThat(Register(block, 2)).ContainsExactly(4f, 5f, 6f, 1f);
		AssertThat(Register(block, 3)).ContainsExactly(0f, 0f, 0f, 1f);
	}
}
