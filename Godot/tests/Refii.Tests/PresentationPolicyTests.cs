using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Tests diagnostic goals separately from uncapped startup and restart pacing.</summary>
[TestSuite]
public sealed class PresentationPolicyTests
{
    /// <summary>Rejects malformed live flags without selecting a startup mode or mutating compositor state.</summary>
    [TestCase]
    public void LivePostFxValidation()
    {
        using var empty = JsonDocument.Parse("{}");
        AssertThat(Engine_PresentationPolicy.ReadPostFxRequest(empty.RootElement) is null).IsTrue();
        foreach (string value in new[] { "1", "null", "\"false\"" })
        {
            using var invalid = JsonDocument.Parse("{\"postFxEnabled\":" + value + "}");
            bool rejected = false;
            try { Engine_PresentationPolicy.ReadPostFxRequest(invalid.RootElement); }
            catch (ArgumentException) { rejected = true; }
            AssertThat(rejected).IsTrue();
        }
    }

    /// <summary>Exercises full-chain, live bypass, read-only inspection and restoration while keeping startup config intact.</summary>
    [TestCase]
    public void LivePostFxLifecycle()
    {
        bool enabled = true;
        var config = new Dictionary<string, string>();
        foreach (string text in new[] { "{\"postFxEnabled\":false}", "{}", "{\"postFxEnabled\":true}" })
        {
            using var request = JsonDocument.Parse(text);
            bool? selection = Engine_PresentationPolicy.ReadPostFxRequest(request.RootElement);
            if (selection is { } value) enabled = value;
            AssertThat(enabled).IsEqual(text.Contains("true"));
            AssertThat(Engine_PresentationPolicy.PostFxMode(config)).IsEqual("on");
            AssertThat(config.Count).IsEqual(0);
        }
    }

    /// <summary>Validates absent, enabled, disabled and malformed startup modes without native renderer calls.</summary>
    [TestCase]
    public void PostFxSelection()
    {
        var values = new Dictionary<string, string>();
        AssertThat(Engine_PresentationPolicy.PostFxMode(values)).IsEqual("on");
        foreach (string value in new[] { "true", "false", "invalid" })
        {
            values["android_postfx_enabled"] = value;
            AssertThat(Engine_PresentationPolicy.PostFxMode(values)).IsEqual(value == "false" ? "off" : "on");
        }
    }

    /// <summary>Exercises original config, diagnostic override, restart selection and restoration without writing user settings.</summary>
    [TestCase]
    public void PostFxConfigLifecycle()
    {
        string path = Path.Combine(Refii.Tests.TestTemp.Root, "refii_postfx_" + Guid.NewGuid().ToString("N") + ".toml");
        const string original = "sun_shadow_map = 1024\n";
        try
        {
            foreach (string text in new[] { original, original + "android_postfx_enabled = false\n", original })
            {
                File.WriteAllText(path, text);
                var values = Refii.Launcher.Core.RuntimeConfig.ReadValues(path);
                AssertThat(Engine_PresentationPolicy.PostFxMode(values)).IsEqual(text == original ? "on" : "off");
                AssertThat(File.ReadAllText(path)).IsEqual(text);
                AssertThat(Engine_PresentationPolicy.TargetFps(true)).IsEqual(30);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>Checks both platform budgets and labels without implying an FPS cap or resolution.</summary>
    [TestCase]
    public void DiagnosticBudgets()
    {
        AssertThat(Engine_PresentationPolicy.TargetFps(true)).IsEqual(30);
        AssertThat(Engine_PresentationPolicy.TargetFps(false)).IsEqual(120);
        AssertThat(Math.Abs(Engine_PresentationPolicy.BudgetMs(true) - 1000d / 30) < .00001).IsTrue();
        AssertThat(Engine_PresentationPolicy.BudgetLabel(true)).IsEqual("target 30 fps / 33.33 ms");
        AssertThat(Engine_PresentationPolicy.BudgetLabel(false)).IsEqual("target 120 fps / 8.33 ms");
    }

    /// <summary>Exercises startup, a changed debug cap, restart, and invalid callbacks without native bindings.</summary>
    [TestCase]
    public void StartupRestartLifecycle()
    {
        int cap = 30, writes = 0;
        var vsync = DisplayServer.VSyncMode.Enabled;
        void Max(int value) { cap = value; writes++; }
        void Vsync(DisplayServer.VSyncMode value) { vsync = value; writes++; }
        Engine_PresentationPolicy.ApplyAndroid(0, Max, Vsync);
        AssertThat(cap).IsEqual(0);
        AssertThat(vsync).IsEqual(DisplayServer.VSyncMode.Disabled);
        cap = 60; vsync = DisplayServer.VSyncMode.Mailbox;
        Engine_PresentationPolicy.ApplyAndroid(30, Max, Vsync);
        AssertThat(cap).IsEqual(30);
        AssertThat(vsync).IsEqual(DisplayServer.VSyncMode.Disabled);
        Engine_PresentationPolicy.ApplyAndroid(-5, Max, Vsync);
        AssertThat(cap).IsEqual(0);
        AssertThat(writes).IsEqual(6);
        bool rejected = false;
        try { Engine_PresentationPolicy.ApplyAndroid(0, Max, null!); }
        catch (ArgumentNullException) { rejected = true; }
        AssertThat(rejected).IsTrue();
        AssertThat(writes).IsEqual(6);
    }
}
