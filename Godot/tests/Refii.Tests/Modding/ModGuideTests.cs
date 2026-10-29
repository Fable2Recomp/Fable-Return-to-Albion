namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// The modders' guide (Godot/docs/modding, shipped as the SDK's docs/modding) and the Mod Studio's
/// Modding Guide tab, held through their pure models: every guide file exists and is reached, every link
/// between them lands on a file and a heading that exist, every event is in the events reference, the
/// markdown model reads what the guide is written in, and every page of the tab has content.
/// </summary>
[TestSuite]
public class ModGuideTests
{
    /// <summary>The chapters and references the index promises; a rename must change this list too.</summary>
    private static readonly string[] Chapters =
    {
        "README.md", "events.md", "handles-and-players.md", "settings-and-input.md", "ui-and-hud.md", "effects.md",
        "gameplay.md", "gdb-and-text.md", "models-and-textures.md", "cutscenes.md", "saves-and-sharing.md",
        "live-iteration.md", "reference-events.md", "reference-calls.md", "reference-commands.md",
    };

    private static string? GuideFolder()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return Path.Combine(at, "Godot", ModGuide.ProjectFolder);
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    [TestCase]
    public void EveryGuideFileExistsAndTheIndexReachesThemInOrder()
    {
        if (GuideFolder() is not { } folder)
        {
            return;
        }

        IReadOnlyList<string> files = ModGuide.Files(folder);
        AssertThat(files.Count > 0).OverrideFailureMessage("no guide files under " + folder).IsTrue();
        AssertThat(files[0]).IsEqual(ModGuide.IndexFile);
        foreach (string chapter in Chapters)
        {
            AssertThat(files.Contains(chapter)).OverrideFailureMessage("missing guide file " + chapter).IsTrue();
        }

        // Each file has a title and a table of contents, and none is empty.
        foreach (string file in files)
        {
            ModGuideDocument doc = ModGuide.Load(folder, file);
            AssertThat(doc.Headings.Count(h => h.Level == 1)).OverrideFailureMessage(file + ": one title").IsEqual(1);
            AssertThat(doc.Blocks.Count > 3).OverrideFailureMessage(file + ": has content").IsTrue();
            AssertThat(doc.Headings.Any(h => h.Text == "Contents")).OverrideFailureMessage(file + ": a Contents section").IsTrue();
        }
    }

    [TestCase]
    public void EveryLinkBetweenGuideFilesResolves()
    {
        if (GuideFolder() is not { } folder)
        {
            return;
        }

        IReadOnlyList<string> problems = ModGuide.Problems(folder);
        AssertThat(problems.Count).OverrideFailureMessage("broken guide links:\n" + string.Join("\n", problems)).IsEqual(0);

        // Every Contents entry is a link to a heading of its own file.
        foreach (string file in ModGuide.Files(folder))
        {
            ModGuideDocument doc = ModGuide.Load(folder, file);
            AssertThat(doc.Links().Count(l => !l.External) > 0).OverrideFailureMessage(file + ": links nowhere").IsTrue();
        }
    }

    [TestCase]
    public void TheGuideKeepsNoInternalArchaeology()
    {
        if (GuideFolder() is not { } folder)
        {
            return;
        }

        foreach (string file in ModGuide.Files(folder))
        {
            string text = File.ReadAllText(Path.Combine(folder, file));
            foreach (string mark in new[] { "sub_8", "docs/mod_api.md", "(r3 ", "TU1 0x", "disc 0x" })
            {
                AssertThat(text.Contains(mark, StringComparison.Ordinal)).OverrideFailureMessage($"{file} still says '{mark}'").IsFalse();
            }
        }
    }

    [TestCase]
    public void EveryEventIsInTheEventsReference()
    {
        if (GuideFolder() is not { } folder)
        {
            return;
        }

        ModGuideDocument doc = ModGuide.Load(folder, "reference-events.md");
        ModGuideBlock table = doc.Blocks.Single(b => b.Kind == ModGuideBlockKind.Table && b.Rows![0][0] == "#");
        var rows = table.Rows!.Skip(1).ToDictionary(r => int.Parse(r[0]), r => r);
        AssertThat(rows.Count).IsEqual(ModApi.Events.Count);
        foreach (ModApiEvent e in ModApi.Events)
        {
            AssertThat(rows.TryGetValue(e.Kind, out IReadOnlyList<string>? row)).OverrideFailureMessage($"event {e.Kind} {e.Name}").IsTrue();
            AssertThat(row![1]).OverrideFailureMessage($"event {e.Kind}").IsEqual($"{e.Name} / {e.LuaName}");
            AssertThat(row[3].Length != 0).OverrideFailureMessage($"{e.Name}: the guide's Cancel column against the catalog").IsEqual(e.Cancellable);
        }
    }

