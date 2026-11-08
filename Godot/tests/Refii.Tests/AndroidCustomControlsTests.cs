namespace Refii.Tests;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;
using Refii.Game;

[TestSuite]
public class AndroidCustomControlsTests
{
    [AfterTest]
    public void BackToDefaults()
    {
        AndroidPadRemap.Reset();
        AndroidPadRemap.Suppress = false;
        AndroidTouchCustomLayout.Load(Path.Combine(Refii.Tests.TestTemp.Root, "refii-none-" + Guid.NewGuid().ToString("N") + ".json"));
    }

    private static string Temp(string name) => Path.Combine(Refii.Tests.TestTemp.Root, "refii-" + name + "-" + Guid.NewGuid().ToString("N") + ".json");

    [TestCase]
    public void TestARemapSwapsSoEveryButtonStaysReachable()
    {
        AssertThat(AndroidPadRemap.IsDefault).IsTrue();
        // Physical B now presses the game's A, and physical A takes A's old place: the game's B.
        AndroidPadRemap.Assign("A", "B");
        AssertThat(AndroidPadRemap.SourceOf("A")).IsEqual("B");
        AssertThat(AndroidPadRemap.SourceOf("B")).IsEqual("A");
        uint buttons = GameInput.ButtonB, lt = 0, rt = 0;
        AndroidPadRemap.Apply(ref buttons, ref lt, ref rt);
        AssertThat(buttons).IsEqual(GameInput.ButtonA);
        buttons = GameInput.ButtonA | GameInput.ButtonX;
        AndroidPadRemap.Apply(ref buttons, ref lt, ref rt);
        AssertThat(buttons).IsEqual(GameInput.ButtonB | GameInput.ButtonX);
    }

    [TestCase]
    public void TestTriggersAndButtonsTradePlaces()
    {
        // The game's LT from physical LB: a press is a full pull. LB's old place, the game's LB, takes the trigger,
        // pressing once the trigger is past the XInput threshold; a trigger to trigger keeps its travel.
        AndroidPadRemap.Assign("LT", "LB");
        uint buttons = GameInput.LeftShoulder, lt = 0, rt = 120;
        AndroidPadRemap.Apply(ref buttons, ref lt, ref rt);
        AssertThat(lt).IsEqual(255u);
        AssertThat(rt).IsEqual(120u);
        AssertThat(buttons).IsEqual(0u);
        buttons = 0; lt = 200; rt = 0;
        AndroidPadRemap.Apply(ref buttons, ref lt, ref rt);
        AssertThat(buttons).IsEqual(GameInput.LeftShoulder);
        AssertThat(lt).IsEqual(0u);
    }

    [TestCase]
    public void TestTheOpenMenuHoldsThePadBack()
    {
        AndroidPadRemap.Suppress = true;
        uint buttons = GameInput.ButtonA, lt = 255, rt = 255;
        AndroidPadRemap.Apply(ref buttons, ref lt, ref rt);
        AssertThat(buttons).IsEqual(0u);
        AssertThat(lt + rt).IsEqual(0u);
    }

