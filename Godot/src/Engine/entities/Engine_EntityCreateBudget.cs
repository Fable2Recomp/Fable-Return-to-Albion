using System;

namespace Refii.Entities;

/// <summary>
/// `entity_create_budget_ms`: in play, how long one frame may spend creating entity nodes, counted
/// from the creations themselves; 0 (the default) keeps the older rule.
/// </summary>
/// <remarks>
/// <para>
/// THE OLDER RULE MAKES AT LEAST 8 A FRAME, whatever they cost, then stops once 2 ms have passed
/// since the walk began. On the S10e the walk alone outlasts the 2 ms, so every frame with a
/// queue made exactly 8, and eight slow creations together (a crowd's rigs coming into view) were
/// one 169-203 ms ent.loop frame. Lowering the minimum to 2 measured nothing at a 30 fps cap.
/// </para>
/// <para>
/// With a budget, a frame makes one creation whatever it costs (so the queue always moves), then
/// further ones only while the creations' own summed time is under the budget. One creation can
/// still outlast it; two together no longer can, past the one that crosses it. The cost is
/// latency: a crowd arriving together takes a few more frames to appear. The load hold keeps its
/// own, larger budget either way.
/// </para>
/// </remarks>
public static class Engine_EntityCreateBudget
{
	/// <summary>A configured budget in milliseconds; 0 (off) for zero, negative or not a number.</summary>
	public static double Clamp(double ms) => !double.IsFinite(ms) || ms <= 0.0 ? 0.0 : ms;

	/// <summary>
	/// Whether the next creation waits a frame under a budget of <paramref name="budgetMs"/>:
	/// never before the first, always at <paramref name="maxCreations"/>, otherwise once the
	/// frame's creations have spent the budget.
	/// </summary>
	public static bool Defer(int createdThisFrame, int maxCreations, double spentMs, double budgetMs) =>
		createdThisFrame >= 1 && (createdThisFrame >= maxCreations || spentMs >= budgetMs);
}
