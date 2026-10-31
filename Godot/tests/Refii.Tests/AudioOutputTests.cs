using GdUnit4;
using Refii.Audio;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Finds the host bridge file Android's linker mapped, which Godot must open as the audio extension.</summary>
[TestSuite]
public sealed class AudioOutputTests
{
    private const string Maps =
        "7a1c000000-7a1c100000 r--p 00000000 fe:2b 1234 /data/app/~~x==/org.refii.androidpresentationprobe-y==/lib/arm64/librefii_hostbridge_test.so\n" +
        "7a2c000000-7a2c100000 r--p 00000000 fe:2b 1235 /data/app/~~x==/org.refii.androidpresentationprobe-y==/lib/arm64/librefii_hostbridge.so\n" +
        "7a2c100000-7a2c900000 r-xp 00100000 fe:2b 1235 /data/app/~~x==/org.refii.androidpresentationprobe-y==/lib/arm64/librefii_hostbridge.so\n" +
        "7a3c000000-7a3c001000 rw-p 00000000 00:00 0 [anon:scudo:primary]\n";

    /// <summary>The mapping's path, matched on the whole file name rather than a prefix of it.</summary>
    [TestCase]
    public void TheMappedBridgeIsFoundByItsWholeFileName()
    {
        AssertThat(Engine_AudioOutput.MappedPath(Maps, "librefii_hostbridge.so"))
            .IsEqual("/data/app/~~x==/org.refii.androidpresentationprobe-y==/lib/arm64/librefii_hostbridge.so");
    }

    /// <summary>No mapping, no maps or anonymous regions only: nothing to hand Godot.</summary>
    [TestCase]
    public void AnAbsentBridgeIsNull()
    {
        AssertThat(Engine_AudioOutput.MappedPath(Maps, "librefii_lua.so")).IsNull();
        AssertThat(Engine_AudioOutput.MappedPath(null, "librefii_hostbridge.so")).IsNull();
        AssertThat(Engine_AudioOutput.MappedPath("7a3c000000-7a3c001000 rw-p 00000000 00:00 0 [stack]\n",
            "librefii_hostbridge.so")).IsNull();
    }
}