    [TestCase]
    public void TestTheMapSavesOnlyItsChanges()
    {
        string path = Temp("map");
        try
        {
            AndroidPadRemap.Assign("X", "Y");
            AndroidPadRemap.Save(path);
            AssertThat(File.ReadAllText(path)).Contains("\"X\":\"Y\"");
            AndroidPadRemap.Reset();
            AndroidPadRemap.Load(path);
            AssertThat(AndroidPadRemap.SourceOf("X")).IsEqual("Y");
            AssertThat(AndroidPadRemap.SourceOf("Y")).IsEqual("X");
            // Back to the default, the file goes.
            AndroidPadRemap.Reset();
            AndroidPadRemap.Save(path);
            AssertThat(File.Exists(path)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    private static TouchControl Button(string id, Rect2 bounds) =>
        new(id, TouchControlKind.Button, bounds, true, 0u, TouchTrigger.None, id, "", Colors.White);

    [TestCase]
    public void TestAMovedAndResizedControlKeepsItsCentreOnScreen()
    {
        string path = Temp("layouts");
        try
        {
            AndroidTouchCustomLayout.Load(path);
            var size = new Vector2(1000, 500);
            var controls = new[] { Button("A", new Rect2(800, 300, 100, 100)), Button("B", new Rect2(100, 100, 100, 100)) };
            const TouchContext world = TouchContext.World;
            AndroidTouchCustomLayout.Move(world, "A", new Vector2(-100, -50), size);
            AndroidTouchCustomLayout.Resize(world, "A", 5);
            TouchControl a = AndroidTouchCustomLayout.Apply(controls, size, world).First(c => c.Id == "A");
            AssertThat(a.Bounds.GetCenter()).IsEqual(new Vector2(750, 300));
            AssertThat(a.Bounds.Size).IsEqual(new Vector2(150, 150));
            // B was never touched.
            AssertThat(AndroidTouchCustomLayout.Apply(controls, size, world).First(c => c.Id == "B").Bounds).IsEqual(controls[1].Bounds);
            // Dragged off the edge, it stops with its centre half its size in.
            AndroidTouchCustomLayout.Move(world, "A", new Vector2(2000, 0), size);
            AssertThat(AndroidTouchCustomLayout.Apply(controls, size, world).First(c => c.Id == "A").Bounds.End.X).IsEqual(1000f);
            // The size stops at its range.
            AndroidTouchCustomLayout.Resize(world, "A", 100);
            AssertThat(AndroidTouchCustomLayout.Of(world, "A").Size).IsEqual(AndroidTouchCustomLayout.MaxSize);
        }
        finally { File.Delete(path); }
    }

    [TestCase]
    public void TestEachKindOfScreenKeepsItsOwnLayout()
    {
        string path = Temp("layouts");
        try
        {
            AndroidTouchCustomLayout.Load(path);
            var size = new Vector2(1000, 500);
            var controls = new[] { Button("A", new Rect2(800, 300, 100, 100)) };
            AndroidTouchCustomLayout.Move(TouchContext.World, "A", new Vector2(-100, 0), size);
            AndroidTouchCustomLayout.Resize(TouchContext.Menu, "A", -3);
            // A moved in the world stays where it was built in a menu, and the menu's smaller A stays out of the world.
            AssertThat(AndroidTouchCustomLayout.Apply(controls, size, TouchContext.World).First().Bounds)
                .IsEqual(new Rect2(700, 300, 100, 100));
            AssertThat(AndroidTouchCustomLayout.Apply(controls, size, TouchContext.Menu).First().Bounds)
                .IsEqual(new Rect2(815, 315, 70, 70));
            // Resetting the menus leaves the world's layout.
            AndroidTouchCustomLayout.ResetScreen(TouchContext.Menu);
            AssertThat(AndroidTouchCustomLayout.Current(TouchContext.Menu).Count).IsEqual(0);
            AssertThat(AndroidTouchCustomLayout.Current(TouchContext.World).Count).IsEqual(1);
        }
        finally { File.Delete(path); }
    }

    [TestCase]
    public void TestPresetsKeepEveryScreenAndDefaultClearsThem()
    {
        string path = Temp("layouts");
        try
        {
            AndroidTouchCustomLayout.Load(path);
            var size = new Vector2(1000, 500);
            AndroidTouchCustomLayout.Move(TouchContext.World, "A", new Vector2(100, 0), size);
            AndroidTouchCustomLayout.Move(TouchContext.Menu, "B", new Vector2(0, 50), size);
            AndroidTouchCustomLayout.SavePreset("Thumbs");
            AndroidTouchCustomLayout.LoadPreset(AndroidTouchCustomLayout.DefaultName);
            AssertThat(AndroidTouchCustomLayout.Current(TouchContext.World).Count + AndroidTouchCustomLayout.Current(TouchContext.Menu).Count).IsEqual(0);
            // Every change is saved, so a fresh load has the preset, with both screens.
            AndroidTouchCustomLayout.Load(path);
            AssertThat(AndroidTouchCustomLayout.Presets.ToArray()).IsEqual(new[] { "Thumbs" });
            AndroidTouchCustomLayout.LoadPreset("Thumbs");
            AssertThat(AndroidTouchCustomLayout.Of(TouchContext.World, "A").Dx).IsEqual(0.1f);
            AssertThat(AndroidTouchCustomLayout.Of(TouchContext.Menu, "B").Dy).IsEqual(0.1f);
            // Default is the built layouts, never a saved name; a deleted preset is gone.
            AndroidTouchCustomLayout.SavePreset("default");
            AssertThat(AndroidTouchCustomLayout.Presets.Count()).IsEqual(1);
            AndroidTouchCustomLayout.DeletePreset("Thumbs");
            AssertThat(AndroidTouchCustomLayout.Presets.Count()).IsEqual(0);
        }
        finally { File.Delete(path); }
    }

    [TestCase]
    public void TestAFirstFilesOneLayoutIsTheWorlds()
    {
        string path = Temp("layouts");
        try
        {
            File.WriteAllText(path, "{\"Current\":{\"A\":{\"Dx\":0.1,\"Dy\":0,\"Size\":1.2}},"
                + "\"Presets\":{\"Old\":{\"B\":{\"Dx\":0,\"Dy\":0.2,\"Size\":1}}}}");
            AndroidTouchCustomLayout.Load(path);
            AssertThat(AndroidTouchCustomLayout.Of(TouchContext.World, "A").Size).IsEqual(1.2f);
            AssertThat(AndroidTouchCustomLayout.Current(TouchContext.Menu).Count).IsEqual(0);
            AndroidTouchCustomLayout.LoadPreset("Old");
            AssertThat(AndroidTouchCustomLayout.Of(TouchContext.World, "B").Dy).IsEqual(0.2f);
        }
        finally { File.Delete(path); }
    }

    [TestCase]
    public void TestTheStickTheCameraAndTheItemRingStayPut()
    {
        AssertThat(AndroidTouchCustomLayout.Editable(Button("A", new Rect2(0, 0, 10, 10)))).IsTrue();
        AssertThat(AndroidTouchCustomLayout.Editable(new TouchControl("Stick", TouchControlKind.Stick, new Rect2(0, 0, 10, 10),
            false, 0u, TouchTrigger.None, "", "", Colors.White))).IsFalse();
        AssertThat(AndroidTouchCustomLayout.Editable(new TouchControl("DPad", TouchControlKind.DPad, new Rect2(0, 0, 10, 10),
            false, 0u, TouchTrigger.None, "", "", Colors.White, OverHud: true))).IsFalse();
    }
}
