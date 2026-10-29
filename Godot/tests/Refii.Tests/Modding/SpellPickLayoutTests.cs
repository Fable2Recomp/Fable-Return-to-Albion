namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Ui;
using Refii.Titles.Fable2;

/// <summary>
/// Every mod's spell slot pick on the selectors (Engine_UiSpellPicks): where each pick's icon sits
/// under a slot, and which pick X and Y act on. No Godot type is constructed.
/// </summary>
[TestSuite]
public class SpellPickLayoutTests
{
	[TestCase]
	public void OnePickSitsWhereItAlwaysHas()
	{
		// The single frame of before: overhanging the lower-right corner at the pick's scale.
		(float x, float y, float s) = Engine_UiSpellPicks.Layout(1, 0.55f)[0];
		AssertThat(s).IsEqual(0.55f);
		AssertThat(Math.Abs(x - (0.5f - (0.55f * 0.35f))) < 1e-6f).IsTrue();
		AssertThat(Math.Abs(y + (0.5f - (0.55f * 0.35f))) < 1e-6f).IsTrue();
	}

	[TestCase]
	public void EveryPickFitsTheSlotInLoadOrder()
	{
		float reach = 0.5f + Engine_UiSpellPicks.LayoutOverhang + 1e-5f;
		for (int count = 1; count <= 9; ++count)
		{
			(float X, float Y, float Scale)[] at = Engine_UiSpellPicks.Layout(count, 0.55f);
			AssertThat(at.Length).IsEqual(count);
			float scale = at[0].Scale;
			AssertThat(count * scale * scale <= (0.55f * 0.55f) + 1e-5f)
				.OverrideFailureMessage($"{count} picks cover more of the slot's spell than one").IsTrue();
			AssertThat(scale >= 0.15f).OverrideFailureMessage($"{count} picks drawn at {scale:F2} of the slot").IsTrue();
			for (int i = 0; i < count; ++i)
			{
				(float x, float y, float s) = at[i];
				AssertThat(s).IsEqual(scale);
				AssertThat(x - (s / 2) >= -reach && x + (s / 2) <= reach && y - (s / 2) >= -reach && y + (s / 2) <= reach)
					.OverrideFailureMessage($"{count} picks: pick {i} at ({x:F2}, {y:F2}) leaves the slot").IsTrue();
				for (int j = 0; j < i; ++j)
				{
					bool apart = Math.Abs(at[j].X - x) >= s - 1e-5f || Math.Abs(at[j].Y - y) >= s - 1e-5f;
					AssertThat(apart).OverrideFailureMessage($"{count} picks: {j} and {i} overlap").IsTrue();

					// Load order reads left to right, then down the rows.
					bool before = at[j].Y > y + 1e-5f || (Math.Abs(at[j].Y - y) < 1e-5f && at[j].X < x);
					AssertThat(before).OverrideFailureMessage($"{count} picks: {j} is not before {i}").IsTrue();
				}
			}
		}
	}

	[TestCase]
	public void APickCursorPerPlayerWraps()
	{
		Engine_UiSpellPicks.StepCursor(1, -Engine_UiSpellPicks.Cursor(1, 3), 3);
		Engine_UiSpellPicks.StepCursor(2, -Engine_UiSpellPicks.Cursor(2, 3), 3);
		AssertThat(Engine_UiSpellPicks.StepCursor(1, 1, 3)).IsEqual(1);
		AssertThat(Engine_UiSpellPicks.StepCursor(1, 2, 3)).IsEqual(0);
		AssertThat(Engine_UiSpellPicks.StepCursor(1, -1, 3)).IsEqual(2);
		AssertThat(Engine_UiSpellPicks.Cursor(2, 3)).OverrideFailureMessage("player two's cursor is their own").IsEqual(0);

		// Fewer picks after a remount: the cursor stays on one that exists.
		AssertThat(Engine_UiSpellPicks.Cursor(1, 2)).IsEqual(1);
		AssertThat(Engine_UiSpellPicks.Cursor(1, 0)).IsEqual(0);
		Engine_UiSpellPicks.StepCursor(1, -Engine_UiSpellPicks.Cursor(1, 3), 3);
	}

	[TestCase]
	public void ABareNameIsWarned()
	{
		IReadOnlyList<ModIssue> bare = ModValidate.Validate("ui/spellwheel.json",
			"{ \"picks\": [ { \"name\": \"partner\", \"store\": \"my_mod.partner_{slot}\" } ] }");
		AssertThat(bare.Any(i => !i.Error && i.Pointer == "picks[0].name")).IsTrue();
		IReadOnlyList<ModIssue> prefixed = ModValidate.Validate("ui/spellwheel.json",
			"{ \"picks\": [ { \"name\": \"my_mod.partner\", \"store\": \"my_mod.partner_{slot}\" } ] }");
		AssertThat(prefixed.Count).IsEqual(0);
	}
}
