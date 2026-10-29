namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Mods;
using Refii.Titles.Fable2;

/// <summary>
/// A mod's input actions rebound in the game (hard_mods_plan H.16): Options › Controls › the mod's
/// title is a host options page with a key row and a pad row per action where the declaration
/// binds each, stepping Default, Unbound, then every candidate; A writes refii.toml
/// (<c>key_&lt;mod&gt;.&lt;action&gt;</c> / <c>pad_&lt;mod&gt;.&lt;action&gt;</c>, the shipped keys'
/// file) and a row at its default stores nothing, so the file's binding stands until the player
/// moves it. Key captions name keys through the engine, so only pad rows are captioned here.
/// </summary>
[TestSuite]
public class ModInputUiTests
{
    private static ModInput.Action Dash(string mod = "mymod") => new(
        mod + ".dash", mod, "Dash",
        new[] { Key.F }, new[] { JoyButton.LeftStick }, Array.Empty<JoyAxis>(), null, 0);

    private static ModInput.Action Aim(string mod = "mymod") => new(
        mod + ".aim", mod, "Aim",
        Array.Empty<Key>(), Array.Empty<JoyButton>(), new[] { JoyAxis.TriggerLeft }, JoyAxis.RightY, -1);

    private static ModInput.Action Look(string mod = "mymod") => new(
        mod + ".look", mod, "Look",
        Array.Empty<Key>(), Array.Empty<JoyButton>(), Array.Empty<JoyAxis>(), JoyAxis.RightX, 0);

    [TestCase]
    public void ARowPerDeviceTheDeclarationBinds()
    {
        ModInputUi.Row[] dash = ModInputUi.RowsFor(Dash()).ToArray();
        AssertThat(dash.Length).IsEqual(2);
        AssertThat(dash[0].Device).IsEqual(ModInputUi.Device.Keyboard);
        AssertThat(dash[0].Label).IsEqual("Dash (key)");
        AssertThat(dash[0].SettingKey).IsEqual("key_mymod.dash");
        AssertThat(dash[1].Device).IsEqual(ModInputUi.Device.Pad);
        AssertThat(dash[1].Label).IsEqual("Dash (pad)");
        AssertThat(dash[1].SettingKey).IsEqual("pad_mymod.dash");

        // A trigger counts as a pad binding; an axis alone is never rebound.
        ModInputUi.Row[] aim = ModInputUi.RowsFor(Aim()).ToArray();
        AssertThat(aim.Length).IsEqual(1);
        AssertThat(aim[0].Device).IsEqual(ModInputUi.Device.Pad);
        AssertThat(ModInputUi.RowsFor(Look()).Any()).IsFalse();
    }

    [TestCase]
    public void APadRowStepsDefaultUnboundThenEveryPadInput()
    {
        ModInputUi.Row pad = ModInputUi.RowsFor(Dash()).Last();
        AssertThat(pad.Choices).IsEqual(2 + ModInputUi.PadCandidates.Count);
        AssertThat(pad.Caption(ModInputUi.DefaultIndex)).IsEqual("Default (L3)");
        AssertThat(pad.Caption(ModInputUi.UnboundIndex)).IsEqual("Unbound");
        AssertThat(pad.Caption(2)).IsEqual("A");
        AssertThat(pad.Caption(pad.Choices - 1)).IsEqual("DpadRight");
        AssertThat(pad.Caption(pad.Choices)).IsEqual(string.Empty);

        // Stored text to index: absent or empty is the default, none is Unbound, a name is its
        // candidate (case-insensitive, the first of a comma list), an unknown name the default.
        int lb = 2 + ModInputUi.PadCandidates.ToList().IndexOf("LB");
        AssertThat(pad.IndexOfStored(null)).IsEqual(ModInputUi.DefaultIndex);
        AssertThat(pad.IndexOfStored("  ")).IsEqual(ModInputUi.DefaultIndex);
        AssertThat(pad.IndexOfStored("\"none\"")).IsEqual(ModInputUi.UnboundIndex);
        AssertThat(pad.IndexOfStored("lb")).IsEqual(lb);
        AssertThat(pad.IndexOfStored("LB,RT")).IsEqual(lb);
        AssertThat(pad.IndexOfStored("Guide")).IsEqual(ModInputUi.DefaultIndex);

        // Index to stored text: the default removes the key, Unbound stores none, else the name.
        AssertThat(pad.StoredAt(ModInputUi.DefaultIndex)).IsNull();
        AssertThat(pad.StoredAt(ModInputUi.UnboundIndex)).IsEqual(ModInput.Unbound);
        AssertThat(pad.StoredAt(lb)).IsEqual("LB");

        // The default caption of a trigger binding, and of several buttons.
        AssertThat(ModInputUi.RowsFor(Aim()).Single().Caption(0)).IsEqual("Default (LT)");
        var two = new ModInput.Action("m.x", "m", "X", Array.Empty<Key>(), new[] { JoyButton.A, JoyButton.RightShoulder }, new[] { JoyAxis.TriggerRight }, null, 0);
        AssertThat(ModInputUi.RowsFor(two).Single().Caption(0)).IsEqual("Default (A,RB,RT)");
    }

