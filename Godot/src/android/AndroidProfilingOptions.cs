using Refii.Core;
using Refii.Launcher.Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.AndroidRuntime;

/// <summary>A startup snapshot of Android's private diagnostic opt-in and visible engine arguments.</summary>
/// <remarks>The private TOML controls the loopback channel; a desktop environment variable does
/// not enable it. GPU timestamp capture is a separate engine command-line choice, so a TOML
/// gpu_profile value cannot be mistaken for active capture. Android may omit packaged engine
/// switches from its managed argument list; only the published GPU pass table proves capture.
/// No method starts or drives a guest.</remarks>
/// <param name="DebugChannel">The private configuration explicitly enables the existing debug server.</param>
/// <param name="GpuProfileArgumentVisible">The managed engine argument list exposes the capture switch; false does not prove it was absent from the APK.</param>
/// <param name="ConfigPath">The fixed runtime configuration beneath the Android app-private directory.</param>
/// <param name="HandshakePath">The fixed private handshake destination, or empty when the channel is disabled.</param>
internal sealed record AndroidProfilingOptions(
    bool DebugChannel, bool GpuProfileArgumentVisible, string ConfigPath, string HandshakePath)
{
    /// <summary>Reads only the app-private runtime TOML; a missing file leaves the channel disabled.</summary>
    /// <param name="privateDirectory">The absolute Android app-private directory supplied by Godot.</param>
    /// <param name="engineArguments">Godot's engine arguments, rather than user/guest arguments.</param>
    /// <returns>A detached startup decision with a private, non-configurable handshake path.</returns>
    internal static AndroidProfilingOptions Read(string privateDirectory, IReadOnlyList<string> engineArguments)
    {
        string root = PrivateRoot(privateDirectory);
        string config = Path.Combine(root, "runtime", "refii.toml");
        return FromValues(root, RuntimeConfig.ReadValues(config), engineArguments);
    }

    /// <summary>Separates private channel permission from capture-switch visibility in managed arguments.</summary>
    /// <param name="privateDirectory">An absolute app-private directory; paths from the configuration are ignored.</param>
    /// <param name="values">Values parsed from that directory's runtime TOML.</param>
    /// <param name="engineArguments">Engine arguments; parsing stops at a user-argument separator.</param>
    /// <returns>An immutable plan; later configuration changes require a new startup snapshot.</returns>
    internal static AndroidProfilingOptions FromValues(string privateDirectory,
        IReadOnlyDictionary<string, string> values, IReadOnlyList<string> engineArguments)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(engineArguments);
        string root = PrivateRoot(privateDirectory);
        bool channel = values.TryGetValue("debug_channel", out string? value) &&
            bool.TryParse(value.Trim(), out bool enabled) && enabled;
        bool capture = false;
        foreach (string argument in engineArguments)
        {
            if (argument is "--" or "++") break;
            if (argument == "--gpu-profile") capture = true;
        }
        return new(channel, capture, Path.Combine(root, "runtime", "refii.toml"),
            channel ? Path.Combine(root, "runtime", "debug", "refii-debug.json") : string.Empty);
    }

    /// <summary>Installs Android's frame samplers before an optional listener, which never owns a second pair.</summary>
    /// <remarks>Clock/draw sampling belongs to the host regardless of socket permission or port
    /// availability. A listener failure cannot skip host sampling or require a fallback pair.</remarks>
    /// <param name="attachDebugServer">Attaches the existing server with its private handshake path and explicit false sampler ownership.</param>
    /// <param name="attachHostSamplers">Attaches Android's single frame-start/end pair before any listener startup.</param>
    internal void InstallFrameSampling(Action<string, bool> attachDebugServer, Action attachHostSamplers)
    {
        ArgumentNullException.ThrowIfNull(attachDebugServer);
        ArgumentNullException.ThrowIfNull(attachHostSamplers);
        attachHostSamplers();
        if (DebugChannel) attachDebugServer(HandshakePath, false);
    }

    /// <summary>Rejects relative roots before deriving private configuration and handshake destinations.</summary>
    /// <param name="directory">The trusted Android app-private root, independent of configuration values.</param>
    /// <returns>The normalized absolute root.</returns>
    /// <exception cref="ArgumentException">The root is empty or relative.</exception>
    private static string PrivateRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Android diagnostics require an absolute app-private directory", nameof(directory));
        return Path.GetFullPath(directory);
    }
}

/// <summary>A detached copy of already published viewport measurements, including geometry work.</summary>
/// <param name="Sampled">The render thread has published at least one measurement.</param>
/// <param name="CpuMs">The viewport's measured rendering CPU time, excluding other viewports and guest work.</param>
/// <param name="GpuMs">The viewport's measured rendering GPU time.</param>
/// <param name="VisibleCalls">Draw calls in the viewport's visible passes.</param>
/// <param name="ShadowCalls">Draw calls in its shadow passes.</param>
/// <param name="VisiblePrims">Primitives in the visible passes; useful when resolution changes leave GPU cost high.</param>
/// <param name="ShadowPrims">Primitives in the shadow passes.</param>
/// <param name="VisibleObjects">Visible-pass objects reported by Godot.</param>
/// <param name="ShadowObjects">Shadow-pass objects reported by Godot.</param>
internal readonly record struct AndroidRenderCounters(bool Sampled, double CpuMs, double GpuMs,
    int VisibleCalls, int ShadowCalls, int VisiblePrims, int ShadowPrims, int VisibleObjects, int ShadowObjects)
{
    /// <summary>Copies existing samples without renderer queries, native calls or readbacks on the main thread.</summary>
    /// <param name="sample">The value previously published by Engine_RenderStats on the render thread.</param>
    /// <returns>A stable serializable snapshot preserving the preceding report's field names.</returns>
    internal static AndroidRenderCounters Capture(Engine_RenderStats.Sample sample) =>
        new(sample.Sampled, sample.CpuMs, sample.GpuMs, sample.VisibleCalls, sample.ShadowCalls,
            sample.VisiblePrims, sample.ShadowPrims, sample.VisibleObjects, sample.ShadowObjects);
}
