namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Game;
using Refii.Launcher.Core;

[TestSuite]
public class RenderingDriverTests
{
    [TestCase]
    public void TestWindowsHdrEnforcesDirect3D12()
    {
        var config = new Dictionary<string, string>
        {
            ["hdr_output"] = "true",
            ["rendering_driver"] = "vulkan",
        };

        string driver = GameChildProcess.ResolveRenderingDriver(config, isWindows: true);
        AssertThat(driver).IsEqual("d3d12");
    }

    [TestCase]
    public void TestWindowsSdrPreservesExplicitVulkan()
    {
        var config = new Dictionary<string, string>
        {
            ["hdr_output"] = "false",
            ["rendering_driver"] = "vulkan",
        };

        string driver = GameChildProcess.ResolveRenderingDriver(config, isWindows: true);
        AssertThat(driver).IsEqual("vulkan");
    }

    [TestCase]
    public void TestWindowsDefaultsToDirect3D12()
    {
        var config = new Dictionary<string, string>();

        string driver = GameChildProcess.ResolveRenderingDriver(config, isWindows: true);
        AssertThat(driver).IsEqual("d3d12");
    }

    [TestCase]
    public void TestNonWindowsAlwaysUsesVulkan()
    {
        var config = new Dictionary<string, string>
        {
            ["hdr_output"] = "true",
            ["rendering_driver"] = "d3d12",
        };

        string driver = GameChildProcess.ResolveRenderingDriver(config, isWindows: false);
        AssertThat(driver).IsEqual("vulkan");
    }

    [TestCase]
    public void TestRuntimeCVarSetRenderingDriverDefaultMatchesPlatform()
    {
        RuntimeCVarDefinition? def = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "rendering_driver");
        AssertThat(def).IsNotNull();
        string expected = OperatingSystem.IsWindows() ? "d3d12" : "vulkan";
        AssertThat(def!.DefaultValue).IsEqual(expected);
    }

    [TestCase]
    public void TestEngineRefiiConfigHdrValues()
    {
        var values = new Dictionary<string, string>
        {
            ["hdr_output"] = "true",
            ["hdr_reference_nits"] = "350",
            ["hdr_peak_nits"] = "1200",
        };

        AssertThat(Refii.Core.Engine_RefiiConfig.Bool(values, "hdr_output", false)).IsTrue();
        AssertThat(Refii.Core.Engine_RefiiConfig.Float(values, "hdr_reference_nits", 0f)).IsEqual(350f);
        AssertThat(Refii.Core.Engine_RefiiConfig.Float(values, "hdr_peak_nits", 0f)).IsEqual(1200f);
    }

    [TestCase]
    public void TestProjectGodotD3D12DescriptorHeapSettings()
    {
        string? dir = AppDomain.CurrentDomain.BaseDirectory;
        string? projectGodotPath = null;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = System.IO.Path.Combine(dir, "project.godot");
            if (System.IO.File.Exists(candidate))
            {
                projectGodotPath = candidate;
                break;
            }
            System.IO.DirectoryInfo? parent = System.IO.Directory.GetParent(dir);
            dir = parent?.FullName;
        }

        if (projectGodotPath != null)
        {
            string content = System.IO.File.ReadAllText(projectGodotPath);
            AssertThat(content.Contains($"rendering_device/d3d12/max_resource_descriptors={GameChildProcess.DefaultD3D12MaxResourceDescriptors}", StringComparison.Ordinal)).IsTrue();
            AssertThat(content.Contains("rendering_device/d3d12/max_sampler_descriptors=2048", StringComparison.Ordinal)).IsTrue();
        }
    }

    [TestCase]
    public void TestSeparateRenderThreadHonorsConfigForBothDrivers()
    {
        var enabledConfig = new Dictionary<string, string>
        {
            ["render_thread"] = "true",
        };
        var disabledConfig = new Dictionary<string, string>
        {
            ["render_thread"] = "false",
        };

        AssertThat(GameChildProcess.ShouldUseSeparateRenderThread(enabledConfig, "d3d12")).IsTrue();
        AssertThat(GameChildProcess.ShouldUseSeparateRenderThread(disabledConfig, "d3d12")).IsFalse();
        AssertThat(GameChildProcess.ShouldUseSeparateRenderThread(enabledConfig, "vulkan")).IsTrue();
        AssertThat(GameChildProcess.ShouldUseSeparateRenderThread(disabledConfig, "vulkan")).IsFalse();
    }

    [TestCase]
    public void TestRotateExistingLogFilePreservesPriorLogWithoutOverwriting()
    {
        string tempDir = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, "log_rotate_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            string logPath = System.IO.Path.Combine(tempDir, "godot.log");
            System.IO.File.WriteAllText(logPath, "first d3d12 run");

            GameChildProcess.RotateExistingLogFile(logPath, maxFiles: 3);

            AssertThat(System.IO.File.Exists(logPath)).IsFalse();
            string[] archives = System.IO.Directory.GetFiles(tempDir, "prev_godot_*.log");
            AssertThat(archives.Length).IsEqual(1);
            AssertThat(System.IO.File.ReadAllText(archives[0])).IsEqual("first d3d12 run");

            System.IO.File.WriteAllText(logPath, "second run");
            GameChildProcess.RotateExistingLogFile(logPath, maxFiles: 2);

            archives = System.IO.Directory.GetFiles(tempDir, "prev_godot_*.log");
            AssertThat(archives.Length).IsEqual(1);
            AssertThat(System.IO.File.ReadAllText(archives[0])).IsEqual("second run");
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
