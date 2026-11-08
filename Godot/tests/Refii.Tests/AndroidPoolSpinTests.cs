namespace Refii.Tests;

using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;

[TestSuite]
public class AndroidPoolSpinTests
{
    [TestCase]
    public void TestAutoSpinsLessOnlyInTheLowMemoryTier()
    {
        AssertThat(AndroidPoolSpin.Resolve("auto", lowMemoryTier: true)).IsEqual(AndroidPoolSpin.LowTierLimit);
        AssertThat(AndroidPoolSpin.Resolve(null, lowMemoryTier: true)).IsEqual(AndroidPoolSpin.LowTierLimit);
        AssertThat(AndroidPoolSpin.Resolve(" Auto ", lowMemoryTier: true)).IsEqual(AndroidPoolSpin.LowTierLimit);
        AssertThat(AndroidPoolSpin.Resolve("auto", lowMemoryTier: false).HasValue).IsFalse();
        AssertThat(AndroidPoolSpin.Resolve("", lowMemoryTier: false).HasValue).IsFalse();
    }

    [TestCase]
    public void TestANumberIsTheLimitOnAnyTierAndAnythingElseLeavesTheRuntimes()
    {
        AssertThat(AndroidPoolSpin.Resolve("0", lowMemoryTier: false)).IsEqual(0);
        AssertThat(AndroidPoolSpin.Resolve("280", lowMemoryTier: true)).IsEqual(280);
        AssertThat(AndroidPoolSpin.Resolve("default", lowMemoryTier: true).HasValue).IsFalse();
        AssertThat(AndroidPoolSpin.Resolve("-5", lowMemoryTier: true).HasValue).IsFalse();
        AssertThat(AndroidPoolSpin.Resolve("0x20", lowMemoryTier: true).HasValue).IsFalse();
    }

    [TestCase]
    public void TestTheConfigIsTheOneTheGcPreinitReads()
    {
        AssertThat(AndroidPoolSpin.ConfigPathFor("org.refii.android\0"))
            .IsEqual("/data/data/org.refii.android/files/runtime/refii.toml");
        AssertThat(AndroidPoolSpin.ConfigPathFor("org.refii.android:service\0"))
            .IsEqual("/data/data/org.refii.android/files/runtime/refii.toml");
        AssertThat(AndroidPoolSpin.ConfigPathFor("")).IsNull();
        AssertThat(AndroidPoolSpin.ConfigPathFor("/system/bin/app_process64\0")).IsNull();
    }

    [TestCase]
    public void TestTheKnobIsTheOneDotNet9Reads()
    {
        // PortableThreadPool.WorkerThread reads exactly this AppContext name (release/9.0).
        AssertThat(AndroidPoolSpin.RuntimeKnob).IsEqual("System.Threading.ThreadPool.UnfairSemaphoreSpinLimit");
    }

    [TestCase]
    public void TestTheSettingIsListedWithAutoAsItsDefault()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsGodot.Definitions.FirstOrDefault(d => d.Name == AndroidPoolSpin.Key);
        AssertThat(row is null).IsFalse();
        AssertThat(row!.DefaultValue).IsEqual("auto");
    }
}
