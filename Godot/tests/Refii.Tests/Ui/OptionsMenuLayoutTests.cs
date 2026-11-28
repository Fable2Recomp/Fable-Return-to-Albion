namespace Refii.Tests.Ui;

using System.Linq;
using GdUnit4;
using Refii.Titles.Fable2;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>Checks long host options folds against the book's reserved footer slot.</summary>
[TestSuite]
public sealed class OptionsMenuLayoutTests
{
	/// <summary>The footer stays clear while every mod remains reachable and activatable.</summary>
	[TestCase]
	public void LongModFoldReservesTheFooterWithoutLosingRows()
	{
		Engine_UiTemplateSet templates = Fable2UiTemplates.AuthoredDefaults();
		Engine_UiMenuTemplate options = templates.Menus["OptionsMenu"];
		Engine_UiMenuEntry mods = options.Entry("ModSettings")!;
		mods.Source = string.Empty;
		for (int i = 0; i < 13; ++i)
		{
			mods.Children.Add(new Engine_UiMenuEntry
			{
				Name = $"mod-{i}", Tag = $"Mod {i}", HostOnly = true, Command = $"settings:{i}",
			});
		}

		Engine_FrontEndMenu menu = options.Build();
		menu.Highlight("ModSettings");
		menu.Activate();
		AssertThat(menu.Visible.Count(row => row.Name.StartsWith("mod-"))).IsEqual(13);

		foreach ((bool Top, bool Bottom)? stated in new (bool, bool)?[] { null, (true, false), (false, false) })
		{
			var fades = Fable2UiTemplates.MenuEndFades(18, stated) ??
				(templates.Screens[18].FadesTopEnd, templates.Screens[18].FadesBottomEnd);
			AssertThat(fades.Top).IsEqual(stated?.Top ?? templates.Screens[18].FadesTopEnd);
			for (int i = 0; i < 13; ++i)
			{
				menu.Highlight($"mod-{i}");
				int highlight = menu.HighlightIndex + 1;
				(int first, int last) = Engine_FrontEndMenuView.Window(highlight, menu.Visible.Count);
				for (int row = first; row <= last; ++row)
				{
					int slot = Engine_UiMenuFormatter.SlotFor(row, highlight);
					Engine_UiMenuConstants.Style style = slot == 11 && fades.Bottom
						? Engine_UiMenuConstants.Styles[4]
						: Engine_UiMenuConstants.Slots[slot - 1].Style;
					if (slot >= 11)
					{
						AssertThat(style.Opacity).IsEqual(0f);
					}
				}

				AssertThat(menu.Highlighted!.Name).IsEqual($"mod-{i}");
				AssertThat(menu.Activate()).IsEqual($"settings:{i}");
			}
		}
	}

	/// <summary>Other books retain the guest's choices and the absence of a stated population.</summary>
	[TestCase]
	public void OtherScreensKeepTheirStatedFades()
	{
		foreach (uint screen in new uint[] { 0, 5, 13, 22 })
		{
			AssertThat(Fable2UiTemplates.MenuEndFades(screen, null) is null).IsTrue();
			foreach (var stated in new[] { (false, false), (true, false), (false, true), (true, true) })
			{
				AssertThat(Fable2UiTemplates.MenuEndFades(screen, stated) == stated).IsTrue();
			}
		}
	}
}
