using System.Collections.Generic;
using GdUnit4;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks command activation gates and the pause menu's host/guest row lifecycle.</summary>
[TestSuite]
public sealed class PauseMenuActivationTests
{
    /// <summary>An explicitly disabled leaf cannot dispatch a native or host-only command.</summary>
    [TestCase]
    public void GhostedCommandsAreRefusedUntilAvailabilityReturns()
    {
        foreach (bool hostOnly in new[] { false, true })
        {
            var menu = new Engine_FrontEndMenu();
            var item = new Engine_UiExpandableMenu.Item("Options", "Options")
            {
                Command = hostOnly ? "PauseOwned:1013" : "PauseTab:8",
                HostOnly = hostOnly,
                Ghosted = true,
            };
            menu.Add(item);
            AssertThat(menu.Activate()).IsNull();
            item.Ghosted = false;
            AssertThat(menu.Activate()).IsEqual(item.Command);
        }
    }

    /// <summary>A fold's input gate also blocks leaves; a visual auto-ghost alone does not.</summary>
    [TestCase]
    public void CommandActivationWaitsForAnimationButIgnoresAutoGhosting()
    {
        foreach (bool hostOnly in new[] { false, true })
        {
            var menu = new Engine_FrontEndMenu();
            var item = new Engine_UiExpandableMenu.Item("Options", "Options")
            {
                Command = hostOnly ? "PauseOwned:1013" : "PauseTab:8",
                HostOnly = hostOnly,
                AutoGhosted = true,
            };
            menu.Add(item);
            AssertThat(item.AutoGhosted).IsTrue();
            menu.Model.Animating = true;
            AssertThat(menu.Activate()).IsNull();
            menu.Model.Animating = false;
            AssertThat(menu.Activate()).IsEqual(item.Command);
        }
    }

    /// <summary>No selection, disabled openers and blocked folds cannot arm a screen transition.</summary>
    [TestCase]
    public void ScreenOpeningRequiresAnAcceptedLeaf()
    {
        var menu = new Engine_FrontEndMenu();
        AssertThat(menu.CanActivate).IsFalse();
        AssertThat(menu.Activate(out string? screen)).IsNull();
        AssertThat(screen).IsNull();
        var opener = new Engine_UiExpandableMenu.Item("Details", "Details") { Opens = "DetailsBook" };
        menu.Add(opener);
        foreach (bool animated in new[] { false, true })
        {
            menu.Model.Animating = animated;
            opener.Ghosted = !animated;
            AssertThat(menu.CanActivate).IsFalse();
            AssertThat(menu.Activate(out screen)).IsNull();
            AssertThat(screen).IsNull();
        }
        menu.Model.Animating = false;
        opener.Ghosted = false;
        opener.AutoGhosted = true;
        AssertThat(menu.CanActivate).IsTrue();
        AssertThat(menu.Activate(out screen)).IsNull();
        AssertThat(screen).IsEqual("DetailsBook");
    }

    /// <summary>The opener decision also waits for the existing profile/storage checks, then recovers normally.</summary>
    [TestCase]
    public void HeroCardOpeningStillHonorsStorageRefusal()
    {
        var menu = new Engine_FrontEndMenu();
        menu.Add(new Engine_UiExpandableMenu.Item("New Game", "New Game")
            { Opens = Engine_FrontEndMenu.HeroCardScreen });
        AssertThat(menu.Activate(out string? screen)).IsNull();
        AssertThat(menu.Warning).IsEqual(Engine_FrontEndMenu.NoProfileWarning);
        AssertThat(screen).IsNull();
        var model = new Engine_UiModel.FrontEndModel(true, 0, 1, 0, false, true, 0, 0, true, 0, 0);
        menu.ApplyAvailability(model, new Engine_UiModel.SaveState(true, 1, 6));
        AssertThat(menu.Activate(out screen)).IsNull();
        AssertThat(menu.Warning).IsNull();
        AssertThat(screen).IsEqual(Engine_FrontEndMenu.HeroCardScreen);
        menu.Highlighted!.Ghosted = true;
        AssertThat(menu.Activate(out screen)).IsNull();
        AssertThat(screen).IsNull();
    }

