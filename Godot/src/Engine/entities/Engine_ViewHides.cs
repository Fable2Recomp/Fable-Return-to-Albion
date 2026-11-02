using System;
using System.Collections.Generic;

namespace Refii.Entities;

/// <summary>
/// Where one rig's hidden bones are hidden this frame: on the pose (every view at once) or in one
/// split view alone (<see cref="Engine_ViewTwin"/>). Pure: bone indices in, bone indices out.
/// </summary>
/// <remarks>
/// <para>WHAT IS HIDDEN. A bone hidden in every view (<c>hide_part</c> with player 0); a bone hidden
/// in player one's or player two's view alone (<c>hide_part</c> with player 1 or 2, and the
/// first-person head of that player's hero, <see cref="Engine_FirstPersonHead"/>). A bone stands for
/// itself and every bone under it.</para>
/// <para>SPLIT SCREEN. View 0 is player one's camera, view 1 player two's
/// (<see cref="Engine_FirstPersonView.OwnView"/>). A bone both views hide goes on the pose; the rest of
/// each player's bones are hidden in that player's view alone: a twin there collapses them, and the
/// body is drawn only in a view with nothing of its own hidden (none when both views hide something).</para>
/// <para>ONE VIEW. A view is drawn for the players this machine shows (<see cref="Viewed"/>): alone,
/// player one; a shared couch screen, both; an online host, player one; an online joiner, player two —
/// each machine draws its own player's view, and the partner's per-player hides are that machine's.
/// A per-player hide for a player this view shows is a hide on the pose; for any other, nothing.</para>
/// </remarks>
public static class Engine_ViewHides
{
	/// <summary>The bones (subtree roots) hidden on the pose, in view 0 alone and in view 1 alone; each sorted, none repeated.</summary>
	public readonly record struct Plan(int[] Pose, int[] FirstView, int[] SecondView)
	{
		public static readonly Plan None = new(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

		/// <summary>Whether any view needs a twin.</summary>
		public bool PerView => FirstView.Length > 0 || SecondView.Length > 0;

		/// <summary>The bones hidden in view <paramref name="view"/> (0 or 1) alone.</summary>
		public int[] View(int view) => view == 0 ? FirstView : SecondView;
	}

	/// <summary>
	/// Whether this machine's one view is drawn for <paramref name="player"/>: player one unless this
	/// machine joined another's game online (<paramref name="onlineClient"/>), player two when it did or
	/// when both players share this screen (<paramref name="localCoop"/>).
	/// </summary>
	public static bool Viewed(int player, bool localCoop, bool onlineClient) => player switch
	{
		1 => !onlineClient,
		2 => onlineClient || localCoop,
		_ => false,
	};

	/// <summary>
	/// The plan for a rig hiding <paramref name="everyView"/> in every view and
	/// <paramref name="playerOne"/> / <paramref name="playerTwo"/> in that player's view.
	/// <paramref name="perView"/>: the screen is split and the rig can be drawn twice (its body has a
	/// skin); otherwise there is one picture of the rig and <paramref name="oneViewed"/> /
	/// <paramref name="twoViewed"/> (<see cref="Viewed"/>) say whose hides it takes.
	/// <paramref name="parents"/>, when given, drops a per-view bone already under a pose bone.
	/// </summary>
	public static Plan Compose(IReadOnlyCollection<int> everyView, IReadOnlyCollection<int> playerOne, IReadOnlyCollection<int> playerTwo,
		bool perView, bool oneViewed, bool twoViewed, IReadOnlyList<int>? parents = null)
	{
		var pose = new SortedSet<int>(everyView);
		if (!perView)
		{
			if (oneViewed)
			{
				pose.UnionWith(playerOne);
			}

			if (twoViewed)
			{
				pose.UnionWith(playerTwo);
			}

			return new Plan([.. pose], Array.Empty<int>(), Array.Empty<int>());
		}

		var two = new HashSet<int>(playerTwo);
		foreach (int bone in playerOne)
		{
			if (two.Contains(bone))
			{
				pose.Add(bone);
			}
		}

		return new Plan([.. pose], Remainder(playerOne, pose, parents), Remainder(playerTwo, pose, parents));
	}

	/// <summary>
	/// The layers of a rig's body while view 0 (<paramref name="firstTwin"/>) and view 1
	/// (<paramref name="secondTwin"/>) draw twins: its own <paramref name="layers"/> with no twin, the
	/// other view alone with one, no view with both.
	/// </summary>
	public static uint BodyLayers(bool firstTwin, bool secondTwin, uint layers) =>
		firstTwin && secondTwin ? 0u
		: firstTwin ? Refii.Models.Engine_ClassicViews.OnlyIn(1, layers)
		: secondTwin ? Refii.Models.Engine_ClassicViews.OnlyIn(0, layers)
		: layers;

	/// <summary>The layers of view <paramref name="view"/>'s twin of a body whose layers are <paramref name="layers"/>: that view alone, lit as the body.</summary>
	public static uint TwinLayers(int view, uint layers) => Refii.Models.Engine_ClassicViews.OnlyIn(view, layers);

	private static int[] Remainder(IReadOnlyCollection<int> bones, SortedSet<int> pose, IReadOnlyList<int>? parents)
	{
		var left = new SortedSet<int>();
		foreach (int bone in bones)
		{
			if (!pose.Contains(bone) && !Under(bone, pose, parents))
			{
				left.Add(bone);
			}
		}

		return left.Count == 0 ? Array.Empty<int>() : [.. left];
	}

	/// <summary>Whether a strict ancestor of <paramref name="bone"/> is one of <paramref name="roots"/>.</summary>
	private static bool Under(int bone, SortedSet<int> roots, IReadOnlyList<int>? parents)
	{
		if (parents is null || roots.Count == 0 || bone < 0 || bone >= parents.Count)
		{
			return false;
		}

		for (int p = parents[bone], guard = 0; p >= 0 && p < parents.Count && guard <= parents.Count; p = parents[p], guard++)
		{
			if (roots.Contains(p))
			{
				return true;
			}
		}

		return false;
	}
}
