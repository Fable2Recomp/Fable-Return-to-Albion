using System.Text;
using GdUnit4;
using Refii.Launcher.Recompile;
using static GdUnit4.Assertions;

namespace Refii.Tests.Recompile;

/// <summary>
/// Which build configuration of a library the bridge imports
/// (<see cref="GameCodeBuild.ImportsConfiguration"/>): the game module links that one, whatever
/// else an older install left beside it.
/// </summary>
[TestSuite]
public sealed class LibraryConfigurationTests
{
    private static byte[] Imports(params string[] names) =>
        Encoding.ASCII.GetBytes("\0" + string.Join("\0", names) + "\0");

    [TestCase]
    public void TheReleaseBridgeImportsTheReleaseRuntimeOnly()
    {
        byte[] bridge = Imports("librefii_core.so", "librexruntime.so", "libc.so.6");
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "librexruntime.so")).IsTrue();
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "librexruntimerd.so")).IsFalse();
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "librexruntimed.so")).IsFalse();
    }

    [TestCase]
    public void TheDebugInfoBridgeImportsItsOwnRuntime()
    {
        byte[] bridge = Imports("librexruntimerd.so");
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "librexruntimerd.so")).IsTrue();
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "librexruntime.so")).IsFalse();
    }

    [TestCase]
    public void AnImportNameIsFoundAcrossAReadBoundary()
    {
        string path = System.IO.Path.Combine(System.AppContext.BaseDirectory,
            "refii-imports-" + System.Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var bytes = new byte[(1 << 20) + 64];
            Encoding.ASCII.GetBytes("librexruntimerd.so").CopyTo(bytes, (1 << 20) + 10);
            System.IO.File.WriteAllBytes(path, bytes);
            AssertThat(GameCodeBuild.FileContainsAny(path, new[] { "librexruntimerd.so" })).IsTrue();
            AssertThat(GameCodeBuild.FileContainsAny(path, new[] { "librexruntime.so" })).IsFalse();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [TestCase]
    public void AnImportLibraryStandsForItsDll()
    {
        byte[] bridge = Imports("rexruntime.dll", "KERNEL32.dll");
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "rexruntime.lib")).IsTrue();
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "librexruntime.dll.a")).IsTrue();
        AssertThat(GameCodeBuild.ImportsConfiguration(bridge, "rexruntime", "rexruntimerd.lib")).IsFalse();
    }
}
