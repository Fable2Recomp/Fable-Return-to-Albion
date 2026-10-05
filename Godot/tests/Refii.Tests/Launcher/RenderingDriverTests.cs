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
            AssertThat(content.Contains("rendering_device/d3d12/max_resource_descriptors=1000000", StringComparison.Ordinal)).IsTrue();
            AssertThat(content.Contains("rendering_device/d3d12/max_sampler_descriptors=2048", StringComparison.Ordinal)).IsTrue();
        }
    }
}
