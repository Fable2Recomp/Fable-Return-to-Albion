namespace Refii.Tests.Lighting;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Lighting;

/// <summary>
/// The pan-stability rules of <see cref="Engine_LightShadowBudget"/>: what stays in view whatever
/// the frustum says, how long a hold lasts, and the sticky pick over a ranked list.
/// </summary>
[TestSuite]
public class LightShadowBudgetPanTests
{
	[TestCase]
	public void NearEligible_KeepsTheNearSetWhateverTheFrustumSays()
	{
		AssertThat(Engine_LightShadowBudget.NearEligible(10f, 5f)).IsTrue();
		AssertThat(Engine_LightShadowBudget.NearEligible(Engine_LightShadowBudget.NearEligibleMetres, 1f)).IsTrue();
		AssertThat(Engine_LightShadowBudget.NearEligible(Engine_LightShadowBudget.NearEligibleMetres + 0.5f, 1f)).IsFalse();
		// A pool that reaches the eye is in view at any distance.
		AssertThat(Engine_LightShadowBudget.NearEligible(60f, 56f)).IsTrue();
		AssertThat(Engine_LightShadowBudget.NearEligible(60f, 55f)).IsFalse();
	}

	[TestCase]
	public void ShadowNearEligible_RanksFromThePlayerAndTheEye()
	{
		// The pool reaches the player with room to spare: a camera orbit never takes the map.
		AssertThat(Engine_LightShadowBudget.ShadowNearEligible(playerDistance: 14f, eyeDistance: 40f, range: 5f)).IsTrue();
		AssertThat(Engine_LightShadowBudget.ShadowNearEligible(playerDistance: 16f, eyeDistance: 40f, range: 5f)).IsFalse();
		// Or the eye.
		AssertThat(Engine_LightShadowBudget.ShadowNearEligible(playerDistance: 40f, eyeDistance: 11f, range: 5f)).IsTrue();
		AssertThat(Engine_LightShadowBudget.ShadowNearEligible(playerDistance: 40f, eyeDistance: 12f, range: 5f)).IsFalse();
	}

	[TestCase]
	public void Held_LastsTheHoldAndNeverFromNothing()
	{
		AssertThat(Engine_LightShadowBudget.Held(0, 5000, Engine_LightShadowBudget.ShadowHoldMs)).IsFalse();
		AssertThat(Engine_LightShadowBudget.Held(1000, 1000 + Engine_LightShadowBudget.ShadowHoldMs, Engine_LightShadowBudget.ShadowHoldMs)).IsTrue();
		AssertThat(Engine_LightShadowBudget.Held(1000, 1001 + Engine_LightShadowBudget.ShadowHoldMs, Engine_LightShadowBudget.ShadowHoldMs)).IsFalse();
		AssertThat(Engine_LightShadowBudget.Held(1000, 1000 + Engine_LightShadowBudget.LightHoldMs, Engine_LightShadowBudget.LightHoldMs)).IsTrue();
	}

	[TestCase]
	public void SelectSticky_AHolderJustPastTheBudgetKeepsItsMap()
	{
		// Ranked best first; rank 7 holds a map and sits within budget 6 + slack 4.
		bool[] holding = { false, false, false, false, false, false, false, true, false, false, false, true };
		bool[] chosen = new bool[holding.Length];
		int taken = Engine_LightShadowBudget.SelectSticky(holding, 6, Engine_LightShadowBudget.Slack, chosen);

		AssertThat(taken).IsEqual(6);
		AssertThat(chosen[7]).IsTrue();
		// The top five fill the rest; rank 5 is the one left out.
		for (int i = 0; i < 5; i++)
		{
			AssertThat(chosen[i]).IsTrue();
		}

		AssertThat(chosen[5]).IsFalse();
		AssertThat(chosen[6]).IsFalse();
		// Rank 11 holds a map too, but is past the slack: it is taken.
		AssertThat(chosen[11]).IsFalse();
	}

	[TestCase]
	public void SelectSticky_WithoutHoldersIsTheTopOfTheRanking()
	{
		bool[] holding = new bool[10];
		bool[] chosen = new bool[10];
		int taken = Engine_LightShadowBudget.SelectSticky(holding, 3, Engine_LightShadowBudget.Slack, chosen);
		AssertThat(taken).IsEqual(3);
		AssertThat(chosen[0] && chosen[1] && chosen[2]).IsTrue();
		AssertThat(chosen[3]).IsFalse();
	}

	[TestCase]
	public void SelectSticky_HoldersNeverExceedTheBudget()
	{
		bool[] holding = { true, true, true, true, true, true, true, true };
		bool[] chosen = new bool[8];
		int taken = Engine_LightShadowBudget.SelectSticky(holding, 6, Engine_LightShadowBudget.Slack, chosen);
		AssertThat(taken).IsEqual(6);
		AssertThat(chosen[6] || chosen[7]).IsFalse();
	}

	[TestCase]
	public void SelectSticky_UncappedTakesEverything()
	{
		bool[] holding = new bool[5];
		bool[] chosen = new bool[5];
		int taken = Engine_LightShadowBudget.SelectSticky(holding, int.MaxValue, Engine_LightShadowBudget.Slack, chosen);
		AssertThat(taken).IsEqual(5);
	}
}
