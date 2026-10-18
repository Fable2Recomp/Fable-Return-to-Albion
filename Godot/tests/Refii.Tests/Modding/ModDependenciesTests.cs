namespace Refii.Tests.Modding;

using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

[TestSuite]
public class ModDependenciesTests
{
    private static ModInstall Mod(string name, string version = "", string[]? requires = null, string[]? after = null)
    {
        var metadata = new ModMetadata { Title = name, Version = version };
        foreach (string requirement in requires ?? System.Array.Empty<string>())
        {
            metadata.Requires.Add(ModRequirement.Parse(requirement)!);
        }

        metadata.After.AddRange(after ?? System.Array.Empty<string>());
        return new ModInstall { Name = name, Metadata = metadata };
    }

    [TestCase]
    public void ParsesRequiresAndAfterFromModToml()
    {
        ModMetadata metadata = ModWorkspace.ParseModToml("""
            title = "Spell Weaving"
            version = "0.1.0"
            requires = ["souls_combat >= 0.1", 'hd-ui']
            after = ["other"]
            """);

        AssertThat(metadata.Requires.Count).IsEqual(2);
        AssertThat(metadata.Requires[0].Name).IsEqual("souls_combat");
        AssertThat(metadata.Requires[0].MinVersion).IsEqual("0.1");
        AssertThat(metadata.Requires[1].Name).IsEqual("hd-ui");
        AssertThat(metadata.Requires[1].MinVersion).IsEqual(string.Empty);
        AssertThat(metadata.After.Count).IsEqual(1);
        AssertThat(metadata.After[0]).IsEqual("other");

        string written = ModWorkspace.FormatModToml(metadata);
        AssertThat(written.Contains("requires = [\"souls_combat >= 0.1\", \"hd-ui\"]")).IsTrue();
        AssertThat(written.Contains("after = [\"other\"]")).IsTrue();
        AssertThat(ModWorkspace.FormatModToml(new ModMetadata()).Contains("requires")).IsFalse();
    }

    [TestCase]
    public void ComparesDottedVersions()
    {
        AssertThat(ModRequirement.CompareVersions("0.10", "0.9")).IsGreater(0);
        AssertThat(ModRequirement.CompareVersions("1.0", "1")).IsEqual(0);
        AssertThat(ModRequirement.CompareVersions("0.1.0", "0.2")).IsLess(0);
        AssertThat(ModRequirement.Parse("souls_combat >= 0.2")!.Satisfied("0.1.0")).IsFalse();
        AssertThat(ModRequirement.Parse("souls_combat")!.Satisfied(string.Empty)).IsTrue();
    }

    [TestCase]
    public void ReportsMissingDisabledAndOldRequirements()
    {
        var installed = new List<ModInstall>
        {
            Mod("weaving", "0.1", requires: new[] { "souls_combat >= 0.2", "ghost", "hd-ui" }),
            Mod("souls_combat", "0.1"),
            Mod("hd-ui", "1.0"),
        };
        var order = new ModLoadOrder();
        order.Order.AddRange(new[] { "weaving", "souls_combat", "hd-ui" });
        order.Enabled.AddRange(new[] { "weaving", "souls_combat" });

        List<ModDependencyProblem> problems = ModDependencies.Check(order, installed);

        AssertThat(problems.Count).IsEqual(3);
        AssertThat(problems.All(p => p.Mod == "weaving")).IsTrue();
        AssertThat(problems.Any(p => p.Message.Contains("'ghost'") && p.Message.Contains("not installed"))).IsTrue();
        AssertThat(problems.Any(p => p.Message.Contains("'hd-ui'") && p.Message.Contains("disabled"))).IsTrue();
        AssertThat(problems.Any(p => p.Message.Contains("'souls_combat' 0.2"))).IsTrue();
        AssertThat(ModDependencies.Check(order, installed, new[] { "souls_combat" }).Count).IsEqual(0);
    }

    [TestCase]
    public void SortsRequirementsBeneathTheirDependents()
    {
        // Lower in the list is lower priority: a requirement must come AFTER what needs it.
        var installed = new List<ModInstall>
        {
            Mod("souls_combat"),
            Mod("textures"),
            Mod("weaving", requires: new[] { "souls_combat" }, after: new[] { "textures" }),
            Mod("unrelated"),
        };
        var order = new ModLoadOrder();
        order.Order.AddRange(new[] { "souls_combat", "textures", "weaving", "unrelated" });
        order.Enabled.AddRange(new[] { "souls_combat", "weaving", "unrelated" });

        bool changed = ModDependencies.Sort(order, installed, out string cycle);

        AssertThat(changed).IsTrue();
        AssertThat(cycle).IsEqual(string.Empty);
        AssertThat(order.Order.SequenceEqual(new[] { "weaving", "souls_combat", "textures", "unrelated" })).IsTrue();
        AssertThat(order.Enabled.SequenceEqual(new[] { "weaving", "souls_combat", "unrelated" })).IsTrue();

        AssertThat(ModDependencies.Sort(order, installed, out _)).IsFalse();
    }

    [TestCase]
    public void LeavesACycleAlone()
    {
        var installed = new List<ModInstall>
        {
            Mod("a", requires: new[] { "b" }),
            Mod("b", requires: new[] { "a" }),
        };
        var order = new ModLoadOrder();
        order.Order.AddRange(new[] { "a", "b" });

        bool changed = ModDependencies.Sort(order, installed, out string cycle);

        AssertThat(changed).IsFalse();
        AssertThat(cycle.Contains("a")).IsTrue();
        AssertThat(cycle.Contains("b")).IsTrue();
        AssertThat(order.Order.SequenceEqual(new[] { "a", "b" })).IsTrue();
    }
}
