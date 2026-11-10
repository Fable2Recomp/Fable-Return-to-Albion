namespace Refii.Tests.Modding;

using System.IO;
using GdUnit4;
using Refii.Mods;
using static GdUnit4.Assertions;

/// <summary>The controller's mod wheel: each mod's entry takes the next wedge, and the south wedge stays Close.</summary>
[TestSuite]
public class ModWheelTests
{
    private static string Read(string root, string mod, params string[] parts)
    {
        string path = GodotModScripts.ModFolder(root, mod);
        foreach (string part in parts) path = Path.Combine(path, part);
        return File.ReadAllText(path);
    }

    [TestCase]
    public void EntriesFillTheWedgesInOrder()
    {
        AssertThat(ModWheel.EntryAt(0, 1, 3)).IsEqual(0);
        AssertThat(ModWheel.EntryAt(0, 3, 3)).IsEqual(2);
        AssertThat(ModWheel.EntryAt(0, 4, 3)).IsEqual(-1);
    }

    [TestCase]
    public void TheCloseAndDogWedgesAreNeverEntries()
    {
        AssertThat(ModWheel.EntryAt(0, ModWheel.CloseSector, 20)).IsEqual(-1);
        AssertThat(ModWheel.EntryAt(0, 0, 20)).IsEqual(-1);
        AssertThat(ModWheel.EntryAt(0, -1, 20)).IsEqual(-1);
    }

    [TestCase]
    public void ASeventhEntryGoesOnASecondPage()
    {
        AssertThat(ModWheel.PageCount(0)).IsEqual(1);
        AssertThat(ModWheel.PageCount(ModWheel.Slots)).IsEqual(1);
        AssertThat(ModWheel.PageCount(ModWheel.Slots + 1)).IsEqual(2);
        AssertThat(ModWheel.EntryAt(1, 1, ModWheel.Slots + 1)).IsEqual(ModWheel.Slots);
        AssertThat(ModWheel.EntryAt(1, 2, ModWheel.Slots + 1)).IsEqual(-1);
    }

    [TestCase]
    public void TheTopWedgeIsTheCoresAndTheModsRunOnPastIt()
    {
        AssertThat(ModWheel.TopSector).IsEqual(3);
        AssertThat(ModWheel.EntryAt(0, ModWheel.TopSector, 20, topTaken: true)).IsEqual(-1);
        AssertThat(ModWheel.EntryAt(0, 1, 20, topTaken: true)).IsEqual(0);
        AssertThat(ModWheel.EntryAt(0, 2, 20, topTaken: true)).IsEqual(1);
        AssertThat(ModWheel.EntryAt(0, 4, 20, topTaken: true)).IsEqual(2);
        AssertThat(ModWheel.EntryAt(0, 6, 20, topTaken: true)).IsEqual(4);
        AssertThat(ModWheel.EntryAt(1, 1, 20, topTaken: true)).IsEqual(5);
        AssertThat(ModWheel.EntryAt(0, ModWheel.CloseSector, 20, topTaken: true)).IsEqual(-1);
    }

    [TestCase]
    public void EachPlayerHasTheirOwnWheel()
    {
        ModWheel.Fixed? before = ModWheel.Top;
        int chosen = 0;
        try
        {
            ModWheel.SetTop(new ModWheel.Fixed("Photo Mode", string.Empty, () => chosen++));
            ModWheel.Close(1);
            ModWheel.Close(2);

            AssertThat(ModWheel.Follow(2, back: true, inWorld: true, out bool held)).IsTrue();
            AssertThat(held).IsTrue();
            AssertThat(ModWheel.IsOpen(2)).IsTrue();
            AssertThat(ModWheel.IsOpen(1)).IsFalse();
            AssertThat(ModWheel.OpenPlayer).IsEqual(2);

            ModWheel.Choose(2, ModWheel.TopSector);
            AssertThat(chosen).IsEqual(1);
            AssertThat(ModWheel.IsOpen(2)).IsFalse();
            AssertThat(ModWheel.OpenPlayer).IsEqual(0);

            AssertThat(ModWheel.Follow(2, back: false, inWorld: true, out _)).IsFalse();
        }
        finally
        {
            ModWheel.Close(2);
            ModWheel.SetTop(before);
        }
    }

