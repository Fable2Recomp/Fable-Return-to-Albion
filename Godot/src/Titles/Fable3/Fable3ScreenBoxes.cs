namespace Refii.Titles.Fable3;

/// <summary>
/// The box-manager screens showing now, and the element root the front one writes under: the
/// hooks' `screen:…` kind names the class (display_box_hooks.cpp), and each class writes its rows
/// under its own root, which <see cref="Fable3StatedScreen"/> draws.
/// </summary>
public static class Fable3ScreenBoxes
{
	/// <summary>Box-manager screens showing now (every `screen:…` class and the unnamed ones).</summary>
	public static int Showing { get; private set; }

	/// <summary>The element root of the front screen whose rows are element writes, or null (the options screen has its own view and names none).</summary>
	public static string? ScreenRoot { get; private set; }

	public static ulong Generation { get; private set; }

	/// <summary>
	/// The element root a screen class writes under — the first segment of every path its Show
	/// writes (sub_829758C8 `HeroStatus.Section1…`, sub_827A3398 `Promises.…`, sub_8296D4A0
	/// `StatsBox`, sub_82973AC8 `QuestSummary.ObjectiveTitle`, sub_82972FD8 `KingsSchedule.Title`).
	/// </summary>
	public static string? RootOf(string kind) => Fable3ScreenModel.RootOf(kind);

	public static void Shown(int delta, string? root = null)
	{
		Showing = System.Math.Max(0, Showing + delta);
		if (delta > 0)
		{
			ScreenRoot = root;
		}
		else if (Showing == 0 || root == ScreenRoot)
		{
			ScreenRoot = null;
		}

		++Generation;
	}
}