    [TestCase]
    public void TheMarkdownModelReadsWhatTheGuideIsWrittenIn()
    {
        const string md =
            "# Title\n\n" +
            "## Contents\n\n" +
            "- [Two words](#two-words)\n" +
            "  continued here\n" +
            "  - nested `code [x]`\n" +
            "1. first\n\n" +
            "Para with **bold**, *it*, a [link](other.md#a-b) and [web](https://example.com).\n\n" +
            "   ```lua\n" +
            "   local x = {1}\n" +
            "   ```\n\n" +
            "| A | B |\n|---|---|\n| `a|b` | x \\| y |\n\n" +
            "## Two words\n\n## Two words\n";
        ModGuideDocument doc = ModGuide.Parse("t.md", md);
        AssertThat(doc.Title).IsEqual("Title");
        AssertThat(doc.Headings.Select(h => h.Anchor).ToArray()).ContainsExactly("title", "contents", "two-words", "two-words-1");

        ModGuideBlock[] items = doc.Blocks.Where(b => b.Kind == ModGuideBlockKind.ListItem).ToArray();
        AssertThat(items.Length).IsEqual(3);
        AssertThat(items[0].Text).IsEqual("[Two words](#two-words) continued here");
        AssertThat(items[1].Level).IsEqual(1);
        AssertThat(items[2].Marker).IsEqual("1.");

        ModGuideBlock code = doc.Blocks.Single(b => b.Kind == ModGuideBlockKind.Code);
        AssertThat(code.Language).IsEqual("lua");
        AssertThat(code.Text).IsEqual("local x = {1}");

        ModGuideBlock table = doc.Blocks.Single(b => b.Kind == ModGuideBlockKind.Table);
        AssertThat(table.Rows![1].ToArray()).ContainsExactly("`a|b`", "x | y");

        ModGuideLink[] links = doc.Links().ToArray();
        AssertThat(links.Length).IsEqual(3);
        AssertThat(links[0].File).IsEqual("t.md");
        AssertThat(links[0].Anchor).IsEqual("two-words");
        AssertThat(links[1].File).IsEqual("other.md");
        AssertThat(links[1].Anchor).IsEqual("a-b");
        AssertThat(links[2].External).IsTrue();

        AssertThat(ModGuide.Anchor("Mod effects: fx/effects/*.json")).IsEqual("mod-effects-fxeffectsjson");
        AssertThat(ModGuide.Anchor("Each villager's opinion of each hero")).IsEqual("each-villagers-opinion-of-each-hero");
        AssertThat(ModGuide.Anchor("play_fx")).IsEqual("play_fx");

        AssertThat(ModGuide.TryParseLinkMeta(ModGuide.LinkMeta("events.md", "verdicts"), out string file, out string anchor)).IsTrue();
        AssertThat(file).IsEqual("events.md");
        AssertThat(anchor).IsEqual("verdicts");
    }

    [TestCase]
    public void TheBbCodeEscapesTextAndMapsEveryAnchor()
    {
        ModGuideDocument doc = ModGuide.Parse("t.md",
            "# T\n\nA [bracket] and `Refii.on(\"hit\")` and [go](#second).\n\n```\nx[1] = 2\n```\n\n| H |\n|---|\n| c |\n\n## Second\n");
        ModGuideRendered rendered = ModGuide.ToBbCode(doc);
        AssertThat(rendered.BbCode).Contains("[lb]bracket]");
        AssertThat(rendered.BbCode).Contains("x[lb]1] = 2");
        AssertThat(rendered.BbCode).Contains("[url=guide:t.md#second]");
        AssertThat(rendered.BbCode).Contains("[table=1]");
        AssertThat(rendered.AnchorParagraphs["t"]).IsEqual(0);

        // The paragraph a heading maps to is the number of line breaks before it.
        int second = rendered.AnchorParagraphs["second"];
        int at = rendered.BbCode.IndexOf("Second", StringComparison.Ordinal);
        AssertThat(rendered.BbCode[..at].Count(c => c == '\n')).IsEqual(second);

        // Tags open and close in pairs.
        foreach (string tag in new[] { "b", "code", "url", "table", "cell", "indent", "color", "font_size", "bgcolor" })
        {
            int open = CountOpen(rendered.BbCode, tag);
            int close = CountOf(rendered.BbCode, "[/" + tag + "]");
            AssertThat(open).OverrideFailureMessage(tag).IsEqual(close);
        }
    }

