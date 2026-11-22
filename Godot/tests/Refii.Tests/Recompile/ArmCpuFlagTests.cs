using GdUnit4;
using Refii.Launcher.Recompile;
using static GdUnit4.Assertions;

namespace Refii.Tests.Recompile;

/// <summary>
/// The CPU the launcher states for game code on ARM64 Linux
/// (<see cref="GameCodeBuild.ArmCpuFlag"/>): one answer for the whole machine, whichever core asks.
/// </summary>
[TestSuite]
public sealed class ArmCpuFlagTests
{
    private const string Big =
        "processor\t: 7\nFeatures\t: fp asimd aes pmull sha1 sha2 crc32 atomics fphp asimdrdm asimddp sve2\n"
        + "CPU part\t: 0xd82\n";

    private const string Little =
        "processor\t: 0\nFeatures\t: fp asimd aes pmull sha1 sha2 crc32 atomics fphp asimdrdm asimddp\n"
        + "CPU part\t: 0xd81\n";

    [TestCase]
    public void MixedCoresGetOneGenericCpuWithTheirCommonFeatures()
    {
        AssertThat(GameCodeBuild.ArmCpuFlag(Little + "\n" + Big))
            .IsEqual("-mcpu=generic+lse+crc+rdm+dotprod+aes+sha2");
    }

    [TestCase]
    public void AFeatureOneCoreLacksIsLeftOut()
    {
        string older = "processor\t: 1\nFeatures\t: fp asimd aes sha2 crc32\n";
        AssertThat(GameCodeBuild.ArmCpuFlag(Big + "\n" + older)).IsEqual("-mcpu=generic+crc+aes+sha2");
    }

    [TestCase]
    public void NoFeatureListStillStatesTheGenericCpu()
    {
        AssertThat(GameCodeBuild.ArmCpuFlag(string.Empty)).IsEqual("-mcpu=generic");
        AssertThat(GameCodeBuild.ArmCpuFlag("Features\t: fp asimd\n")).IsEqual("-mcpu=generic");
    }
}
