using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`entity_create_budget_ms`: one creation a frame always, more only while under the budget.</summary>
[TestSuite]
public sealed class EntityCreateBudgetTests
{
	/// <summary>Creations made in one frame whose creations each cost <paramref name="eachMs"/>.</summary>
	private static int MadeInAFrame(double eachMs, double budgetMs, int queued, int max = 128)
	{
		int made = 0;
		double spent = 0.0;
		while (made < queued && !Engine_EntityCreateBudget.Defer(made, max, spent, budgetMs))
		{
			++made;
			spent += eachMs;
		}
		return made;
	}

	[TestCase]
	public void TheFirstIsAlwaysMadeHoweverSlow() =>
		AssertThat(MadeInAFrame(eachMs: 200.0, budgetMs: 5.0, queued: 20)).IsEqual(1);

	[TestCase]
	public void SlowCreationsStopOnceTheBudgetIsSpent() =>
		AssertThat(MadeInAFrame(eachMs: 3.0, budgetMs: 5.0, queued: 20)).IsEqual(2);

	[TestCase]
	public void CheapCreationsRunToTheMaximum() =>
		AssertThat(MadeInAFrame(eachMs: 0.01, budgetMs: 5.0, queued: 500, max: 128)).IsEqual(128);

	[TestCase]
	public void ZeroNegativeAndNaNAreOff()
	{
		AssertThat(Engine_EntityCreateBudget.Clamp(0.0)).IsEqual(0.0);
		AssertThat(Engine_EntityCreateBudget.Clamp(-3.0)).IsEqual(0.0);
		AssertThat(Engine_EntityCreateBudget.Clamp(double.NaN)).IsEqual(0.0);
		AssertThat(Engine_EntityCreateBudget.Clamp(5.0)).IsEqual(5.0);
	}
}