    [TestCase]
    public void EveryPadCandidateIsANameTheFileTakes()
    {
        foreach (string name in ModInputUi.PadCandidates)
        {
            bool known = ModInput.TryButton(name, out JoyButton button)
                ? string.Equals(ModInput.PadName(button), name, StringComparison.Ordinal)
                : ModInput.TryTrigger(name, out JoyAxis trigger) && ModInput.TriggerName(trigger) == name;
            AssertThat(known).OverrideFailureMessage(name).IsTrue();
        }

        AssertThat(ModInputUi.PadCandidates.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqual(ModInputUi.PadCandidates.Count);
        AssertThat(ModInputUi.KeyCandidates.Distinct().Count()).IsEqual(ModInputUi.KeyCandidates.Count);
        AssertThat(ModInputUi.KeyCandidates.Count > 60).IsTrue();
        AssertThat(ModInputUi.RowsFor(Dash()).First().Choices).IsEqual(2 + ModInputUi.KeyCandidates.Count);

        // The parse the rebind read uses: aliases and JoyButton names, triggers apart, the rest named.
        (List<JoyButton> buttons, List<JoyAxis> triggers, List<string> unknown) = ModInput.ParsePad("LB, RT,DpadUp,foo");
        AssertThat(buttons).ContainsExactly(JoyButton.LeftShoulder, JoyButton.DpadUp);
        AssertThat(triggers).ContainsExactly(JoyAxis.TriggerRight);
        AssertThat(unknown).ContainsExactly("foo");
        AssertThat(ModInput.Names(" a, ,b ,").ToArray()).ContainsExactly("a", "b");
    }

    [TestCase]
    public void APageReadsAndWritesTheShippedKeysFile()
    {
        var page = new ModInputUi.Page("mymod", "My Mod", ModInputUi.RowsFor(Dash()).ToList());
        AssertThat(page.Name).IsEqual("options:controls:mymod");
        AssertThat(page.Count).IsEqual(2);
        AssertThat(page.RowTitle(1)).IsEqual("Dash (pad)");

        var stored = new Dictionary<string, string> { ["pad_mymod.dash"] = "RB" };
        int[] read = page.Read(key => stored.TryGetValue(key, out string? v) ? v : string.Empty);
        AssertThat(read[0]).IsEqual(ModInputUi.DefaultIndex);
        AssertThat(read[1]).IsEqual(2 + ModInputUi.PadCandidates.ToList().IndexOf("RB"));

        // Committing: a row at its default removes its key, the others write their names.
        IReadOnlyDictionary<string, string?> values = page.Values(row => row == 0 ? ModInputUi.DefaultIndex : ModInputUi.UnboundIndex);
        AssertThat(values["key_mymod.dash"]).IsNull();
        AssertThat(values["pad_mymod.dash"]).IsEqual("none");
    }

    [TestCase]
    public void OnePagePerModWithSomethingToRebind()
    {
        IReadOnlyList<ModInputUi.Page> pages = ModInputUi.Pages(
            new[] { Dash("alpha"), Look("beta"), Aim("gamma"), Dash("alpha") with { Name = "alpha.roll", Label = "Roll" } },
            mod => mod.ToUpperInvariant());
        AssertThat(pages.Select(p => p.Mod).ToArray()).ContainsExactly("alpha", "gamma");
        AssertThat(pages[0].Title).IsEqual("ALPHA");
        AssertThat(pages[0].Count).IsEqual(4);
        AssertThat(pages[1].Count).IsEqual(1);
    }

    [TestCase]
    public void TheLauncherListsTheSameRowsWithAnEmptyDefault()
    {
        // The launcher's Controls group carries the mods' actions as "Controls: <title>" groups:
        // the same key_/pad_ names the game's page writes, an EMPTY default (what the game reads
        // as the mod's own), the mod's binding named in the description. Pad and axis rows only
        // here: a key row's description names keys through the engine.
        var rows = Refii.Launcher.Core.RuntimeCVarsGodot.ModBindings(
            new[] { Aim("alpha"), Look("alpha"), Aim("beta") with { Name = "beta.aim", Label = "Take aim" } },
            mod => mod == "alpha" ? "Alpha Mod" : mod).ToList();
        AssertThat(rows.Count).IsEqual(2);
        AssertThat(rows[0].Name).IsEqual("pad_alpha.aim");
        AssertThat(rows[0].Category).IsEqual("Controls: Alpha Mod");
        AssertThat(rows[0].Type).IsEqual(Refii.Launcher.Core.RuntimeCVarType.String);
        AssertThat(rows[0].DefaultValue).IsEqual(string.Empty);
        AssertThat(rows[0].Description.Contains("Aim on the pad", StringComparison.Ordinal)).IsTrue();
        AssertThat(rows[0].Description.Contains("empty keeps the mod's own (LT)", StringComparison.Ordinal)).IsTrue();
        AssertThat(rows[1].Name).IsEqual("pad_beta.aim");
        AssertThat(rows[1].Category).IsEqual("Controls: beta");
        AssertThat(rows[1].Description.Contains("Take aim on the pad", StringComparison.Ordinal)).IsTrue();
        AssertThat(Refii.Launcher.Core.RuntimeCVarsGodot.ModControlsCategory("X")).IsEqual("Controls: X");

        // The game reads an empty stored value as the file's binding, and the page shows it as Default.
        AssertThat(ModInputUi.RowsFor(Aim()).Single().IndexOfStored(string.Empty)).IsEqual(ModInputUi.DefaultIndex);

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string model = File.ReadAllText(Path.Combine(root, "Godot", "src", "launcher", "settings", "RuntimeSettingsModel.cs"));
        AssertThat(model.Contains("RuntimeCVarSet.Refresh();", StringComparison.Ordinal)).IsTrue();
        string set = File.ReadAllText(Path.Combine(root, "Godot", "src", "launcher", "settings", "RuntimeCVarSet.cs"));
        AssertThat(set.Contains("all.AddRange(RuntimeCVarsGodot.ModBindings().Where(mod => all.All(known => known.Name != mod.Name)));", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheControlsFoldAndTheRebindReadAreWired()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The page is registered under its command prefix, its rows are a menu source, the
        // options list folds the game's Controls row with that source, and the rebinds are read
        // where the shipped keys are (every two seconds from refii.toml, at once after a commit).
        string pages = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "ui", "Engine_UiHostOptionsPage.cs"));
        AssertThat(pages.Contains("Register(Refii.Mods.ModInputUi.OpenCommandPrefix, Refii.Mods.ModInputUi.PageFor);", StringComparison.Ordinal)).IsTrue();
        string sources = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "ui", "Engine_UiMenuSources.cs"));
        AssertThat(sources.Contains("[Refii.Mods.ModInputUi.SourceName] = Refii.Mods.ModInputUi.Rows,", StringComparison.Ordinal)).IsTrue();
        string templates = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "ui", "Engine_UiGameTemplates.cs"));
        AssertThat(templates.Contains("\"name\": \"ControlsSettings\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(templates.Contains("\"source\": \"mod-controls\"", StringComparison.Ordinal)).IsTrue();
        string input = File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameInput.cs"));
        AssertThat(input.Contains("Refii.Mods.ModInput.ReadBindings(values);", StringComparison.Ordinal)).IsTrue();
        string ui = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModInputUi.cs"));
        AssertThat(ui.Contains("Engine_UiScreenActions.WriteSettings(write, remove)", StringComparison.Ordinal)).IsTrue();
        // The limit is gone from the guide, and the guide says how to rebind.
        string folder = Path.Combine(root, "Godot", Refii.Mods.ModGuide.ProjectFolder);
        string docs = string.Join("\n", Refii.Mods.ModGuide.Files(folder).Select(f => File.ReadAllText(Path.Combine(folder, f))));
        AssertThat(File.ReadAllText(Path.Combine(folder, "settings-and-input.md")).Contains("### Rebinding", StringComparison.Ordinal)).IsTrue();
        AssertThat(docs.Contains("Rebinding a mod's input actions in the options menu.", StringComparison.Ordinal)).IsFalse();
        AssertThat(docs.Contains("A rebinding UI is not here yet", StringComparison.Ordinal)).IsFalse();
    }

    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }
}