    [TestCase]
    public void WithTheTopTakenAPageHoldsFive()
    {
        AssertThat(ModWheel.FreeSlots(topTaken: true)).IsEqual(ModWheel.Slots - 1);
        AssertThat(ModWheel.PageCount(5, topTaken: true)).IsEqual(1);
        AssertThat(ModWheel.PageCount(6, topTaken: true)).IsEqual(2);
        AssertThat(ModWheel.EntryAt(1, 1, 6, topTaken: true)).IsEqual(5);
        AssertThat(ModWheel.EntryAt(1, 2, 6, topTaken: true)).IsEqual(-1);
    }

    [TestCase]
    public void PhotoModeIsOnTheTopWedgeWithItsIcon()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string wheel = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "game", "GameUi.ModWheel.cs"));
        AssertThat(wheel).Contains("ModWheel.SetTop(new ModWheel.Fixed(\"Photo Mode\", PhotoModeWheelIcon, GamePhotoMode.RequestOpen))");
        AssertThat(File.Exists(Path.Combine(root, "Godot", "assets", "ui", "wheel", "photo_mode.png"))).IsTrue();
    }

    [TestCase]
    public void PagesWrapBothWays()
    {
        AssertThat(ModWheel.Stepped(0, -1, 3)).IsEqual(2);
        AssertThat(ModWheel.Stepped(2, 1, 3)).IsEqual(0);
        AssertThat(ModWheel.Stepped(0, 1, 1)).IsEqual(0);
    }

    /// <summary>The mods' controller shortcuts are wheel entries now, with no button of their own.</summary>
    [TestCase]
    public void TheBundledShortcutsAreOnTheWheel()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        foreach ((string mod, string entry) in new[]
        {
            ("quicksave", "\"wheel\": \"{QUICKSAVE_TOUCH_QUICKSAVE}\""),
            ("quicksave", "\"wheel\": \"{QUICKSAVE_TOUCH_QUICKLOAD}\""),
            ("skate", "\"wheel\": \"{SKATE_TOUCH_TOGGLE}\""),
            ("portal_gun", "\"wheel\": \"{PORTAL_GUN_TOUCH_TOGGLE}\""),
            ("satchel", "\"wheel\": \"{SATCHEL_TOUCH_OPEN}\""),
            ("minimap", "\"wheel\": true"),
        })
        {
            string input = Read(root, mod, "input.json");
            AssertThat(input).Contains(entry);
            AssertThat(input).NotContains("\"chord\": \"LB\"");
        }
    }

    /// <summary>Every entry names a picture its mod really ships.</summary>
    [TestCase]
    public void EveryBundledEntryShipsItsIcon()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        foreach ((string mod, string icon) in new[]
        {
            ("quicksave", "quicksave.png"), ("quicksave", "quickload.png"), ("skate", "skateboard.png"),
            ("minimap", "map.png"), ("portal_gun", "portal.png"), ("satchel", "satchel.png"),
        })
        {
            AssertThat(Read(root, mod, "input.json")).Contains($"\"wheel_icon\": \"art/gui/wheel/{icon}\"");
            AssertThat(File.Exists(Path.Combine(GodotModScripts.ModFolder(root, mod), "art", "gui", "wheel", icon))).IsTrue();
        }
    }

    /// <summary>A wheel entry is a binding: an action with nothing else bound still loads and validates.</summary>
    [TestCase]
    public void AWheelEntryAloneCountsAsABinding()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string mods = Path.Combine(root, "Godot", "src", "Engine", "mods");
        AssertThat(File.ReadAllText(Path.Combine(mods, "Engine_ModInput.cs"))).Contains("touch is not { Pill.Length: > 0 } && wheel.Length == 0");
        AssertThat(File.ReadAllText(Path.Combine(mods, "Engine_ModValidate.cs"))).Contains("wheel.ValueKind == System.Text.Json.JsonValueKind.True");
    }

    /// <summary>The expression wheel gives Back up to the mods' wheel, and A chooses without the wheel's own select.</summary>
    [TestCase]
    public void BackDrivesTheModWheelInsteadOfHoldingTheExpressionWheel()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string game = Path.Combine(root, "Godot", "src", "Titles", "Fable2", "game");
        AssertThat(File.ReadAllText(Path.Combine(game, "Fable2Input.cs")))
            .Contains("Refii.Mods.ModWheel.Follow(wheel, Instance.WorldHasInput, out bool held)");
        string ui = File.ReadAllText(Path.Combine(game, "GameUi.cs"));
        AssertThat(ui).Contains("if (modWheel)\n\t\t\t\t{\n\t\t\t\t\tFollowModWheelButtons();");
        AssertThat(ui).Contains("if (clicked && !modWheel)");
    }
}
