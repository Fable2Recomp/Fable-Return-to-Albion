using Godot;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Game;
using System;
using System.Collections.Generic;
using System.IO;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks shared configuration semantics and world-scale reload lifecycles without native Godot controls.</summary>
[TestSuite]
public sealed class AndroidRenderScaleTests
{
    /// <summary>No-file defaults remain distinct from a present but empty runtime configuration.</summary>
    [TestCase]
    public void SharedParserPreservesNoFileAndEmptyFileDefaults()
    {
        var empty = new Dictionary<string, string>();
        var absent = GameWindowSettings.FromValues(empty, string.Empty);
        var present = GameWindowSettings.FromValues(empty, "refii.toml");
        foreach (var settings in new[] { absent, present })
        {
            AssertThat(settings.Size).IsEqual(new Vector2I(1280, 720));
            AssertThat(settings.RenderScalePercent).IsEqual(100);
            AssertThat(settings.RenderScale).IsEqual(1f);
            AssertThat(settings.Vsync).IsTrue();
            AssertThat(settings.Scaler).IsEqual("bilinear");
            AssertThat(settings.Anisotropic).IsEqual(4);
        }
        AssertThat(absent.Source).IsEqual("defaults (no refii.toml found)");
        AssertThat(absent.VsyncMode).IsNull();
        AssertThat(absent.MeshLodPixels).IsEqual(0f);
        AssertThat(absent.SunShadowMap).IsEqual(0);
        AssertThat(present.Source).IsEqual("defaults (unset in refii.toml)");
        AssertThat(present.VsyncMode).IsEqual("mailbox");
        AssertThat(present.MeshLodPixels).IsEqual(1f);
        AssertThat(present.SunShadowMap).IsEqual(4096);
    }

    /// <summary>The pure parser retains display resolution precedence, legacy fallbacks and minimum sizes.</summary>
    [TestCase]
    public void SharedResolutionPrecedenceAndClampsStayIntact()
    {
        var values = new Dictionary<string, string>
        {
            ["display_resolution"] = "1920x1080", ["window_width"] = "1024", ["window_height"] = "576",
            ["video_mode_width"] = "800", ["video_mode_height"] = "600"
        };
        AssertThat(GameWindowSettings.FromValues(values, "source.toml").Size).IsEqual(new Vector2I(1920, 1080));
        values["display_resolution"] = "bad";
        AssertThat(GameWindowSettings.FromValues(values, "source.toml").Size).IsEqual(new Vector2I(1024, 576));
        values["window_width"] = "-1";
        AssertThat(GameWindowSettings.FromValues(values, "source.toml").Size).IsEqual(new Vector2I(800, 600));
        values["display_resolution"] = "1x2";
        AssertThat(GameWindowSettings.FromValues(values, "source.toml").Size).IsEqual(new Vector2I(320, 240));
        AssertThat(GameWindowSettings.FromValues(values, "source.toml").Source).IsEqual("source.toml");
    }

    /// <summary>Moving the parser does not change desktop toggles, quality settings, normalization or numeric guards.</summary>
    [TestCase]
    public void SharedDesktopSettingsKeepTheirNormalization()
    {
        var values = new Dictionary<string, string>
        {
            ["fullscreen"] = "true", ["borderless"] = "1", ["monitor"] = "-1", ["vsync"] = "false",
            ["vsync_mode"] = " ON ", ["max_fps"] = "-4", ["draw_latency_ticks"] = "2.5",
            ["mesh_lod_pixels"] = "80", ["sun_shadow_map"] = "12", ["render_scale_percent"] = "67",
            ["msaa"] = "4", ["render_scaler"] = " FSR2 ", ["fsr_quality"] = " PERFORMANCE ",
            ["fsr_sharpness"] = "4", ["texture_mipmap_bias"] = "-3", ["screen_space_aa"] = " FXAA ",
            ["taa"] = "true", ["anisotropic_filtering"] = "16", ["debanding"] = "true",
            ["hdr_output"] = "true", ["hdr_reference_nits"] = "-2", ["hdr_peak_nits"] = "1000"
        };
        var settings = GameWindowSettings.FromValues(values, "source.toml");
        AssertThat(settings.Fullscreen && settings.Borderless && settings.Taa && settings.Debanding && settings.Hdr).IsTrue();
        AssertThat(settings.Monitor).IsEqual(0);
        AssertThat(settings.Vsync).IsFalse();
        AssertThat(settings.VsyncMode).IsEqual("on");
        AssertThat(settings.MaxFps).IsEqual(0);
        AssertThat(settings.DrawLatencyTicks).IsEqual(2.5f);
        AssertThat(settings.MeshLodPixels).IsEqual(64f);
        AssertThat(settings.SunShadowMap).IsEqual(512);
        AssertThat(settings.Msaa).IsEqual(4);
        AssertThat(settings.RenderScalePercent).IsEqual(67);
        AssertThat(settings.Scaler).IsEqual("fsr2");
        AssertThat(settings.FsrQuality).IsEqual("performance");
        AssertThat(settings.RenderScale).IsEqual(0.5f);
        AssertThat(settings.FsrSharpness).IsEqual(2f);
        AssertThat(settings.MipmapBias).IsEqual(-2f);
        AssertThat(settings.ScreenSpaceAa).IsEqual("fxaa");
        AssertThat(settings.Anisotropic).IsEqual(16);
        AssertThat(settings.HdrReferenceNits).IsEqual(0f);
        AssertThat(settings.HdrPeakNits).IsEqual(1000f);
    }

