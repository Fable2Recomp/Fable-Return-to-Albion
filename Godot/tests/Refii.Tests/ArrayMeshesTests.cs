using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Every surface goes through Engine_ArrayMeshes.AddSurface, which keeps its arrays alive.</summary>
[TestSuite]
public sealed class ArrayMeshesTests
{
    private static string GodotRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

    /// <summary>A direct call can have its collections finalized mid-call under NativeAOT.</summary>
    [TestCase]
    public void NoCodeCallsAddSurfaceFromArraysDirectly()
    {
        var call = new Regex(@"\.AddSurfaceFromArrays\(");
        string[] offenders = new[] { "src", "FlashGames" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(GodotRoot(), dir), "*.cs", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path) != "Engine_ArrayMeshes.cs")
            .Where(path => File.ReadLines(path).Any(line => !line.TrimStart().StartsWith("//") && call.IsMatch(line)))
            .ToArray();
        AssertThat(offenders.Length).OverrideFailureMessage("direct AddSurfaceFromArrays in: " +
            string.Join(", ", offenders)).IsEqual(0);
    }
}
