using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// What the phone's main thread leaves undone each frame: refii.toml read only when it moved, the status
/// file written every few seconds off the thread, and a hidden touch layer with nothing held skipping its frame.
/// </summary>
[TestSuite]
public sealed class AndroidFrameCostTests
{
    private static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "refii-frame-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [TestCase]
    public void TheConfigIsReadAgainOnlyWhenItMoves()
    {
        var read = new AndroidConfigStamp(true, 1000, 64);
        AssertThat(AndroidConfigStamp.Reread(read, read, force: false)).IsFalse();
        AssertThat(AndroidConfigStamp.Reread(read, read with { WriteTicksUtc = 1001 }, force: false)).IsTrue();
        AssertThat(AndroidConfigStamp.Reread(read, read with { Length = 65 }, force: false)).IsTrue();
        // A write here says so; a missing file is read, so its error is the read's.
        AssertThat(AndroidConfigStamp.Reread(read, read, force: true)).IsTrue();
        AssertThat(AndroidConfigStamp.Reread(default, default, force: false)).IsTrue();
    }

    [TestCase]
    public void AStampFollowsTheFile()
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "refii.toml");
            AssertThat(AndroidConfigStamp.Of(path).Exists).IsFalse();
            File.WriteAllText(path, "max_fps = 30\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            AndroidConfigStamp first = AndroidConfigStamp.Of(path);
            AssertThat(first.Exists).IsTrue();
            AssertThat(AndroidConfigStamp.Of(path)).IsEqual(first);
            File.WriteAllText(path, "max_fps = 60\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
            AssertThat(AndroidConfigStamp.Reread(first, AndroidConfigStamp.Of(path), force: false)).IsTrue();
            // The same time, another length: still moved.
            File.WriteAllText(path, "max_fps = 120\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
            AndroidConfigStamp longer = AndroidConfigStamp.Of(path);
            AssertThat(longer.WriteTicksUtc).IsEqual(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc).Ticks);
            AssertThat(longer.Length).IsGreater(first.Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase]
    public void TheStatusIsWrittenEveryFiveSecondsAndAChangeSoon()
    {
        AssertThat(AndroidStatusCadence.StatusDue(1.0, false, false)).IsFalse();
        AssertThat(AndroidStatusCadence.StatusDue(AndroidStatusCadence.StatusSeconds - 0.01, false, false)).IsFalse();
        AssertThat(AndroidStatusCadence.StatusDue(AndroidStatusCadence.StatusSeconds, false, false)).IsTrue();
        // A new phase within a second, never more often than the old once a second.
        AssertThat(AndroidStatusCadence.StatusDue(0.2, true, false)).IsFalse();
        AssertThat(AndroidStatusCadence.StatusDue(AndroidStatusCadence.PhaseSeconds, true, false)).IsTrue();
        // A failure at once.
        AssertThat(AndroidStatusCadence.StatusDue(0.0, false, true)).IsTrue();
        AssertThat(AndroidStatusCadence.RefreshSeconds).IsEqual(1.0);
    }

    [TestCase]
    public void TheWriterLeavesTheNewestSnapshotWhole()
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "android-status.json");
            var writer = new AndroidStatusWriter(path);
            for (int i = 0; i < 50; ++i)
            {
                int n = i;
                writer.Post(() => "{ \"n\": " + n + " }");
            }

            AssertThat(writer.WaitIdle(5000)).IsTrue();
            AssertThat(File.ReadAllText(path)).IsEqual("{ \"n\": 49 }");
            AssertThat(File.Exists(path + ".tmp")).IsFalse();
            AssertThat(writer.TakeError()).IsNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase]
    public void AWriteThatFailsIsHandedBack()
    {
        string directory = TempDirectory();
        try
        {
            var writer = new AndroidStatusWriter(Path.Combine(directory, "missing", "android-status.json"));
            writer.Post(() => "{}");
            AssertThat(writer.WaitIdle(5000)).IsTrue();
            AssertThat(writer.TakeError()).IsNotNull();
            // Once: the next take finds none.
            AssertThat(writer.TakeError()).IsNull();
            // A snapshot that throws is the write's error too.
            writer.Post(() => throw new InvalidOperationException("snapshot"));
            AssertThat(writer.WaitIdle(5000)).IsTrue();
            AssertThat(writer.TakeError()).IsInstanceOf<InvalidOperationException>();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase]
    public void SettingsFromReadValuesLeaveThemAsTheyWere()
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["max_fps"] = "30" };
        var settings = AndroidRenderScale.SettingsOf(values, "refii.toml");
        // Android's half-resolution default, supplied without touching the values other readers share.
        AssertThat(settings.RenderScalePercent).IsEqual(50);
        AssertThat(values.ContainsKey("render_scale_percent")).IsFalse();
        values["render_scale_percent"] = "70";
        AssertThat(AndroidRenderScale.SettingsOf(values, "refii.toml").RenderScalePercent).IsEqual(70);
    }

    [TestCase]
    public void AHiddenFrameRestsOnlyWithNothingHeldOrToLayOut()
    {
        static bool Rests(TouchContext context = TouchContext.Hidden, bool rested = true, TouchContext laidOut = TouchContext.Hidden,
            bool layoutSame = true, bool padIdle = true, bool pointerIdle = true, bool hidingWheel = false) =>
            AndroidTouchControls.RestsHidden(context, rested, laidOut, layoutSame, padIdle, pointerIdle, hidingWheel);

        AssertThat(Rests()).IsTrue();
        // The first hidden frame runs whole: it lays out and tells the game nothing is held.
        AssertThat(Rests(rested: false)).IsFalse();
        AssertThat(Rests(laidOut: TouchContext.World)).IsFalse();
        AssertThat(Rests(context: TouchContext.Menu)).IsFalse();
        AssertThat(Rests(context: TouchContext.World, laidOut: TouchContext.World)).IsFalse();
        // A turned phone or a new scale still lays Options out again.
        AssertThat(Rests(layoutSame: false)).IsFalse();
        // A finger, a tap waiting, a pointer gesture or a wheel held hidden.
        AssertThat(Rests(padIdle: false)).IsFalse();
        AssertThat(Rests(pointerIdle: false)).IsFalse();
        AssertThat(Rests(hidingWheel: true)).IsFalse();
    }

    [TestCase]
    public void APadAndPointerAtRestHoldNothing()
    {
        var size = new Vector2(1560f, 720f);
        var pad = new AndroidTouchPad();
        AssertThat(pad.Idle).IsTrue();
        pad.SetLayout(TouchContext.Hidden, AndroidTouchLayout.Build(size, 1f, TouchContext.Hidden), Array.Empty<Rect2>());
        AssertThat(pad.Idle).IsTrue();
        // A finger off Options is the pointer's: down, the pad holds it; up, its events wait to be taken.
        pad.Down(0, new Vector2(800f, 300f), 0.0);
        AssertThat(pad.Idle).IsFalse();
        pad.Up(0, new Vector2(800f, 300f), 0.05);
        AssertThat(pad.Idle).IsFalse();
        var pointer = new AndroidTouchPointer();
        AssertThat(pointer.Idle).IsTrue();
        foreach (TouchPointerEvent touch in pad.TakePointerEvents())
            pointer.Feed(touch, 0.05);
        AssertThat(pad.Idle).IsTrue();
        AssertThat(pad.Sample(0.1)).IsEqual(TouchPadOutput.Neutral);
        // The click still plays out over the next frames.
        AssertThat(pointer.Idle).IsFalse();
        for (int frame = 0; frame < 4; ++frame)
        {
            pointer.Tick(0.06 + frame * 0.016);
            pointer.Take();
        }

        AssertThat(pointer.Idle).IsTrue();
    }

    [TestCase]
    public void AnOfferWithoutPillsOrAreasLaysNoModControl()
    {
        AssertThat(AndroidTouchModControls.HasControls(AndroidTouchModControls.Offer.None)).IsFalse();
        var captions = new AndroidTouchModControls.Offer(Array.Empty<(string, string, bool)>(), Array.Empty<(string, Rect2)>(),
            new Dictionary<string, string> { ["A"] = "Hop" });
        AssertThat(AndroidTouchModControls.HasControls(captions)).IsFalse();
        var pill = new AndroidTouchModControls.Offer(new[] { ("skate.go", "Skate", false) }, Array.Empty<(string, Rect2)>(),
            new Dictionary<string, string>());
        AssertThat(AndroidTouchModControls.HasControls(pill)).IsTrue();
        var area = new AndroidTouchModControls.Offer(Array.Empty<(string, string, bool)>(),
            new[] { ("map.open", new Rect2(0f, 0f, 10f, 10f)) }, new Dictionary<string, string>());
        AssertThat(AndroidTouchModControls.HasControls(area)).IsTrue();
    }
}