    /// <summary>Invalid integers fall back to native scale and out-of-range values retain the existing safe bounds.</summary>
    [TestCase]
    public void RenderPercentageValidationUsesSharedPolicy()
    {
        foreach ((string raw, int expected) in new[]
        {
            ("67", 67), ("\"50\"", 50), ("0", 25), ("-20", 25), ("201", 200),
            ("25", 25), ("200", 200), ("1.5", 100), ("NaN", 100), ("invalid", 100)
        })
        {
            var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
                { ["render_scale_percent"] = raw }, "runtime.toml");
            AssertThat(settings.RenderScalePercent).IsEqual(expected);
        }
    }

    /// <summary>Mobile uses the explicit percentage even when a desktop FSR quality preset is present.</summary>
    [TestCase]
    public void AndroidPercentageDoesNotAdoptDesktopFsrPreset()
    {
        var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
        {
            ["render_scale_percent"] = "67", ["render_scaler"] = "fsr2", ["fsr_quality"] = "performance"
        }, "runtime.toml");
        var policy = new AndroidRenderScale();
        float applied = 0;
        AssertThat(policy.Apply(settings, value => applied = value)).IsTrue();
        AssertThat(applied).IsEqual(0.67f);
        AssertThat(settings.RenderScale).IsEqual(0.5f);
        AssertThat(policy.Apply(settings, _ => throw new InvalidOperationException("duplicate setter"))).IsFalse();
    }

    /// <summary>A failed viewport assignment is retried rather than recorded as applied.</summary>
    [TestCase]
    public void FailedApplicationPreservesPreviousScaleAndCanRecover()
    {
        var policy = new AndroidRenderScale();
        var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
            { ["render_scale_percent"] = "50" }, "runtime.toml");
        bool failed = false;
        try { policy.Apply(settings, _ => throw new InvalidOperationException("synthetic renderer failure")); }
        catch (InvalidOperationException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(policy.Applied).IsEqual(1f);
        AssertThat(policy.Apply(settings, _ => { })).IsTrue();
        AssertThat(policy.Applied).IsEqual(0.5f);
    }

    /// <summary>Logical canvas size alone is insufficient under canvas-items stretching; only world scale changes the estimate.</summary>
    [TestCase]
    public void ExpectedDimensionsDistinguishLogicalAndPhysicalTargets()
    {
        Vector2 logical = new(1560, 738);
        Vector2 stretch = new(2280f / 1560, 1080f / 738);
        var native = AndroidRenderScale.ExpectedSize(logical, stretch, 1f);
        var half = AndroidRenderScale.ExpectedSize(logical, stretch, 0.5f);
        var oldViewportMode = AndroidRenderScale.ExpectedSize(logical, Vector2.One, 1f);
        AssertThat(Math.Abs(native.X - 2280f) < 0.01f && Math.Abs(native.Y - 1080f) < 0.01f).IsTrue();
        AssertThat(Math.Abs(half.X - 1140f) < 0.01f && Math.Abs(half.Y - 540f) < 0.01f).IsTrue();
        AssertThat(oldViewportMode).IsEqual(logical);
        AssertThat(logical).IsEqual(new Vector2(1560, 738));
    }

    /// <summary>Android supplies half scale only for an absent percentage, preserving explicit parsing and the private file.</summary>
    [TestCase]
    public void AndroidFileDefaultPreservesExplicitPercentagesAndDesktopPolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-android-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "refii.toml");
            foreach ((string contents, int expected) in new[]
            {
                ("", 50),
                ("sun_shadow_map = 1024\nsun_shadow_splits = 2\nshader_warmup_enabled = false\n", 50),
                ("render_scaler = \"fsr2\"\nfsr_quality = \"quality\"\n", 50),
                ("render_scale_percent = 100\n", 100),
                ("render_scale_percent = \"67\" # explicit\n", 67),
                ("render_scale_percent = 0\n", 25),
                ("render_scale_percent = 201\n", 200),
                ("render_scale_percent = invalid\n", 100)
            })
            {
                File.WriteAllText(path, contents);
                var settings = AndroidRenderScale.ReadSettings(path);
                AssertThat(settings.RenderScalePercent).IsEqual(expected);
                AssertThat(File.ReadAllText(path)).IsEqual(contents);
                AssertThat(settings.Size).IsEqual(new Vector2I(1280, 720));
            }
            AssertThat(GameWindowSettings.FromValues(new Dictionary<string, string>(), path).RenderScalePercent).IsEqual(100);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Original config, explicit override, key removal and unavailable-file recovery retain only successfully applied world scales.</summary>
    [TestCase]
    public void AndroidDefaultOverrideAndRestoreLifecycle()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-android-scale-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "refii.toml");
            string original = "sun_shadow_map = 1024\nsun_shadow_splits = 2\nshader_warmup_enabled = false\n";
            var policy = new AndroidRenderScale();
            var applied = new List<float>();
            foreach (string contents in new[] { original, original, original + "render_scale_percent = 100\n", original, original })
            {
                File.WriteAllText(path, contents);
                policy.Apply(AndroidRenderScale.ReadSettings(path), applied.Add);
                AssertThat(File.ReadAllText(path)).IsEqual(contents);
            }
            AssertThat(applied.ToArray()).IsEqual(new[] { 0.5f, 1f, 0.5f });
            File.Delete(path);
            bool unavailable = false;
            try { policy.Apply(AndroidRenderScale.ReadSettings(path), applied.Add); }
            catch (FileNotFoundException) { unavailable = true; }
            AssertThat(unavailable).IsTrue();
            AssertThat(policy.Applied).IsEqual(0.5f);
            File.WriteAllText(path, original);
            AssertThat(policy.Apply(AndroidRenderScale.ReadSettings(path), applied.Add)).IsFalse();
            AssertThat(applied.Count).IsEqual(3);
            AssertThat(File.ReadAllText(path)).IsEqual(original);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A synthetic private TOML can change and restore world scale repeatedly without redundant renderer assignments.</summary>
    [TestCase]
    public void RuntimeConfigReloadLifecycleAppliesOnlyEffectiveChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-render-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "refii.toml");
            var policy = new AndroidRenderScale();
            var applied = new List<float>();
            foreach (int percentage in new[] { 100, 67, 67, 50, 100, 100 })
            {
                string contents = "# Synthetic configuration; no game or save inputs\n" +
                    "display_resolution = \"640x360\"\nrender_scaler = \"fsr2\"\n" +
                    "fsr_quality = \"performance\"\nrender_scale_percent = \"" + percentage + "\" # reload\n";
                File.WriteAllText(path, contents);
                var settings = AndroidRenderScale.ReadSettings(path);
                policy.Apply(settings, applied.Add);
                AssertThat(File.ReadAllText(path)).IsEqual(contents);
                AssertThat(settings.Size).IsEqual(new Vector2I(640, 360));
            }
            AssertThat(applied.ToArray()).IsEqual(new[] { 1f, 0.67f, 0.5f, 1f });
            AssertThat(policy.Applied).IsEqual(1f);
            File.Delete(path);
            bool unavailable = false;
            try { policy.Apply(AndroidRenderScale.ReadSettings(path), applied.Add); }
            catch (FileNotFoundException) { unavailable = true; }
            AssertThat(unavailable).IsTrue();
            AssertThat(applied.Count).IsEqual(4);
            AssertThat(policy.Applied).IsEqual(1f);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
