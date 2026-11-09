using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using System.Collections.Generic;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>refii.toml's android_render_thread mirrored into the project-settings override, without Godot.</summary>
[TestSuite]
public sealed class AndroidRenderThreadTests
{
    /// <summary>Unset is Godot's default; only an explicit true asks for the separate thread.</summary>
    [TestCase]
    public void OffUnlessTheTomlSaysTrue()
    {
        AssertThat(AndroidRenderThread.Configured(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidRenderThread.Configured(new Dictionary<string, string> { ["android_render_thread"] = "false" })).IsFalse();
        AssertThat(AndroidRenderThread.Configured(new Dictionary<string, string> { ["android_render_thread"] = "true" })).IsTrue();
        AssertThat(AndroidRenderThread.Configured(new Dictionary<string, string> { ["android_render_thread"] = "\"true\"" })).IsTrue();
        // The desktop key the launcher has already written into Android configs at its default.
        AssertThat(AndroidRenderThread.Configured(new Dictionary<string, string> { ["render_thread"] = "true" })).IsFalse();
    }

    /// <summary>Every graphics preset asks for the thread; a phone on Custom keeps what it set.</summary>
    [TestCase]
    public void EveryPresetRunsTheRenderThread()
    {
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
            AssertThat(AndroidRenderThread.Configured(GraphicsPresets.Values(preset)!)).IsTrue();
    }

    /// <summary>No file and nothing to state writes nothing; stating it makes the section.</summary>
    [TestCase]
    public void AnEmptyOverrideGainsOnlyTheThreadModel()
    {
        AssertThat(AndroidRenderThread.Rewrite(null, false)).IsNull();
        AssertThat(AndroidRenderThread.Rewrite(null, true))
            .IsEqual("[rendering]\n\ndriver/threads/thread_model=2\n");
    }

    /// <summary>Turning it off removes the file the key alone filled.</summary>
    [TestCase]
    public void OffRemovesAFileThatHeldOnlyTheThreadModel()
    {
        AssertThat(AndroidRenderThread.Rewrite("[rendering]\n\ndriver/threads/thread_model=2\n", false)).IsNull();
    }

    /// <summary>A hand-written thread model is replaced and every other key is kept.</summary>
    [TestCase]
    public void OtherKeysAreKeptAndAHandWrittenModelReplaced()
    {
        const string existing = "[rendering]\r\n\r\ndriver/depth_prepass/enable=false\r\ndriver/threads/thread_model = 1\r\n";
        AssertThat(AndroidRenderThread.Rewrite(existing, true))
            .IsEqual("[rendering]\n\ndriver/depth_prepass/enable=false\ndriver/threads/thread_model=2\n");
        AssertThat(AndroidRenderThread.Rewrite(existing, false))
            .IsEqual("[rendering]\n\ndriver/depth_prepass/enable=false\n");
    }

    /// <summary>The same key in another section is not the thread model.</summary>
    [TestCase]
    public void OnlyTheRenderingSectionsKeyIsTouched()
    {
        const string existing = "[display]\n\ndriver/threads/thread_model=7\n";
        AssertThat(AndroidRenderThread.Rewrite(existing, false)).IsEqual(existing);
        AssertThat(AndroidRenderThread.Rewrite(existing, true))
            .IsEqual("[display]\n\ndriver/threads/thread_model=7\n\n[rendering]\n\ndriver/threads/thread_model=2\n");
    }

    /// <summary>A file already as wanted comes back unchanged, so nothing is rewritten each launch.</summary>
    [TestCase]
    public void AStatedFileIsStable()
    {
        string once = AndroidRenderThread.Rewrite(null, true)!;
        AssertThat(AndroidRenderThread.Rewrite(once, true)).IsEqual(once);
        string kept = AndroidRenderThread.Rewrite("[rendering]\n\ndriver/depth_prepass/enable=false\n", true)!;
        AssertThat(kept).IsEqual("[rendering]\n\ndriver/depth_prepass/enable=false\ndriver/threads/thread_model=2\n");
        AssertThat(AndroidRenderThread.Rewrite(kept, true)).IsEqual(kept);
    }

    /// <summary>A bare section gets the key after its blank line, and repeats collapse into one.</summary>
    [TestCase]
    public void ABareSectionAndRepeats()
    {
        AssertThat(AndroidRenderThread.Rewrite("[rendering]\n", true))
            .IsEqual("[rendering]\n\ndriver/threads/thread_model=2\n");
        AssertThat(AndroidRenderThread.Rewrite("[rendering]\ndriver/threads/thread_model=1\ndriver/threads/thread_model=2\n", true))
            .IsEqual("[rendering]\ndriver/threads/thread_model=2\n");
    }
}
