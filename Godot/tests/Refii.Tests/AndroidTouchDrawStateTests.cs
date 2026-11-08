using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>When the touch controls draw again: every change to what they show does it, and nothing else does.</summary>
[TestSuite]
public sealed class AndroidTouchDrawStateTests
{
    private static readonly Vector2 Phone = new(1560f, 720f);

    private static readonly TouchContext[] EveryContext =
    {
        TouchContext.World, TouchContext.Wheel, TouchContext.Menu, TouchContext.PubGame, TouchContext.Photo,
        TouchContext.Decorate, TouchContext.Mirror, TouchContext.Hidden,
    };

    private static AndroidTouchPad Pad(TouchContext context)
    {
        var pad = new AndroidTouchPad();
        pad.SetLayout(context, Layout(context), System.Array.Empty<Rect2>());
        return pad;
    }

    private static IReadOnlyList<TouchControl> Layout(TouchContext context) =>
        AndroidTouchLayout.Build(Phone, 1f, context, pubGame: "keystone", placing: true);

    /// <summary>The overlay as a frame ends: the pad sampled, then read, with every other input at its usual value.</summary>
    private static TouchDrawnOverlay Frame(AndroidTouchPad pad, double now, bool picker = false)
    {
        pad.Sample(now);
        return AndroidTouchDrawState.Overlay(pad, Phone, 1f, 0.5f, 1.5f, null, -1, null, picker, now);
    }

    private static bool Redraws(in TouchDrawnOverlay now, in TouchDrawnOverlay drawn) =>
        AndroidTouchDrawState.NeedsRedraw(now, drawn, artMissing: false, sinceDrawn: 0.0);

    private static TouchStickLook Stick(AndroidTouchPad pad, bool picker = false) =>
        AndroidTouchDrawState.Stick(pad, Phone, 1f, 0.5f, picker);

    private static Vector2 Centre(AndroidTouchPad pad, string id) => pad.Controls.First(c => c.Id == id).Bounds.GetCenter();

    [TestCase]
    public void AStillFrameRedrawsNothing()
    {
        foreach (TouchContext context in EveryContext)
        {
            AndroidTouchPad pad = Pad(context);
            TouchDrawnOverlay drawn = Frame(pad, 1.0);
            for (double now = 1.0; now < 5.0; now += 0.05)
            {
                AssertThat(Redraws(Frame(pad, now), drawn)).IsFalse();
            }
        }
    }

    [TestCase]
    public void EveryDrawnControlRedrawsWhenPressedAndWhenLifted()
    {
        foreach (TouchContext context in EveryContext)
        {
            AndroidTouchPad pad = Pad(context);
            TouchDrawnOverlay rest = Frame(pad, 0.0);
            foreach (TouchControl control in pad.Controls.Where(c => c.Kind is not (TouchControlKind.Stick or TouchControlKind.Look)))
            {
                // A long press off centre, so a D-pad is on an arm: a Latch takes no latch from it.
                Vector2 at = control.Bounds.GetCenter() + (control.Kind == TouchControlKind.DPad ? new Vector2(control.Bounds.Size.X * 0.3f, 0f) : Vector2.Zero);
                pad.Down(0, at, 0.0);
                AssertThat(Redraws(Frame(pad, 0.0), rest)).OverrideFailureMessage($"{context} {control.Id} pressed").IsTrue();
                pad.Up(0, at, 1.0);
                AssertThat(Redraws(Frame(pad, 1.0), rest)).OverrideFailureMessage($"{context} {control.Id} lifted").IsFalse();
                pad.TakeActions();
            }
        }
    }

    [TestCase]
    public void ALatchRedrawsHoweverItGoes()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        TouchDrawnOverlay rest = Frame(pad, 0.0);
        Vector2 back = Centre(pad, "Back");
        pad.Down(0, back, 0.0);
        TouchDrawnOverlay held = Frame(pad, 0.0);
        pad.Up(0, back, 0.1);
        TouchDrawnOverlay latched = Frame(pad, 0.1);
        AssertThat(Redraws(latched, held)).OverrideFailureMessage("the heavier rim, once latched").IsTrue();
        AssertThat(Redraws(latched, rest)).IsTrue();