    [TestCase]
    public void EveryGuideFileRendersWithBalancedTags()
    {
        if (GuideFolder() is not { } folder)
        {
            return;
        }

        foreach (string file in ModGuide.Files(folder))
        {
            ModGuideDocument doc = ModGuide.Load(folder, file);
            ModGuideRendered rendered = ModGuide.ToBbCode(doc);
            AssertThat(rendered.BbCode.Length > 200).OverrideFailureMessage(file).IsTrue();
            foreach (ModGuideHeading heading in doc.Headings)
            {
                AssertThat(rendered.AnchorParagraphs.ContainsKey(heading.Anchor)).OverrideFailureMessage(file + "#" + heading.Anchor).IsTrue();
            }

            foreach (string tag in new[] { "b", "i", "code", "url", "table", "cell", "indent", "color", "font_size", "bgcolor" })
            {
                AssertThat(CountOpen(rendered.BbCode, tag)).OverrideFailureMessage(file + ": [" + tag + "]")
                    .IsEqual(CountOf(rendered.BbCode, "[/" + tag + "]"));
            }
        }
    }

    [TestCase]
    public void EveryPageOfTheGuideTabHasContent()
    {
        AssertThat(ModGuidePages.All.Count).IsEqual(9);
        AssertThat(ModGuidePages.All.Select(p => p.Key).Distinct().Count()).IsEqual(ModGuidePages.All.Count);
        foreach (ModGuidePage page in ModGuidePages.All)
        {
            AssertThat(string.IsNullOrWhiteSpace(page.Button)).OverrideFailureMessage(page.Key + ": a button caption").IsFalse();
            foreach (ModGuideCard card in page.Cards.Concat(page.Closing))
            {
                AssertThat(card.Title.Length > 0 && card.Body.Length > 40).OverrideFailureMessage(page.Key + ": " + card.Title).IsTrue();
            }

            if (page.Key == ModGuidePages.GuideKey)
            {
                continue; // its content is the guide's files, held above
            }

            AssertThat(page.Cards.Count > 0).OverrideFailureMessage(page.Key + ": no cards").IsTrue();
        }

        // The events page: one card per catalog event, each naming its event and its fields.
        foreach (ModApiEvent e in ModApi.Events)
        {
            ModGuideCard card = ModGuidePages.EventCard(e);
            AssertThat(card.Title).Contains(e.LuaName);
            AssertThat(card.Body).Contains("REFII_MOD_ON(" + e.Name);
            foreach (ModApiField f in e.Fields)
            {
                AssertThat(card.Body).Contains("• " + f.Name + " (");
            }
        }

        // The pages that point at the guide name its folder, never the old single file.
        string all = string.Join("\n", ModGuidePages.All.SelectMany(p => p.Cards.Concat(p.Closing)).Select(c => c.Body));
        AssertThat(all.Contains("mod_api.md", StringComparison.Ordinal)).IsFalse();
        AssertThat(all.Contains(ModGuide.ProjectFolder, StringComparison.Ordinal)).IsTrue();
        AssertThat(ModGuidePages.IndexOf(ModGuidePages.GuideKey)).IsEqual(ModGuidePages.All.Count - 1);
    }

    private static int CountOf(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    /// <summary>Opening tags: <c>[tag]</c> and <c>[tag=…]</c> / <c>[tag …]</c>, never <c>[tagx</c>.</summary>
    private static int CountOpen(string text, string tag) =>
        CountOf(text, "[" + tag + "]") + CountOf(text, "[" + tag + "=") + CountOf(text, "[" + tag + " ");
}
