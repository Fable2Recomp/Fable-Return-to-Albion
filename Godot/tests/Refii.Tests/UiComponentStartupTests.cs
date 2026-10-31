using System;
using System.Collections.Generic;
using GdUnit4;
using Refii.Ui;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

namespace Refii.Tests;

/// <summary>Checks authoring export admission and on-demand art lifecycles without native Godot nodes.</summary>
[TestSuite]
public sealed class UiComponentStartupTests
{
    /// <summary>Android never invokes the exporter, so it cannot create directories or resolve unused art.</summary>
    [TestCase]
    public void AndroidStartupDoesNotExecuteTheAuthoringExport()
    {
        int calls = 0;
        Engine_UiComponentLibrary.Result? result = Engine_UiComponentLibrary.ExportAtStartup(true, () =>
        {
            ++calls;
            throw new InvalidOperationException("Authoring must not run in the Android player");
        });
        AssertThat(result).IsNull();
        AssertThat(calls).IsEqual(0);
    }

    /// <summary>Desktop runs the same export once and preserves its successes and failures for the existing report.</summary>
    [TestCase]
    public void DesktopStartupPreservesTheExportResult()
    {
        int calls = 0;
        var expected = new Engine_UiComponentLibrary.Result(22, 51, 3);
        var result = Engine_UiComponentLibrary.ExportAtStartup(false, () => { ++calls; return expected; });
        AssertThat(calls).IsEqual(1);
        AssertThat(result!.Value).IsEqual(expected);
        var empty = Engine_UiComponentLibrary.ExportAtStartup(false, () => default);
        AssertThat(empty.HasValue).IsTrue();
        AssertThat(empty!.Value.Screens).IsEqual(0);
    }

    /// <summary>The new admission policy must not swallow existing desktop authoring failures.</summary>
    [TestCase]
    public void DesktopAuthoringExceptionsStillReachTheCaller()
    {
        var expected = new InvalidOperationException("Synthetic export failure");
        Exception? caught = null;
        try { Engine_UiComponentLibrary.ExportAtStartup(false, () => throw expected); }
        catch (Exception error) { caught = error; }
        AssertThat(ReferenceEquals(caught, expected)).IsTrue();
    }

    /// <summary>
    /// The HUD's initial request survives startup; visiting and revisiting authored Options/Map records
    /// remains available afterward without eagerly requesting the rest of the authored catalogue.
    /// </summary>
    [TestCase]
    public void AndroidHudAndVisitedScreensRemainAvailableOnDemand()
    {
        var books = new Dictionary<string, Engine_UiTheme>(StringComparer.OrdinalIgnoreCase);
        var catalogue = Catalogue();
        Engine_UiTheme Resolve(string name)
        {
            if (!books.TryGetValue(name, out var book)) books.Add(name, book = new Engine_UiTheme());
            return book;
        }
        var hud = Resolve("hud");
        var result = Engine_UiComponentLibrary.ExportAtStartup(true, () =>
        {
            foreach (var screen in catalogue) Resolve(screen.Presentation);
            return new(books.Count, 0, 0);
        });
        AssertThat(result).IsNull();
        AssertThat(books.Count).IsEqual(1);

        var options = Array.Find(catalogue, screen => screen.Name == "options");
        var map = Array.Find(catalogue, screen => screen.Name == "worldmap");
        AssertThat(options).IsNotNull();
        AssertThat(map).IsNotNull();
        var optionsArt = Resolve(options!.Presentation);
        Resolve(map!.Presentation);
        AssertThat(books.Count).IsEqual(3);
        AssertThat(ReferenceEquals(Resolve("hud"), hud)).IsTrue();
        AssertThat(ReferenceEquals(Resolve(options.Presentation), optionsArt)).IsTrue();
        AssertThat(books.Count).IsEqual(3);
        AssertThat(books.ContainsKey("shopscreen")).IsFalse();

        // Use the real pure menu factory after admission, not a Godot view or a native scene.
        var menuTemplate = new Engine_UiMenuTemplate { Name = "pause-lifecycle" };
        menuTemplate.Entries.Add(Fable2UiTemplates.Tab("Options", "Options", Engine_PauseMenu.TabOptions));
        Engine_FrontEndMenu menu = menuTemplate.Build();
        AssertThat(menu.Activate()).IsEqual("PauseTab:8");
        AssertThat(menu.Back()).IsFalse();
    }

    /// <summary>Desktop still visits the whole authoring catalogue, including unvisited screens; empty records are skipped.</summary>
    [TestCase]
    public void DesktopStartupStillAdmitsTheFullAuthoringCatalogue()
    {
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = Engine_UiComponentLibrary.ExportAtStartup(false, () =>
        {
            int screens = 0;
            foreach (var screen in Catalogue())
            {
                if (screen.Presentation.Length == 0 || screen.ChromeUnits.Count == 0) continue;
                requested.Add(screen.Presentation);
                ++screens;
            }
            return new(screens, 0, 0);
        });
        AssertThat(result.HasValue).IsTrue();
        AssertThat(result!.Value.Screens > 3).IsTrue();
        AssertThat(requested.Contains("optionsscreen")).IsTrue();
        AssertThat(requested.Contains("worldmapscreen")).IsTrue();
        AssertThat(requested.Contains("shopscreen")).IsTrue();
    }

    /// <summary>
    /// Synthetic authored records keep the CLI test independent of Godot's native override-file
    /// reader. The records exercise the same presentation/chrome admission fields as ExportAll.
    /// </summary>
    private static Engine_UiScreenTemplate[] Catalogue() => new[]
    {
        new Engine_UiScreenTemplate { Name = "hud", Presentation = "hud", ChromeUnits = { "Meters" } },
        new Engine_UiScreenTemplate { Name = "options", Presentation = "optionsscreen", ChromeUnits = { "Rows" } },
        new Engine_UiScreenTemplate { Name = "worldmap", Presentation = "worldmapscreen", ChromeUnits = { "Map" } },
        new Engine_UiScreenTemplate { Name = "shop", Presentation = "shopscreen", ChromeUnits = { "Rows" } },
        new Engine_UiScreenTemplate { Name = "without-art", Presentation = "unusedscreen" },
        new Engine_UiScreenTemplate { Name = "without-presentation", ChromeUnits = { "Unused" } },
    };
}