    /// <summary>A row with children folds instead of also opening its independently named surface.</summary>
    [TestCase]
    public void GroupToggleTakesPrecedenceOverScreenOpening()
    {
        var menu = new Engine_FrontEndMenu();
        var group = new Engine_UiExpandableMenu.Item("Group", "Group") { Opens = "DetailsBook" };
        group.Children.Add(new Engine_UiExpandableMenu.Item("Child", "Child") { Opens = "ChildBook" });
        menu.Add(group);
        menu.Model.Animating = true;
        AssertThat(menu.Activate(out string? screen)).IsNull();
        AssertThat(group.IsOpen).IsFalse();
        AssertThat(screen).IsNull();
        menu.Model.Animating = false;
        AssertThat(menu.Activate(out screen)).IsNull();
        AssertThat(group.IsOpen).IsTrue();
        AssertThat(menu.Highlighted!.Name).IsEqual("Child");
        AssertThat(screen).IsNull();
        AssertThat(menu.Activate(out screen)).IsNull();
        AssertThat(screen).IsEqual("ChildBook");
        AssertThat(menu.Back()).IsTrue();
        AssertThat(menu.Highlighted!.Name).IsEqual("Group");
    }

    /// <summary>The real tab factory produces command rows even when a spawn source currently has no children.</summary>
    [TestCase]
    public void SourcedPauseTabsAreNotEmptySubmenus()
    {
        var template = new Engine_UiMenuTemplate { Name = "pause-tab-unit" };
        template.Entries.Add(Engine_UiTemplates.Tab("Map", "Map", Engine_PauseMenu.TabMap));
        template.Entries.Add(Engine_UiTemplates.Tab("Options", "Options", Engine_PauseMenu.TabOptions));
        Engine_FrontEndMenu menu = template.Build();
        foreach (Engine_UiExpandableMenu.Item item in menu.Roots)
        {
            AssertThat(item.EventType).IsEqual((int)Engine_UiMenuConstants.EventType.EnterScreen);
            AssertThat(item.Children.Count).IsEqual(0);
            AssertThat(item.AutoGhosted).IsFalse();
            AssertThat(item.IsGhosted).IsFalse();
        }
        menu.Highlight("Options");
        AssertThat(menu.Activate()).IsEqual("PauseTab:8");
    }

    /// <summary>Label joins, restriction changes, host tabs and Saving folds preserve the same ten-row pause list.</summary>
    [TestCase]
    public void PausePopulationNavigationAndAvailabilityLifecycle()
    {
        Engine_FrontEndMenu menu = PauseFixture();
        var buttons = new List<Engine_ExpandableMenuItems.Button>();
        var stated = new List<Engine_UiMenuModel.Row>();
        string[] guestNames = { "Saving", "Map", "Abilities", "Items", "Weapons", "Clothing", "Logbook", "Options" };
        string[] labels = { "Save", "Quests/Maps!", "Abilities (4)", "Items!", "Weapons!", "Clothing!", "Logbook", "Options" };
        for (int i = 0; i < guestNames.Length; ++i)
        {
            buttons.Add(new(i, "MenuItemButton" + (i + 1), i, labels[i], "DEFAULT_TEXT_ID", true, 0));
            stated.Add(new() { Name = guestNames[i], Parent = "Root", Label = labels[i] });
        }
        AssertThat(Engine_PauseMenu.ApplyStatedLabels(menu, buttons, stated)).IsTrue();
        AssertThat(menu.Visible.Count).IsEqual(10);
        AssertThat(menu.Visible[7].Name).IsEqual("Achievements");
        AssertThat(menu.Visible[8].Name).IsEqual("Multiplayer");
        AssertThat(menu.Visible[9].Name).IsEqual("Options");
        AssertThat(menu.Find("Map")!.StatedLabel).IsEqual("Quests/Maps!");
        AssertThat(menu.Find("Options")!.StatedLabel).IsEqual("Options");
        AssertThat(menu.Find("Achievements")!.StatedLabel).IsNull();
        AssertThat(menu.Find("Multiplayer")!.StatedLabel).IsNull();

        // Eight distinct down edges take the initial Map highlight to Options, through both host rows.
        for (int i = 0; i < 8; ++i)
        {
            AssertThat(menu.Navigate(1, 1.0 / 30)).IsEqual(1);
            menu.Navigate(0, 1.0 / 30);
        }
        AssertThat(menu.Highlighted!.Name).IsEqual("Options");
        AssertThat(menu.Activate()).IsEqual("PauseTab:8");
        AssertThat(Engine_PauseMenu.ApplyTabAvailability(menu, 0xFCu)).IsTrue();
        AssertThat(menu.Visible.Count).IsEqual(10);
        AssertThat(menu.CanActivate).IsFalse();
        AssertThat(menu.Activate()).IsNull();
        AssertThat(Engine_PauseMenu.ApplyTabAvailability(menu, 0x1FCu)).IsTrue();
        AssertThat(menu.Activate()).IsEqual("PauseTab:8");
        menu.Highlight("Achievements");
        AssertThat(menu.Activate()).IsEqual("PauseOwned:1013");
        menu.Highlight("Multiplayer");
        AssertThat(menu.Activate()).IsEqual("PauseOwned:1014");

        menu.Highlight("Saving");
        AssertThat(menu.Activate()).IsNull();
        AssertThat(menu.CanCollapse).IsTrue();
        AssertThat(menu.Highlighted!.Name).IsEqual("SaveAndContinue");
        AssertThat(menu.Back()).IsTrue();
        AssertThat(menu.Highlighted!.Name).IsEqual("Saving");
        AssertThat(menu.Visible.Count).IsEqual(10);
        menu.Highlight("Options");
        AssertThat(menu.Highlighted!.StatedLabel).IsEqual("Options");
        AssertThat(menu.Activate()).IsEqual("PauseTab:8");
    }