        // Let go by the guest's wheel closing, by a tap, and by losing the focus.
        pad.Unlatch("Back");
        AssertThat(Redraws(Frame(pad, 0.2), rest)).IsFalse();
        pad.Down(0, back, 0.3);
        pad.Up(0, back, 0.4);
        AssertThat(Redraws(Frame(pad, 0.4), rest)).IsTrue();
        pad.Down(0, back, 0.5);
        pad.Up(0, back, 0.6);
        AssertThat(Redraws(Frame(pad, 0.6), rest)).IsFalse();
        pad.Down(0, back, 0.7);
        pad.Up(0, back, 0.8);
        AssertThat(Redraws(Frame(pad, 0.8), rest)).IsTrue();
        pad.Cancel();
        AssertThat(Redraws(Frame(pad, 0.9), rest)).IsFalse();
    }

    [TestCase]
    public void TheSpellsHoldRingAnimatesEveryFrameUntilFull()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        TouchDrawnOverlay rest = Frame(pad, 0.0);
        Vector2 spells = Centre(pad, "RT");
        pad.Down(0, spells, 0.0);
        TouchDrawnOverlay drawn = Frame(pad, 0.0);
        for (double now = 0.02; now < AndroidTouchPad.LatchTap; now += 0.02)
        {
            TouchDrawnOverlay frame = Frame(pad, now);
            AssertThat(Redraws(frame, drawn)).OverrideFailureMessage($"filling at {now}").IsTrue();
            drawn = frame;
        }

        drawn = Frame(pad, AndroidTouchPad.LatchTap);
        AssertThat(drawn.Hold).IsEqual(1f);
        for (double now = AndroidTouchPad.LatchTap; now < 3.0; now += 0.1)
        {
            AssertThat(Redraws(Frame(pad, now), drawn)).OverrideFailureMessage($"full at {now}").IsFalse();
        }

        pad.Up(0, spells, 3.0);
        AssertThat(Redraws(Frame(pad, 3.0), rest)).IsFalse();
        AssertThat(Redraws(Frame(pad, 3.0), drawn)).IsTrue();
    }

    [TestCase]
    public void TheHeldDPadArmRedrawsAsItChanges()
    {
        foreach (TouchContext context in new[] { TouchContext.Menu, TouchContext.World })
        {
            AndroidTouchPad pad = Pad(context);
            TouchControl dpad = pad.Controls.First(c => c.Kind == TouchControlKind.DPad);
            TouchDrawnOverlay rest = Frame(pad, 0.0);
            Vector2 centre = dpad.Bounds.GetCenter(), reach = dpad.Bounds.Size * 0.4f;
            pad.Down(0, centre + new Vector2(0f, -reach.Y), 0.0);
            TouchDrawnOverlay up = Frame(pad, 0.0);
            AssertThat(Redraws(up, rest)).IsTrue();
            AssertThat(Redraws(Frame(pad, 0.1), up)).IsFalse();
            pad.Move(0, centre + new Vector2(reach.X, 0f), Vector2.Zero);
            AssertThat(Redraws(Frame(pad, 0.2), up)).IsTrue();
            pad.Up(0, centre + new Vector2(reach.X, 0f), 1.0);
            AssertThat(Redraws(Frame(pad, 1.0), rest)).IsFalse();
        }
    }

    [TestCase]
    public void EveryOtherInputRedraws()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        TouchDrawnOverlay drawn = Frame(pad, 0.0);
        (string Name, TouchDrawnOverlay Now)[] changes =
        {
            ("touch_opacity", drawn with { Opacity = 0.6f }),
            ("touch_scale", drawn with { Scale = 1.1f }),
            ("the screen", drawn with { Size = new Vector2(1600f, 720f) }),
            ("the window's density", drawn with { Density = 2f }),
            ("the HUD's slots", drawn with { Slots = (new Vector2(100f, 500f), 30f) }),
            ("the spell chosen", drawn with { Spell = 3 }),
            ("the button art loading", drawn with { Atlas = new object() }),
            ("the picker opening", drawn with { Picker = true }),
            ("the context", drawn with { Context = TouchContext.Wheel }),
        };
        foreach ((string name, TouchDrawnOverlay now) in changes)
        {
            AssertThat(Redraws(now, drawn)).OverrideFailureMessage(name).IsTrue();
        }

        // A controller in hand hides the controls, and the next touch brings them back: each a new layout.
        pad.SetLayout(TouchContext.World, Layout(TouchContext.World), System.Array.Empty<Rect2>());
        AssertThat(Redraws(Frame(pad, 0.0), drawn)).OverrideFailureMessage("a new layout, even the same again").IsTrue();
        TouchDrawnOverlay relaid = Frame(pad, 0.0);
        pad.SetLayout(TouchContext.Hidden, Layout(TouchContext.Hidden), System.Array.Empty<Rect2>());
        AssertThat(Redraws(Frame(pad, 0.0), relaid)).IsTrue();
    }

    [TestCase]
    public void MissingArtIsRetriedAndThePictureIsKeptOnlySoLong()
    {
        TouchDrawnOverlay drawn = Frame(Pad(TouchContext.World), 0.0);
        AssertThat(AndroidTouchDrawState.NeedsRedraw(drawn, drawn, artMissing: true, sinceDrawn: 0.0)).IsTrue();
        AssertThat(AndroidTouchDrawState.NeedsRedraw(drawn, drawn, artMissing: false, sinceDrawn: AndroidTouchDrawState.LongestKept - 0.01)).IsFalse();
        AssertThat(AndroidTouchDrawState.NeedsRedraw(drawn, drawn, artMissing: false, sinceDrawn: AndroidTouchDrawState.LongestKept)).IsTrue();
    }

    [TestCase]
    public void AMovingThumbRedrawsNothingButTheSprintRing()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        TouchDrawnOverlay rest = Frame(pad, 0.0);
        AssertThat(Stick(pad).Origin is null).IsTrue();

        var at = new Vector2(300f, 400f);
        pad.Down(0, at, 0.0);
        TouchStickLook landed = Stick(pad);
        AssertThat(landed.Origin is not null).IsTrue();
        AssertThat(Redraws(Frame(pad, 0.0), rest)).OverrideFailureMessage("landing").IsFalse();

        pad.Move(0, at + new Vector2(30f, 10f), Vector2.Zero);
        AssertThat(Stick(pad)).IsEqual(landed);
        AssertThat(Redraws(Frame(pad, 0.1), rest)).OverrideFailureMessage("moving").IsFalse();

        pad.Move(0, at + new Vector2(pad.StickRadius * AndroidTouchPad.SprintReach + 5f, 0f), Vector2.Zero);
        TouchStickLook sprinting = Stick(pad);
        AssertThat(sprinting.Sprinting).IsTrue();
        AssertThat(sprinting == landed).IsFalse();
        AssertThat(sprinting.KnobOnly).IsEqual(landed.KnobOnly);
        AssertThat(Redraws(Frame(pad, 0.2), rest)).OverrideFailureMessage("sprinting").IsFalse();

        pad.Up(0, at, 0.3);
        AssertThat(Stick(pad).Origin is null).IsTrue();
        AssertThat(Stick(pad).KnobOnly).IsEqual(landed.KnobOnly);
        AssertThat(Redraws(Frame(pad, 0.3), rest)).OverrideFailureMessage("lifting").IsFalse();
    }

    [TestCase]
    public void DraggingTheCameraRedrawsNothing()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        TouchDrawnOverlay rest = Frame(pad, 0.0);
        var at = new Vector2(1000f, 300f);
        pad.Down(0, at, 0.0);
        AssertThat(Redraws(Frame(pad, 0.0), rest)).IsFalse();
        pad.Move(0, at + new Vector2(40f, 0f), new Vector2(40f, 0f));
        AssertThat(Redraws(Frame(pad, 0.1), rest)).IsFalse();
        pad.Up(0, at, 0.2);
        AssertThat(Redraws(Frame(pad, 0.2), rest)).IsFalse();
    }

    /// <summary>The picker's backdrop darkens a held stick, so the main item draws it under the picker, and follows it.</summary>
    [TestCase]
    public void UnderTheOpenPickerTheStickMovesWithTheMainItem()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        var at = new Vector2(300f, 400f);
        pad.Down(0, at, 0.0);
        TouchStickLook own = Stick(pad);

        TouchDrawnOverlay drawn = Frame(pad, 0.0, picker: true);
        AssertThat(Stick(pad, picker: true)).IsEqual(default(TouchStickLook));
        AssertThat(drawn.InlineStick).IsEqual(own);
        pad.Move(0, at + new Vector2(30f, 10f), Vector2.Zero);
        TouchDrawnOverlay moved = Frame(pad, 0.1, picker: true);
        AssertThat(Redraws(moved, drawn)).IsTrue();
        AssertThat(moved.InlineKnob).IsEqual(pad.StickKnob);

        // Closed, it goes back to its own items.
        TouchDrawnOverlay closed = Frame(pad, 0.2);
        AssertThat(closed.InlineStick).IsEqual(default(TouchStickLook));
        AssertThat(Redraws(closed, moved)).IsTrue();
        AssertThat(Stick(pad)).IsEqual(own);
    }

    /// <summary>A menu opening under a held stick leaves the pad its origin until the thumb lifts: nothing draws it.</summary>
    [TestCase]
    public void ALayoutWithNoStickDrawsNoStick()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, new Vector2(300f, 400f), 0.0);
        pad.SetLayout(TouchContext.Menu, Layout(TouchContext.Menu), System.Array.Empty<Rect2>());
        AssertThat(pad.StickOrigin is not null).IsTrue();
        AssertThat(Stick(pad).Origin is null).IsTrue();
        AssertThat(Frame(pad, 0.0, picker: true).InlineStick.Origin is null).IsTrue();
    }

    /// <summary>
    /// The stick's items stand above the main one, which is the order drawn only while nothing drawn
    /// comes after the stick in a layout. The picker, laid first, is drawn last and handled apart.
    /// </summary>
    [TestCase]
    public void NoLayoutDrawsAnythingAfterTheStick()
    {
        foreach (TouchContext context in EveryContext)
        foreach (bool shown in new[] { true, false })
        {
            List<TouchControl> controls = AndroidTouchLayout.Build(Phone, 1f, context, photoPanel: shown, pubGame: "keystone", placing: shown).ToList();
            int stick = controls.FindIndex(c => c.Kind == TouchControlKind.Stick);
            if (stick >= 0)
            {
                AssertThat(controls.Count(c => c.Kind == TouchControlKind.Stick)).IsEqual(1);
                AssertThat(controls.Skip(stick + 1).All(c => c.Kind == TouchControlKind.Look)).IsTrue();
            }
        }
    }

    /// <summary>The pressed controls are bits in a ulong: the largest layout, with all eight spells offered, must fit.</summary>
    [TestCase]
    public void EveryLayoutFitsThePressedBits()
    {
        (int, string)[] spells = Enumerable.Range(0, 8).Select(will => (will, "Spell")).ToArray();
        int picker = AndroidTouchLayout.BuildSpellPicker(Phone, 1f, spells).Count;
        foreach (TouchContext context in EveryContext)
        {
            AssertThat(Layout(context).Count + picker).IsLess(64);
        }
    }

    /// <summary>The comparison runs every frame, so it must leave nothing for the collector, with fingers down or not.</summary>
    [TestCase]
    public void AFrameComparisonAllocatesNothing()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, new Vector2(300f, 400f), 0.0);
        pad.Down(1, Centre(pad, "RT"), 0.0);
        TouchDrawnOverlay drawn = default;
        bool any = false;
        void Run(int frames, double from)
        {
            for (int i = 0; i < frames; ++i)
            {
                double now = from + i * 0.0001;
                TouchDrawnOverlay overlay = AndroidTouchDrawState.Overlay(pad, Phone, 1f, 0.5f, 1.5f, (new Vector2(100f, 500f), 30f), 2, null, false, now);
                any |= AndroidTouchDrawState.NeedsRedraw(overlay, drawn, false, 0.0);
                any |= AndroidTouchDrawState.Stick(pad, Phone, 1f, 0.5f, false) == default;
                drawn = overlay;
            }
        }

        Run(20000, 0.0);
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        Run(100000, 0.0);
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        AssertThat(any).IsTrue();
        AssertThat(allocated).IsEqual(0L);
    }

    /// <summary>Under the engine: what really draws, counted, as a thumb lands, moves, sprints and lifts.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public async Task TheStickItemsDrawOnlyWhenTheirLookChanges()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var stick = new AndroidTouchStick();
        tree.Root.AddChild(stick);
        var knob = (Node2D)stick.GetChild(0);
        int bases = 0, knobs = 0;
        stick.Draw += () => bases++;
        knob.Draw += () => knobs++;
        async Task Frames() { for (int i = 0; i < 3; ++i) await AwaitSignalOn(tree, "process_frame"); }
        try
        {
            await Frames();
            bases = knobs = 0;
            var look = new TouchStickLook(new Vector2(200.25f, 300.5f), 128f, false, 1f, 0.5f);
            stick.Follow(look, Vector2.Zero);
            await Frames();
            AssertThat(knob.Visible).IsTrue();
            AssertThat(bases).OverrideFailureMessage("base, landing").IsEqual(1);
            AssertThat(knobs).OverrideFailureMessage("knob, landing").IsEqual(1);

            for (int i = 0; i < 20; ++i)
            {
                var offset = new Vector2(i * 1.5f, -i * 0.75f);
                stick.Follow(look, offset);
                AssertThat(knob.Position).IsEqual(look.Origin!.Value + offset);
                await AwaitSignalOn(tree, "process_frame");
            }

            await Frames();
            AssertThat(bases).OverrideFailureMessage("base, moving").IsEqual(1);
            AssertThat(knobs).OverrideFailureMessage("knob, moving").IsEqual(1);

            stick.Follow(look with { Sprinting = true }, Vector2.Zero);
            await Frames();
            AssertThat(bases).OverrideFailureMessage("base, sprinting").IsEqual(2);
            AssertThat(knobs).OverrideFailureMessage("knob, sprinting").IsEqual(1);

            stick.Follow(look with { Alpha = 0.7f }, Vector2.Zero);
            await Frames();
            AssertThat(bases).OverrideFailureMessage("base, opacity").IsEqual(3);
            AssertThat(knobs).OverrideFailureMessage("knob, opacity").IsEqual(2);

            stick.Follow(look with { Alpha = 0.7f, Origin = null }, Vector2.Zero);
            await Frames();
            AssertThat(knob.Visible).IsFalse();
            AssertThat(bases).OverrideFailureMessage("base, lifting").IsEqual(4);
            AssertThat(knobs).OverrideFailureMessage("knob, lifting").IsEqual(2);

            // A thumb landing again: the knob is shown, which draws it, with the look it last had.
            stick.Follow(look with { Alpha = 0.7f }, Vector2.Zero);
            await Frames();
            AssertThat(knob.Visible).IsTrue();
            AssertThat(bases).OverrideFailureMessage("base, again").IsEqual(5);

            int knobsShown = knobs;
            stick.Visible = false;
            stick.Follow(look with { Alpha = 0.2f }, Vector2.Zero);
            await Frames();
            AssertThat(knobs).OverrideFailureMessage("hidden draws nothing").IsEqual(knobsShown);
            stick.Visible = true;
            stick.Follow(look with { Alpha = 0.2f }, Vector2.Zero);
            await Frames();
            AssertThat(knobs).OverrideFailureMessage("shown, drawn again").IsEqual(knobsShown + 1);
        }
        finally
        {
            stick.Free();
        }
    }
}
