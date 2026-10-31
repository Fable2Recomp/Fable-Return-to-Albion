using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks private profiling admission, frame-sampler ownership and detached report lifecycles without Godot nodes.</summary>
[TestSuite]
public sealed class AndroidProfilingOptionsTests
{
    /// <summary>Missing, false and malformed values cannot enable a channel through unrelated settings.</summary>
    [TestCase]
    public void ChannelRequiresAnExplicitParsedTrueValue()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "refii-profile-policy"));
        foreach (string? value in new string?[] { null, "", "false", "0", "1", "yes", "true trailing" })
        {
            var values = new Dictionary<string, string> { ["gpu_profile"] = "true", ["REFII_DEBUG_PORT"] = "8787" };
            if (value != null) values["debug_channel"] = value;
            var options = AndroidProfilingOptions.FromValues(root, values, Array.Empty<string>());
            AssertThat(options.DebugChannel).IsFalse();
            AssertThat(options.GpuProfileArgumentVisible).IsFalse();
            AssertThat(options.HandshakePath).IsEqual(string.Empty);
        }
        foreach (string value in new[] { "true", " TRUE ", "True" })
            AssertThat(AndroidProfilingOptions.FromValues(root, new Dictionary<string, string>
                { ["debug_channel"] = value }, Array.Empty<string>()).DebugChannel).IsTrue();
    }

    /// <summary>Only a visible exact engine switch is reported; omission does not establish unavailable capture.</summary>
    [TestCase]
    public void ArgumentVisibilityAndPrivateChannelAreIndependent()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        var values = new Dictionary<string, string> { ["debug_channel"] = "true", ["gpu_profile"] = "true" };
        foreach (string[] args in new[] { Array.Empty<string>(), new[] { "--gpu-profile=true" },
            new[] { "--", "--gpu-profile" }, new[] { "++", "--gpu-profile" } })
        {
            var options = AndroidProfilingOptions.FromValues(root, values, args);
            AssertThat(options.DebugChannel).IsTrue();
            AssertThat(options.GpuProfileArgumentVisible).IsFalse();
        }
        values["debug_channel"] = "false";
        var captured = AndroidProfilingOptions.FromValues(root, values,
            new[] { "--rendering-method", "mobile", "--gpu-profile", "--", "private-guest-argument" });
        AssertThat(captured.DebugChannel).IsFalse();
        AssertThat(captured.GpuProfileArgumentVisible).IsTrue();
    }

    /// <summary>Configuration paths cannot redirect the handshake outside the trusted private root.</summary>
    [TestCase]
    public void HandshakeUsesOnlyTheAbsolutePrivateDirectory()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "refii profile paths", "nested", ".."));
        var options = AndroidProfilingOptions.FromValues(root, new Dictionary<string, string>
        {
            ["debug_channel"] = "true", ["debug_handshake"] = "../../external.json",
            ["REFII_DEBUG_HANDSHAKE"] = "/sdcard/public.json", ["debug_port"] = "1"
        }, Array.Empty<string>());
        AssertThat(options.ConfigPath).IsEqual(Path.Combine(root, "runtime", "refii.toml"));
        AssertThat(options.HandshakePath).IsEqual(Path.Combine(root, "runtime", "debug", "refii-debug.json"));
        foreach (string invalid in new[] { string.Empty, "runtime", "../private" })
        {
            bool rejected = false;
            try { AndroidProfilingOptions.FromValues(invalid, new Dictionary<string, string>(), Array.Empty<string>()); }
            catch (ArgumentException) { rejected = true; }
            AssertThat(rejected).IsTrue();
        }
    }

    /// <summary>The same startup decision used by Android installs exactly one frame-sampler owner.</summary>
    [TestCase]
    public void HostOwnsSamplingRegardlessOfOptionalListener()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        var events = new List<string>();
        foreach (bool enabled in new[] { false, true, false })
        {
            var options = AndroidProfilingOptions.FromValues(root, new Dictionary<string, string>
                { ["debug_channel"] = enabled.ToString() }, Array.Empty<string>());
            options.InstallFrameSampling((path, ownsSampling) => events.Add("debug:" + ownsSampling + ":" + path), () => events.Add("ordinary"));
        }
        AssertThat(events.ToArray()).IsEqual(new[] { "ordinary", "ordinary",
            "debug:False:" + Path.Combine(root, "runtime", "debug", "refii-debug.json"), "ordinary" });
    }

    /// <summary>A refused listener leaves the already installed host sampler pair intact and never owns another.</summary>
    [TestCase]
    public void ListenerRefusalPreservesHostSampling()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        var options = AndroidProfilingOptions.FromValues(root,
            new Dictionary<string, string> { ["debug_channel"] = "true" }, Array.Empty<string>());
        var events = new List<string>();
        int samplerPairs = 0;
        bool listening = false;
        options.InstallFrameSampling((path, ownsSampling) =>
        {
            AssertThat(samplerPairs).IsEqual(1);
            AssertThat(ownsSampling).IsFalse();
            AssertThat(path).IsEqual(options.HandshakePath);
            events.Add("listener-refused");
            // Models permission/port refusal: listener startup returns without a socket.
            listening = false;
        }, () => { samplerPairs++; events.Add("host-samplers"); });
        AssertThat(events.ToArray()).IsEqual(new[] { "host-samplers", "listener-refused" });
        AssertThat(samplerPairs).IsEqual(1);
        AssertThat(listening).IsFalse();
    }

    /// <summary>Even an unexpected listener exception occurs after host sampling has acquired its sole ownership.</summary>
    [TestCase]
    public void ListenerExceptionCannotSkipHostSampling()
    {
        var options = AndroidProfilingOptions.FromValues(Path.GetFullPath(Path.GetTempPath()),
            new Dictionary<string, string> { ["debug_channel"] = "true" }, Array.Empty<string>());
        int samplerPairs = 0;
        bool failed = false;
        try
        {
            options.InstallFrameSampling((_, ownsSampling) =>
            {
                AssertThat(ownsSampling).IsFalse();
                throw new IOException("Synthetic listener startup failure");
            }, () => samplerPairs++);
        }
        catch (IOException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(samplerPairs).IsEqual(1);
    }

    /// <summary>Real synthetic private TOML changes affect the next startup while prior snapshots and files remain intact.</summary>
    [TestCase]
    public void PrivateConfigStartupLifecycleIsStableAndReadOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-profile-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var absent = AndroidProfilingOptions.Read(root, Array.Empty<string>());
            AssertThat(absent.DebugChannel).IsFalse();
            string config = Path.Combine(root, "runtime", "refii.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            string firstText = "debug_channel = true # private opt-in\ngpu_profile = true\n" +
                "debug_handshake = \"../../public.json\"\nrender_scale_percent = 67\n";
            File.WriteAllText(config, firstText);
            var first = AndroidProfilingOptions.Read(root, new[] { "--gpu-profile" });
            string firstJson = JsonSerializer.Serialize(first);
            var owners = new List<string>();
            first.InstallFrameSampling((path, ownsSampling) => owners.Add(path + ":" + ownsSampling), () => owners.Add("ordinary"));
            AssertThat(first.DebugChannel && first.GpuProfileArgumentVisible).IsTrue();
            AssertThat(File.ReadAllText(config)).IsEqual(firstText);
            AssertThat(Directory.Exists(Path.Combine(root, "runtime", "debug"))).IsFalse();
            File.WriteAllText(config, "debug_channel = false\n");
            var next = AndroidProfilingOptions.Read(root, Array.Empty<string>());
            next.InstallFrameSampling((path, ownsSampling) => owners.Add(path + ":" + ownsSampling), () => owners.Add("ordinary"));
            AssertThat(next.DebugChannel || next.GpuProfileArgumentVisible).IsFalse();
            AssertThat(JsonSerializer.Serialize(first)).IsEqual(firstJson);
            AssertThat(owners.ToArray()).IsEqual(new[] { "ordinary", first.HandshakePath + ":False", "ordinary" });
            File.Delete(config);
            AssertThat(AndroidProfilingOptions.Read(root, Array.Empty<string>()).DebugChannel).IsFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Extended counters retain the existing JSON fields and remain detached from later source samples.</summary>
    [TestCase]
    public void PrimitiveReportCopiesPublishedSamplesWithoutNativeQueries()
    {
        var source = new Engine_RenderStats.Sample { Sampled = true, CpuMs = 12.7, GpuMs = 164.03,
            VisibleCalls = 802, ShadowCalls = 88, VisiblePrims = 3_000_000, ShadowPrims = 900_000,
            VisibleObjects = 412, ShadowObjects = 67 };
        var snapshot = AndroidRenderCounters.Capture(source);
        string json = JsonSerializer.Serialize(snapshot);
        source.VisiblePrims = 0;
        source.GpuMs = 0;
        AssertThat(snapshot.VisiblePrims).IsEqual(3_000_000);
        AssertThat(snapshot.ShadowPrims).IsEqual(900_000);
        AssertThat(JsonSerializer.Serialize(snapshot)).IsEqual(json);
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (string field in new[] { "Sampled", "CpuMs", "GpuMs", "VisibleCalls", "ShadowCalls",
            "VisiblePrims", "ShadowPrims", "VisibleObjects", "ShadowObjects" })
            AssertThat(document.RootElement.TryGetProperty(field, out _)).IsTrue();
        var absent = AndroidRenderCounters.Capture(default);
        AssertThat(absent.Sampled).IsFalse();
        AssertThat(absent.VisiblePrims + absent.ShadowPrims).IsEqual(0);
    }
}