    /// <summary>The early positional label fallback also skips host-only entries before Options.</summary>
    [TestCase]
    public void PoolOnlyLabelsDoNotShiftAcrossHostTabs()
    {
        Engine_FrontEndMenu menu = PauseFixture();
        var buttons = new List<Engine_ExpandableMenuItems.Button>();
        string[] labels = { "Save", "Map", "Abilities", "Items", "Weapons", "Clothing", "Logbook", "Options from guest" };
        for (int i = 0; i < labels.Length; ++i)
            buttons.Add(new(i, "MenuItemButton" + (i + 1), i, labels[i], "DEFAULT_TEXT_ID", true, 0));
        AssertThat(Engine_PauseMenu.ApplyStatedLabels(menu, buttons, new List<Engine_UiMenuModel.Row>())).IsTrue();
        AssertThat(menu.Find("Options")!.StatedLabel).IsEqual("Options from guest");
        AssertThat(menu.Find("Achievements")!.StatedLabel).IsNull();
        AssertThat(menu.Find("Multiplayer")!.StatedLabel).IsNull();
    }

    /// <summary>Builds a synthetic population using the production tab factory and both host-only commands.</summary>
    private static Engine_FrontEndMenu PauseFixture()
    {
        var template = new Engine_UiMenuTemplate { Name = "pause-lifecycle", Highlight = "Map" };
        var saving = new Engine_UiMenuEntry { Name = "Saving", Tag = "Save" };
        saving.Children.Add(new() { Name = "SaveAndContinue", Tag = "Save and Continue", Command = "PauseSaving:0" });
        saving.Children.Add(new() { Name = "SaveAndQuit", Tag = "Save and Quit", Command = "PauseSaving:1" });
        template.Entries.Add(saving);
        template.Entries.Add(Engine_UiTemplates.Tab("Map", "Map", Engine_PauseMenu.TabMap));
        template.Entries.Add(Engine_UiTemplates.Tab("Abilities", "Abilities", Engine_PauseMenu.TabAbilities));
        template.Entries.Add(Engine_UiTemplates.Tab("Items", "Items", Engine_PauseMenu.TabItems));
        template.Entries.Add(Engine_UiTemplates.Tab("Weapons", "Weapons", Engine_PauseMenu.TabWeapons));
        template.Entries.Add(Engine_UiTemplates.Tab("Clothing", "Clothing", Engine_PauseMenu.TabClothing));
        template.Entries.Add(Engine_UiTemplates.Tab("Logbook", "Logbook", Engine_PauseMenu.TabLogbook));
        template.Entries.Add(new() { Name = "Achievements", Tag = "Achievements", HostOnly = true, Command = "PauseOwned:1013" });
        template.Entries.Add(new() { Name = "Multiplayer", Tag = "Multiplayer", HostOnly = true, Command = "PauseOwned:1014" });
        template.Entries.Add(Engine_UiTemplates.Tab("Options", "Options", Engine_PauseMenu.TabOptions));
        return template.Build();
    }
}
