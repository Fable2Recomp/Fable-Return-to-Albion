using System;
using System.Collections.Generic;
using System.Reflection;
using GdUnit4;
using Godot;
using Refii.Fx;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Verifies pending glare publication independently of native Godot GUI bindings.</summary>
[TestSuite]
public sealed class PostFxHaloPublicationTests
{
    /// <summary>Repeated construction publication retains the latest sky state before a tree publisher exists.</summary>
    [TestCase]
    public void PendingSkyPublicationKeepsLatestState()
    {
        string key = "halo-publication-" + Guid.NewGuid();
        var first = new Engine_PostFxHalos.SkyHalo(Vector3.Up, 10, 1, Vector3.One, null, true);
        var latest = first with { Distance = 20, WorldHalf = 2, Active = false };
        Engine_PostFxHalos.PublishSky(key, first);
        Engine_PostFxHalos.PublishSky(key, latest);
        AssertThat(Sky()[key]).IsEqual(latest);
        Sky().Remove(key);
    }

    /// <summary>Cloud visibility published before attachment stays bounded and is independent of sky replacement.</summary>
    [TestCase]
    public void PendingVisibilitySurvivesSkyReplacement()
    {
        string key = "halo-visibility-" + Guid.NewGuid();
        Engine_PostFxHalos.SetSkyVisibility(key, 4);
        AssertThat(Visibility()[key]).IsEqual(1f);
        Engine_PostFxHalos.PublishSky(key, default);
        AssertThat(Visibility()[key]).IsEqual(1f);
        Engine_PostFxHalos.SetSkyVisibility(key, -2);
        AssertThat(Visibility()[key]).IsEqual(0f);
        Sky().Remove(key);
        Visibility().Remove(key);
    }

    /// <summary>Reads the pending registry only; no native scene objects are constructed by CLI tests.</summary>
    private static Dictionary<string, Engine_PostFxHalos.SkyHalo> Sky() =>
        (Dictionary<string, Engine_PostFxHalos.SkyHalo>)typeof(Engine_PostFxHalos)
            .GetField("Sky", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>Reads the pending visibility registry without calling Godot's unmanaged bindings.</summary>
    private static Dictionary<string, float> Visibility() =>
        (Dictionary<string, float>)typeof(Engine_PostFxHalos)
            .GetField("SkyVisibility", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
}
