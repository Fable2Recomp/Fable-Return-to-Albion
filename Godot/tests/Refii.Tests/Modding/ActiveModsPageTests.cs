namespace Refii.Tests.Modding;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The Active Mods book's page: a mod row's page is the author's own description from the
/// package's mod.toml, credited with its version and author. Pure .NET — <c>Detail</c> is read
/// directly, so the source's frame-clock cache is not reached.
/// </summary>
[TestSuite]
public class ActiveModsPageTests
{
    [TestCase]
    public void AModRowsPageIsItsDescriptionAndCredit()
    {
        string mods = Path.Combine(Refii.Tests.TestTemp.Root, "refii_active_mods_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mods);
        string before = Engine_UiNetworkSource.ModsRoot;
        try
        {
            F2ModPackage.Create(Path.Combine(mods, "weave.f2mod"),
                new ModMetadata { Title = "Weave", Version = "0.2.0", Author = "Miki", Description = "Two spells at once." },
                Array.Empty<(string, byte[])>(), out _);
            Engine_UiNetworkSource.ModsRoot = mods;

            AssertThat(Engine_UiNetworkSource.ModInfo("weave")?.Title).IsEqual("Weave");
            AssertThat(Engine_UiNetworkSource.Detail(Engine_UiNetworkSource.ModRowPrefix + "weave"))
                .IsEqual("Two spells at once.\n\nVersion 0.2.0 by Miki.");
            AssertThat(Engine_UiNetworkSource.Detail(Engine_UiNetworkSource.ModRowPrefix + "gone"))
                .Contains("package is missing");
        }
        finally
        {
            Engine_UiNetworkSource.ModsRoot = before;
            Directory.Delete(mods, recursive: true);
        }
    }
}
