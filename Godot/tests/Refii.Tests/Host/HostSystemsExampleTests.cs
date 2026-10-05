namespace Refii.Tests;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Launcher;
using Refii.Launcher.Core;
using Refii.Mods;

/// <summary>
/// Reference test suite demonstrating how other developers can write unit tests
/// for general host engine systems (SafePath, path normalization, Godot types, etc.).
/// </summary>
[TestSuite]
public class HostSystemsExampleTests
{
    [TestCase]
    public void TestGodotVectorsAndMath()
    {
        var v1 = new Vector2(100, 200);
        var v2 = new Vector2(50, 25);
        AssertThat(v1 + v2).IsEqual(new Vector2(150, 225));

        var v3 = new Vector3(1, 2, 3);
        AssertThat(v3.Length()).IsEqualApprox(3.741657f, 0.001f);

        var color = new Color(1, 0, 0, 1);
        AssertThat(color.R).IsEqual(1.0f);
        AssertThat(color.G).IsEqual(0.0f);
    }

    [TestCase]
    public void TestModLoadOrderPathNormalization()
    {
        // Tests path normalization used by the mod and VFS loaders:
        string messyPath = "files\\globals/weapons\\toy_gun.gdb.tu";
        string normalized = ModLoadOrder.Normalize(messyPath);

        // All backslashes converted to forward slashes, lowercased:
        AssertThat(normalized).IsEqual("files/globals/weapons/toy_gun.gdb.tu");
    }

    [TestCase]
    public void TestSafePathResolution()
    {
        // Traversal attempt should be rejected by SafePath:
        bool safe = SafePath.IsSafeRelativePath("..\\..\\Windows\\System32", out string error);
        AssertThat(safe).IsFalse();
        AssertThat(error.Length).IsGreater(0);

        // Valid relative subpath should be accepted:
        bool valid = SafePath.IsSafeRelativePath("assets\\mods\\mymod\\files", out error);
        AssertThat(valid).IsTrue();
    }
}
