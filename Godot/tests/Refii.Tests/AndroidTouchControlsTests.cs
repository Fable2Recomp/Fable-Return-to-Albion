using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Titles.Fable2;
using Refii.Titles.Fable2.PubGames.Framework;
using Refii.Game;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The touch controls' layout, finger bindings, latches, stick and menu pointer, without a scene.</summary>
[TestSuite]
public sealed class AndroidTouchControlsTests
{
    /// <summary>The S25+'s canvas: 2340×1080 under the Android 720-unit stretch.</summary>
    private static readonly Vector2 Phone = new(1560f, 720f);

    /// <summary>The pub games as the guest names them (GamePubGames.Showing).</summary>
    private static readonly string[] PubGames = { "keystone", "magedice", "wizardstower" };

    private static AndroidTouchPad Pad(TouchContext context)
    {
        var pad = new AndroidTouchPad();
        pad.SetLayout(context, AndroidTouchLayout.Build(Phone, 1f, context), System.Array.Empty<Rect2>());
        return pad;
    }

    private static Vector2 Centre(AndroidTouchPad pad, string id) =>
        pad.Controls.First(control => control.Id == id).Bounds.GetCenter();

    /// <summary>At every allowed scale, every button lies on the screen and no two overlap where a finger would land.</summary>
    [TestCase]
    public void LayoutsFitTheScreenWithoutOverlaps()
    {
        foreach (float scale in new[] { AndroidTouchLayout.MinScale, 1f, AndroidTouchLayout.MaxScale })
        foreach (TouchContext context in new[] { TouchContext.World, TouchContext.Wheel, TouchContext.Menu, TouchContext.PubGame })
        foreach (string pubGame in context == TouchContext.PubGame ? PubGames : new[] { "" })
        {
            var buttons = AndroidTouchLayout.Build(Phone, scale, context, pubGame: pubGame)
                .Where(control => control.Kind is not (TouchControlKind.Stick or TouchControlKind.Look)).ToArray();
            AssertThat(buttons.Length > 0).IsTrue();
            foreach (TouchControl control in buttons)
            {
                AssertThat(new Rect2(Vector2.Zero, Phone).Encloses(control.Bounds)).IsTrue();
                foreach (TouchControl other in buttons)
                {
                    if (!ReferenceEquals(control, other))
                        AssertThat(Overlaps(control, other)).IsFalse();
                }
            }
        }

        // A controller in hand hides everything but Options.
        AssertThat(AndroidTouchLayout.Build(Phone, 1f, TouchContext.Hidden).Select(c => c.Id).ToArray()).ContainsExactly("Options");
    }

    /// <summary>Two round buttons overlap by their radii, anything else by its rectangle.</summary>
    private static bool Overlaps(TouchControl a, TouchControl b) => a.Round && b.Round
        ? a.Bounds.GetCenter().DistanceTo(b.Bounds.GetCenter()) < (a.Bounds.Size.X + b.Bounds.Size.X) * 0.5f
        : a.Bounds.Intersects(b.Bounds);

    /// <summary>The world has the stick and camera; a menu leaves the glass to the menu's own taps.</summary>
    [TestCase]
    public void ContextsAdaptTheControls()
    {
        string[] world = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World).Select(control => control.Id).ToArray();
        string[] menu = AndroidTouchLayout.Build(Phone, 1f, TouchContext.Menu).Select(control => control.Id).ToArray();
        string[] wheel = AndroidTouchLayout.Build(Phone, 1f, TouchContext.Wheel).Select(control => control.Id).ToArray();
        AssertThat(world).Contains("Stick", "Look", "A", "B", "X", "Y", "LT", "RT", "LB", "Start", "Back", "DPad", "Options");
        AssertThat(world).NotContains("RS");
        AssertThat(menu).Contains("A", "B", "X", "Y", "LB", "RB", "Start", "DPad", "Options");
        AssertThat(menu).NotContains("Stick", "Look", "LT", "RT");
        AssertThat(wheel).Contains("Back", "A", "B", "DPad");
        AssertThat(wheel).NotContains("Stick", "Look", "RT");
    }

    /// <summary>
    /// A button stands only when it would act — Y with a ranged weapon, B with a spell, Spells with
    /// two — and the rest keep their places when one goes.
    /// </summary>
    [TestCase]
    public void ButtonsStandOnlyWhenTheyWouldAct()
    {
        string[] Ids(TouchHero hero) => AndroidTouchLayout.Build(Phone, 1f, TouchContext.World, hero: hero).Select(c => c.Id).ToArray();
        AssertThat(Ids(new TouchHero(false, true, true))).NotContains("Y");
        // A villager's Y prompt brings Y back without a bow, named for what it does then.
        TouchControl villagerY = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World, hero: new TouchHero(false, true, true, Villager: true))
            .First(c => c.Id == "Y");
        AssertThat(villagerY.Caption).IsEqual("Info");
        AssertThat(villagerY.Bounds).IsEqual(AndroidTouchLayout.Build(Phone, 1f, TouchContext.World).First(c => c.Id == "Y").Bounds);
        AssertThat(Ids(new TouchHero(true, false, false))).NotContains("B");
        AssertThat(Ids(TouchHero.Everything)).Contains("Y", "B", "RT", "LT");

        // Spells stands for every hero, its hold drawing in the experience orbs, and keeps its name.
        TouchControl Spells(TouchHero hero) => AndroidTouchLayout.Build(Phone, 1f, TouchContext.World, hero: hero).First(c => c.Id == "RT");
        AssertThat(Spells(new TouchHero(true, false, false)).Caption).IsEqual("Spells");
        AssertThat(Spells(new TouchHero(true, true, false)).Caption).IsEqual("Spells");
        AssertThat(Spells(TouchHero.Everything).Caption).IsEqual("Spells");

        // Hiding one moves no other.
        TouchControl x = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World, hero: new TouchHero(false, false, false)).First(c => c.Id == "X");
        AssertThat(x.Bounds).IsEqual(AndroidTouchLayout.Build(Phone, 1f, TouchContext.World).First(c => c.Id == "X").Bounds);

        // In a menu B is Back and Y the flip, whatever the hero carries.
        string[] menu = AndroidTouchLayout.Build(Phone, 1f, TouchContext.Menu, hero: new TouchHero(false, false, false)).Select(c => c.Id).ToArray();
        AssertThat(menu).Contains("Y", "B");
    }

    /// <summary>The thumb's cluster is the fight's: the face buttons, Spells and Lock; Emotes sits in the top row, View beside Options.</summary>
    [TestCase]
    public void TheThumbClusterIsTheFights()
    {
        var controls = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World);
        float rowBottom = controls.First(c => c.Id == "Start").Bounds.End.Y;
        foreach (string id in new[] { "Back", "Start" })
            AssertThat(controls.First(c => c.Id == id).Bounds.Position.Y < 30f).IsTrue();
        // View sits beside Options in the bottom-left corner.
        Rect2 view = controls.First(c => c.Id == "LB").Bounds, options = controls.First(c => c.Id == "Options").Bounds;
        AssertThat(view.Position.Y).IsEqual(options.Position.Y);
        AssertThat(view.Position.X > options.End.X).IsTrue();
        foreach (string id in new[] { "A", "X", "Y", "B", "LT" })
            AssertThat(controls.First(c => c.Id == id).Bounds.Position.Y > rowBottom).IsTrue();
        // Spells and Lock stand at the side edges, mirrored, on their triggers' sides (Lock left,
        // Spells right), a third of the way down: clear of the camera's middle and of a corner's or
        // an edge-centred punch-hole camera.
        foreach (float scale in new[] { AndroidTouchLayout.MinScale, 1f, AndroidTouchLayout.MaxScale })
        {
            var scaled = AndroidTouchLayout.Build(Phone, scale, TouchContext.World);
            Rect2 spells = scaled.First(c => c.Id == "RT").Bounds, lockAt = scaled.First(c => c.Id == "LT").Bounds;
            AssertThat(Mathf.Abs(lockAt.Position.X - AndroidTouchLayout.OptionsInset * scale) < 0.01f).IsTrue();
            AssertThat(Mathf.Abs(spells.End.X - (Phone.X - AndroidTouchLayout.OptionsInset * scale)) < 0.01f).IsTrue();
            AssertThat(Mathf.Abs(lockAt.Position.X - (Phone.X - spells.End.X)) < 0.5f).IsTrue();
            AssertThat(spells.GetCenter().Y).IsEqual(lockAt.GetCenter().Y);
            AssertThat(spells.End.Y < Phone.Y * 0.5f - 20f && spells.Position.Y > Phone.Y * 0.08f).IsTrue();
            TouchControl spellsControl = scaled.First(c => c.Id == "RT");
            foreach (string face in new[] { "A", "B", "X", "Y" })
                AssertThat(Overlaps(spellsControl, scaled.First(c => c.Id == face))).IsFalse();
        }
        // A latched Emotes is tapped closed where it was tapped open.
        AssertThat(AndroidTouchLayout.Build(Phone, 1f, TouchContext.Wheel).First(c => c.Id == "Back").Bounds)
            .IsEqual(controls.First(c => c.Id == "Back").Bounds);
    }

    /// <summary>Options sits in the bottom-left corner in every context, clear of the D-pad and the screen's rounded corner.</summary>
    [TestCase]
    public void OptionsSitsBottomLeftClearOfTheDPad()
    {
        foreach (TouchContext context in new[] { TouchContext.Hidden, TouchContext.World, TouchContext.Wheel, TouchContext.Menu, TouchContext.PubGame })
        {
            var controls = AndroidTouchLayout.Build(Phone, 1f, context, pubGame: "keystone");
            Rect2 options = controls.First(c => c.Id == "Options").Bounds;
            AssertThat(options.Position.X).IsEqual(AndroidTouchLayout.OptionsInset);
            AssertThat(options.End.Y > Phone.Y - 30f && options.End.Y <= Phone.Y).IsTrue();
            if (controls.FirstOrDefault(c => c.Id == "DPad") is { } dpad)
                AssertThat(dpad.Bounds.Intersects(options)).OverrideFailureMessage($"D-pad over Options in {context}").IsFalse();
        }

        // The menus' drawn D-pad stands well clear above Options: a thumb pressing Down misses it.
        var menu = AndroidTouchLayout.Build(Phone, 1f, TouchContext.Menu);
        float clearance = menu.First(c => c.Id == "Options").Bounds.Position.Y - menu.First(c => c.Id == "DPad").Bounds.End.Y;
        AssertThat(clearance >= AndroidTouchLayout.DPadClearance - 0.5f).IsTrue();
    }

    /// <summary>A wheel's opener stays where it was tapped open, so the same spot taps it closed; so does the D-pad that steers it.</summary>
    [TestCase]
    public void LatchedOpenersStayPutInTheWheel()
    {
        AndroidTouchPad world = Pad(TouchContext.World), wheel = Pad(TouchContext.Wheel);
        foreach (string id in new[] { "Back", "DPad" })
            AssertThat(Centre(wheel, id)).IsEqual(Centre(world, id));
    }

    /// <summary>A finger holds its button until it lifts, and a short tap is held long enough for the guest to read.</summary>
    [TestCase]
    public void ButtonsHoldAndShortTapsLinger()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, Centre(pad, "X"), 1.0);
        AssertThat(pad.Sample(1.5).Buttons).IsEqual(GameInput.PadX);
        pad.Up(0, Centre(pad, "X"), 1.6);
        AssertThat(pad.Sample(1.6).Buttons).IsEqual(0u);

        pad.Down(1, Centre(pad, "A"), 2.0);
        pad.Up(1, Centre(pad, "A"), 2.01);
        AssertThat(pad.Sample(2.05).Buttons).IsEqual(GameInput.PadA);
        AssertThat(pad.Sample(2.0 + AndroidTouchPad.MinimumHold + 0.001).Buttons).IsEqual(0u);
    }

    /// <summary>
    /// Nothing stands over the health (top left) or the spell-charge meter (left edge); the item
    /// buttons follow the HUD's item slots.
    /// </summary>
    [TestCase]
    public void TheHudCornersStayClear()
    {
        var health = new Rect2(0f, 0f, 400f, 120f);
        // Where Android draws the spell-charge meter: moved in from the left edge, where Lock stands.
        var spellCharge = new Rect2(85f + Engine_HudLayout.AndroidSpellChargeInset, 195f, 65f, 320f);
        foreach (float scale in new[] { AndroidTouchLayout.MinScale, 1f, AndroidTouchLayout.MaxScale })
        foreach (TouchContext context in new[] { TouchContext.World, TouchContext.Wheel })
        foreach (TouchControl control in AndroidTouchLayout.Build(Phone, scale, context))
        {
            if (control.Kind is TouchControlKind.Stick or TouchControlKind.Look) continue;
            AssertThat(control.Bounds.Intersects(health)).IsFalse();
            if (!control.OverHud) AssertThat(control.Bounds.Intersects(spellCharge)).IsFalse();
        }

        // The item buttons follow the HUD's slots wherever it states them.
        TouchControl items = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World, (new Vector2(140f, 560f), 40f))
            .First(c => c.Id == "DPad");
        AssertThat(items.Bounds.GetCenter()).IsEqual(new Vector2(140f, 560f));
        AssertThat(items.OverHud).IsTrue();
    }

    /// <summary>Options is a host action, not a pad button; a double tap on the camera side clicks the right stick.</summary>
    [TestCase]
    public void OptionsAndTheCameraDoubleTap()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, Centre(pad, "Options"), 0.0);
        AssertThat(pad.Sample(0.05)).IsEqual(TouchPadOutput.Neutral);
        pad.Up(0, Centre(pad, "Options"), 0.1);
        AssertThat(pad.TakeActions().ToArray()).ContainsExactly("Options");
        AssertThat(pad.TakeActions().Count).IsEqual(0);

        var look = new Vector2(1000f, 300f);
        pad.Down(1, look, 1.0);
        pad.Up(1, look, 1.1);
        AssertThat(pad.Sample(1.15).Buttons).IsEqual(0u);
        pad.Down(1, look + new Vector2(10f, 5f), 1.3);
        pad.Up(1, look + new Vector2(10f, 5f), 1.35);
        AssertThat(pad.Sample(1.36).Buttons).IsEqual(GameInput.PadRightThumb);
        AssertThat(pad.Sample(1.5).Buttons).IsEqual(0u);

        // A drag is the camera, never a tap.
        pad.Down(2, look, 3.0);
        pad.Move(2, look + new Vector2(80f, 0f), new Vector2(80f, 0f));
        pad.Up(2, look + new Vector2(80f, 0f), 3.1);
        pad.Down(2, look, 3.2);
        pad.Up(2, look, 3.25);
        AssertThat(pad.Sample(3.26).Buttons).IsEqual(0u);
    }

    /// <summary>Two thumbs press together: the stick and a button, OR'd like any two devices.</summary>
    [TestCase]
    public void TwoFingersAtOnce()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, new Vector2(300f, 500f), 0.0);
        pad.Move(0, new Vector2(300f, 500f - 200f), new Vector2(0f, -200f));
        pad.Down(1, Centre(pad, "B"), 0.0);
        TouchPadOutput output = pad.Sample(0.5);
        AssertThat(output.Buttons & GameInput.PadB).IsEqual(GameInput.PadB);
        AssertThat(output.LeftY).IsEqual(32767);
        AssertThat(output.LeftX).IsEqual(0);
    }

    /// <summary>A tap latches a hold on and the next tap lets it go; a long press is an ordinary hold.</summary>
    [TestCase]
    public void LatchesTapOnTapOffOrHold()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        Vector2 lockAt = Centre(pad, "LT");
        pad.Down(0, lockAt, 0.0);
        pad.Up(0, lockAt, 0.1);
        AssertThat(pad.Sample(1.0).LeftTrigger).IsEqual(255u);
        AssertThat(pad.IsLatched("LT")).IsTrue();
        pad.Down(0, lockAt, 2.0);
        AssertThat(pad.Sample(2.0).LeftTrigger).IsEqual(255u);
        pad.Up(0, lockAt, 2.1);
        AssertThat(pad.Sample(2.2).LeftTrigger).IsEqual(0u);

        Vector2 viewAt = Centre(pad, "LB");
        pad.Down(0, viewAt, 3.0);
        AssertThat(pad.Sample(3.5).Buttons).IsEqual(GameInput.PadLeftShoulder);
        pad.Up(0, viewAt, 3.0 + AndroidTouchPad.LatchTap + 0.1);
        AssertThat(pad.Sample(4.0).Buttons).IsEqual(0u);
        AssertThat(pad.IsLatched("LB")).IsFalse();
    }

    /// <summary>A latch the game is done with lets go as if tapped off.</summary>
    [TestCase]
    public void UnlatchLetsGo()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, Centre(pad, "Back"), 0.0);
        pad.Up(0, Centre(pad, "Back"), 0.1);
        AssertThat(pad.Sample(0.2).Buttons).IsEqual(GameInput.PadBack);
        pad.Unlatch("Back");
        AssertThat(pad.IsLatched("Back")).IsFalse();
        AssertThat(pad.Sample(0.3).Buttons).IsEqual(0u);
    }

    /// <summary>A latched Emotes follows the guest's wheel: gone after it was drawn, or never drawn at all.</summary>
    [TestCase]
    public void EmotesLetGoWhenTheWheelCloses()
    {
        var release = new AndroidTouchWheelRelease();
        AssertThat(release.Update(true, false, 0.1)).IsFalse();   // opening
        AssertThat(release.Update(true, true, 0.1)).IsFalse();    // drawn
        AssertThat(release.Update(true, false, 0.1)).IsFalse();   // a group's hop
        AssertThat(release.Update(true, true, 0.1)).IsFalse();
        AssertThat(release.Update(true, false, 0.2)).IsFalse();
        AssertThat(release.Update(true, false, 0.1)).IsTrue();    // closed: an expression started

        release.Update(false, false, 0.1);
        AssertThat(release.Update(true, false, 1.0)).IsFalse();
        AssertThat(release.Update(true, false, 0.6)).IsTrue();    // the guest never opened it
    }

    /// <summary>A latched Lock ends as the wheel, which has no Lock, opens; a latched Emotes survives the wheel it opens and ends when a menu opens.</summary>
    [TestCase]
    public void LatchesEndWithThePlay()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, Centre(pad, "Back"), 0.0);
        pad.Up(0, Centre(pad, "Back"), 0.05);
        pad.Down(0, Centre(pad, "LT"), 0.5);
        pad.Up(0, Centre(pad, "LT"), 0.55);
        pad.SetLayout(TouchContext.Wheel, AndroidTouchLayout.Build(Phone, 1f, TouchContext.Wheel), System.Array.Empty<Rect2>());
        AssertThat(pad.Sample(0.7).Buttons & GameInput.PadBack).IsEqual(GameInput.PadBack);
        AssertThat(pad.Sample(0.7).LeftTrigger).IsEqual(0u);
        pad.SetLayout(TouchContext.Menu, AndroidTouchLayout.Build(Phone, 1f, TouchContext.Menu), System.Array.Empty<Rect2>());
        AssertThat(pad.Sample(0.8).Buttons & GameInput.PadBack).IsEqual(0u);
    }

    /// <summary>The stick starts outside the guest's own dead zone, is full at the ring, and sprints past it.</summary>
    [TestCase]
    public void StickDeflectionAndSprint()
    {
        float r = AndroidTouchLayout.StickRadius;
        AssertThat(AndroidTouchPad.StickAxes(new Vector2(r * 0.05f, 0f), r)).IsEqual((0, 0));
        (int smallX, _) = AndroidTouchPad.StickAxes(new Vector2(r * 0.12f, 0f), r);
        AssertThat(smallX > 7849).IsTrue();
        AssertThat(AndroidTouchPad.StickAxes(new Vector2(0f, -r), r)).IsEqual((0, 32767));
        AssertThat(AndroidTouchPad.StickAxes(new Vector2(-r, 0f), r)).IsEqual((-32767, 0));

        AndroidTouchPad pad = Pad(TouchContext.World);
        var start = new Vector2(300f, 450f);
        pad.Down(0, start, 0.0);
        pad.Move(0, start + new Vector2(r, 0f), new Vector2(r, 0f));
        AssertThat(pad.Sample(0.1).Buttons & GameInput.PadA).IsEqual(0u);
        pad.Move(0, start + new Vector2(r * (AndroidTouchPad.SprintReach + 0.1f), 0f), new Vector2(r, 0f));
        AssertThat(pad.Sample(0.2).Buttons & GameInput.PadA).IsEqual(GameInput.PadA);
        AssertThat(pad.Sample(0.2).LeftX).IsEqual(32767);
        pad.Up(0, start, 0.3);
        AssertThat(pad.Sample(0.4)).IsEqual(TouchPadOutput.Neutral);
    }

    /// <summary>The camera turns with a drag on free glass, and with a steering button once it slides past its slop.</summary>
    [TestCase]
    public void CameraDragAndSteeringButtons()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        pad.Down(0, new Vector2(1000f, 200f), 0.0);
        pad.Move(0, new Vector2(1030f, 190f), new Vector2(30f, -10f));
        AssertThat(pad.TakeLook()).IsEqual(new Vector2(30f, -10f));
        AssertThat(pad.TakeLook()).IsEqual(Vector2.Zero);
        AssertThat(pad.Sample(0.1)).IsEqual(TouchPadOutput.Neutral);
        pad.Up(0, new Vector2(1030f, 190f), 0.2);

        Vector2 y = Centre(pad, "Y");
        pad.Down(1, y, 1.0);
        pad.Move(1, y + new Vector2(10f, 0f), new Vector2(10f, 0f));
        AssertThat(pad.TakeLook()).IsEqual(Vector2.Zero);
        pad.Move(1, y + new Vector2(30f, 0f), new Vector2(20f, 0f));
        pad.Move(1, y + new Vector2(50f, 0f), new Vector2(20f, 0f));
        AssertThat(pad.TakeLook()).IsEqual(new Vector2(20f, 0f));
        AssertThat(pad.Sample(1.5).Buttons).IsEqual(GameInput.PadY);
    }

    /// <summary>The D-pad picks its direction from where the finger is, and rolls between them.</summary>
    [TestCase]
    public void DPadRolls()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        TouchControl dpad = pad.Controls.First(control => control.Id == "DPad");
        Vector2 centre = dpad.Bounds.GetCenter();
        float reach = dpad.Bounds.Size.X * 0.4f;
        pad.Down(0, centre + new Vector2(0f, -reach), 0.0);
        AssertThat(pad.Sample(0.2).Buttons).IsEqual(GameInput.PadUp);
        pad.Move(0, centre + new Vector2(reach, 0f), new Vector2(reach, reach));
        AssertThat(pad.Sample(0.3).Buttons).IsEqual(GameInput.PadRight);
        pad.Move(0, centre, Vector2.Zero);
        AssertThat(pad.Sample(0.4).Buttons).IsEqual(0u);
    }

    /// <summary>In a menu a finger on no control is the pointer — one at a time — and the diagnostics' rectangles pass through.</summary>
    [TestCase]
    public void MenuTapsAreThePointer()
    {
        AndroidTouchPad pad = Pad(TouchContext.Menu);
        pad.Down(0, new Vector2(700f, 300f), 0.0);
        pad.Down(1, new Vector2(800f, 300f), 0.0);
        pad.Move(0, new Vector2(700f, 320f), new Vector2(0f, 20f));
        pad.Up(0, new Vector2(700f, 320f), 0.1);
        TouchPointerPhase[] phases = pad.TakePointerEvents().Select(e => e.Phase).ToArray();
        AssertThat(phases).ContainsExactly(TouchPointerPhase.Down, TouchPointerPhase.Move, TouchPointerPhase.Up);
        AssertThat(pad.Sample(0.2)).IsEqual(TouchPadOutput.Neutral);

        var world = new AndroidTouchPad();
        var diagnostics = new[] { new Rect2(1300f, 20f, 240f, 56f) };
        world.SetLayout(TouchContext.World, AndroidTouchLayout.Build(Phone, 1f, TouchContext.World), diagnostics);
        world.Down(0, new Vector2(1400f, 40f), 0.0);
        AssertThat(world.TakePointerEvents().Count).IsEqual(1);
        world.Down(1, new Vector2(1400f, 300f), 0.0);
        AssertThat(world.TakePointerEvents().Count).IsEqual(0);
    }

    /// <summary>A tap clicks where it landed, after the pointer arrives; a swipe scrolls and never clicks; a still finger becomes a held press.</summary>
    [TestCase]
    public void PointerTapsSwipesAndHolds()
    {
        var pointer = new AndroidTouchPointer();
        var at = new Vector2(500f, 400f);
        pointer.Feed(new TouchPointerEvent(TouchPointerPhase.Down, at, Vector2.Zero), 0.0);
        pointer.Feed(new TouchPointerEvent(TouchPointerPhase.Up, at, Vector2.Zero), 0.05);
        AssertThat(pointer.Take().Select(a => a.Kind).ToArray()).ContainsExactly(TouchMouseKind.Hover);
        // The click plays out a frame at a time, so a hover-picked view sees the pointer first.
        pointer.Tick(0.06);
        AssertThat(pointer.Take().Select(a => a.Kind).ToArray()).ContainsExactly(TouchMouseKind.Press);
        pointer.Tick(0.07);
        AssertThat(pointer.Take().Select(a => a.Kind).ToArray()).ContainsExactly(TouchMouseKind.Release);

        pointer.Feed(new TouchPointerEvent(TouchPointerPhase.Down, at, Vector2.Zero), 1.0);
        pointer.Feed(new TouchPointerEvent(TouchPointerPhase.Move, at - new Vector2(0f, 80f), Vector2.Zero), 1.05);
        pointer.Tick(1.5);
        pointer.Feed(new TouchPointerEvent(TouchPointerPhase.Up, at - new Vector2(0f, 80f), Vector2.Zero), 1.6);
        TouchMouseAction[] swipe = pointer.Take().ToArray();
        AssertThat(swipe.Any(a => a.Kind is TouchMouseKind.Press or TouchMouseKind.Release)).IsFalse();
        AssertThat(swipe.Where(a => a.Kind == TouchMouseKind.Wheel).Sum(a => a.Notches)).IsEqual(2);

        pointer.Feed(new TouchPointerEvent(TouchPointerPhase.Down, at, Vector2.Zero), 2.0);
        pointer.Tick(2.0 + AndroidTouchPointer.HoldDelay);
        AssertThat(pointer.Pressed).IsTrue();
        pointer.Feed(new TouchPointerEvent(TouchPointerPhase.Move, at + new Vector2(40f, 0f), Vector2.Zero), 2.4);
        pointer.Cancel();
        AssertThat(pointer.Take().Select(a => a.Kind).ToArray())
            .ContainsExactly(TouchMouseKind.Hover, TouchMouseKind.Press, TouchMouseKind.Drag, TouchMouseKind.Release);
    }

    /// <summary>
    /// Godot's own touch-to-mouse emulation is off while the controls own the fingers, and on under
    /// the dev menu, whose window takes every finger before the controls could pass it on.
    /// </summary>
    [TestCase]
    public void TheDevMenuGetsGodotsMouse()
    {
        AssertThat(AndroidTouchControls.EmulatesMouse(controlsEnabled: true, devMenuOpen: false, projectSetting: true)).IsFalse();
        AssertThat(AndroidTouchControls.EmulatesMouse(controlsEnabled: true, devMenuOpen: true, projectSetting: true)).IsTrue();
        AssertThat(AndroidTouchControls.EmulatesMouse(controlsEnabled: true, devMenuOpen: true, projectSetting: false)).IsTrue();
        AssertThat(AndroidTouchControls.EmulatesMouse(controlsEnabled: false, devMenuOpen: false, projectSetting: true)).IsTrue();
        AssertThat(AndroidTouchControls.EmulatesMouse(controlsEnabled: false, devMenuOpen: false, projectSetting: false)).IsFalse();
    }

    /// <summary>
    /// Photo mode flies the camera: the stick, the drag and Raise/Lower, and nothing else — with the
    /// panel hidden for a clear view, only the stick and the drag.
    /// </summary>
    [TestCase]
    public void PhotoModeFliesTheCamera()
    {
        var photo = AndroidTouchLayout.Build(Phone, 1f, TouchContext.Photo);
        AssertThat(photo.Select(c => c.Id).ToArray()).ContainsExactly("LT", "RT", "DPad", "Stick", "Look");
        // The settings' D-pad stands whole on the screen, clear of Raise.
        TouchControl dpad = photo.First(c => c.Id == "DPad");
        AssertThat(new Rect2(Vector2.Zero, Phone).Encloses(dpad.Bounds)).IsTrue();
        AssertThat(dpad.Bounds.End.Y < photo.First(c => c.Id == "RT").Bounds.Position.Y).IsTrue();
        TouchControl lower = photo.First(c => c.Id == "LT"), raise = photo.First(c => c.Id == "RT");
        AssertThat(lower.Trigger).IsEqual(TouchTrigger.Left);
        AssertThat(raise.Trigger).IsEqual(TouchTrigger.Right);
        AssertThat(lower.Kind).IsEqual(TouchControlKind.Button);
        AssertThat(new Rect2(Vector2.Zero, Phone).Encloses(raise.Bounds)).IsTrue();
        AssertThat(Overlaps(lower, raise)).IsFalse();
        AssertThat(AndroidTouchLayout.Build(Phone, 1f, TouchContext.Photo, photoPanel: false).Select(c => c.Id).ToArray())
            .ContainsExactly("Stick", "Look");

        // Raise and Lower are held only while pressed: no latch to outlive photo mode.
        AndroidTouchPad pad = Pad(TouchContext.Photo);
        pad.Down(0, Centre(pad, "RT"), 0.0);
        AssertThat(pad.Sample(0.2).RightTrigger).IsEqual(255u);
        pad.Up(0, Centre(pad, "RT"), 0.4);
        AssertThat(pad.Sample(0.5).RightTrigger).IsEqual(0u);
    }

    /// <summary>
    /// Decorating flies the camera as photo mode does; a held piece adds Place, Turn and Item, all
    /// on the screen and none over another; a hidden panel gives a Panel button to bring it back.
    /// </summary>
    [TestCase]
    public void DecoratingFliesTheCameraAndWorksTheHeldPiece()
    {
        AssertThat(AndroidTouchLayout.Build(Phone, 1f, TouchContext.Decorate).Select(c => c.Id).ToArray())
            .ContainsExactly("Lower", "Raise", "Stick", "Look");
        AssertThat(AndroidTouchLayout.Build(Phone, 1f, TouchContext.Decorate, photoPanel: false).Select(c => c.Id).ToArray())
            .ContainsExactly("Panel", "Lower", "Raise", "Stick", "Look");
        var placing = AndroidTouchLayout.Build(Phone, 1f, TouchContext.Decorate, placing: true);
        AssertThat(placing.Select(c => c.Id).ToArray())
            .ContainsExactly("Place", "TurnLeft", "TurnRight", "ItemDown", "ItemUp", "Lower", "Raise", "Stick", "Look");
        var buttons = placing.Where(c => c.Kind is not (TouchControlKind.Stick or TouchControlKind.Look)).ToList();
        foreach (TouchControl button in buttons)
        {
            AssertThat(new Rect2(Vector2.Zero, Phone).Encloses(button.Bounds)).IsTrue();
            AssertThat(buttons.Any(other => other != button && Overlaps(button, other))).IsFalse();
        }

        AssertThat(placing.First(c => c.Id == "Place").Bits).IsEqual(GameInput.PadA);
        AssertThat(placing.First(c => c.Id == "Raise").Trigger).IsEqual(TouchTrigger.Right);
    }

    /// <summary>A tap on the room while decorating is reported with where it landed; a drag only turns the camera.</summary>
    [TestCase]
    public void DecoratingReportsATapWhereItLands()
    {
        AndroidTouchPad pad = Pad(TouchContext.Decorate);
        var at = new Vector2(1000f, 300f);
        pad.Down(0, at, 0.0);
        pad.Up(0, at, 0.1);
        AssertThat(pad.TakeActions().ToArray()).ContainsExactly(AndroidTouchLayout.DecorateTap);
        AssertThat(pad.LastTap).IsEqual(at);

        pad.Down(1, at, 1.0);
        pad.Move(1, at + new Vector2(80f, 0f), new Vector2(80f, 0f));
        pad.Up(1, at + new Vector2(80f, 0f), 1.1);
        AssertThat(pad.TakeActions().Count).IsEqual(0);
        AssertThat(pad.TakeLook()).IsEqual(new Vector2(80f, 0f));
    }

    /// <summary>
    /// A finger on the photo panel is the panel's even where the stick zone lies under it, and a
    /// tap on the camera brings a hidden panel back instead of clicking the right stick.
    /// </summary>
    [TestCase]
    public void PhotoModeLeavesThePanelItsTaps()
    {
        Rect2 panel = GamePhotoMode.PanelRectFor(Phone, help: false);
        AssertThat(panel).IsEqual(new Rect2(20f, 64f, 460f, 592f));
        AssertThat(GamePhotoMode.PanelRectFor(Phone, help: true)).IsEqual(new Rect2(550f, 168f, 460f, 384f));

        var pad = new AndroidTouchPad();
        pad.SetLayout(TouchContext.Photo, AndroidTouchLayout.Build(Phone, 1f, TouchContext.Photo), new[] { panel });
        var onPanel = new Vector2(200f, 500f);
        pad.Down(0, onPanel, 0.0);
        AssertThat(pad.Sample(0.1).LeftX).IsEqual(0);
        AssertThat(pad.TakePointerEvents().Count).IsEqual(1);
        pad.Up(0, onPanel, 0.1);
        pad.TakePointerEvents();

        var look = new Vector2(1000f, 300f);
        pad.Down(1, look, 1.0);
        pad.Up(1, look, 1.1);
        pad.Down(1, look, 1.2);
        pad.Up(1, look, 1.25);
        AssertThat(pad.TakeActions().ToArray()).ContainsExactly(AndroidTouchPad.PhotoTap, AndroidTouchPad.PhotoTap);
        AssertThat(pad.Sample(1.26).Buttons).IsEqual(0u);
    }

    /// <summary>
    /// Android moves the spell-charge meters in from the side edges, where Spells and Lock stand:
    /// player one's rightward, player two's leftward, nothing else.
    /// </summary>
    [TestCase]
    public void TheSpellChargeMovesInFromTheEdges()
    {
        var layout = new Engine_HudLayout()
            .Add(new Engine_HudLayout.Piece("group.SpellCharging.SpellCharging", "SpellCharging", new Engine_HudLayout.Anchor(0f, 0.5f), new Vector2(110f, 26f)))
            .Add(new Engine_HudLayout.Piece("group.SpellCharging2.SpellCharging2", "SpellCharging2", new Engine_HudLayout.Anchor(1f, 0.5f), new Vector2(-110f, 26f)))
            .Add(new Engine_HudLayout.Piece("group.PlayerHealth.PlayerHealth", "PlayerHealth", Engine_HudLayout.Anchor.TopLeft, new Vector2(69f, 65f)));
        AssertThat(layout.InsetSpellCharge(Engine_HudLayout.AndroidSpellChargeInset)).IsEqual(2);
        AssertThat(layout.Pieces[0].Offset).IsEqual(new Vector2(110f + Engine_HudLayout.AndroidSpellChargeInset, 26f));
        AssertThat(layout.Pieces[1].Offset).IsEqual(new Vector2(-110f - Engine_HudLayout.AndroidSpellChargeInset, 26f));
        AssertThat(layout.Pieces[2].Offset).IsEqual(new Vector2(69f, 65f));
    }

    /// <summary>
    /// Android lifts the HUD's d-pad, all its pieces together, so a thumb pressing Down clears the
    /// Options button below it; the rest of the HUD stays where it was.
    /// </summary>
    [TestCase]
    public void TheDpadIsLiftedClearOfOptions()
    {
        static Engine_HudLayout.Piece Piece(string id) =>
            new(id, id, Engine_HudLayout.Anchor.BottomLeft, new Vector2(280f, -150f));
        var layout = new Engine_HudLayout()
            .Add(Piece("slidetoggle.DPadDown.down"))
            .Add(Piece("meter.SubtitlesY.dpadmenu"))
            .Add(Piece("group.PlayerHealth.PlayerHealth"));

        AssertThat(layout.LiftDpad(Engine_HudLayout.AndroidDpadLift)).IsEqual(2);
        AssertThat(layout.Pieces[0].Offset).IsEqual(new Vector2(280f, -150f - Engine_HudLayout.AndroidDpadLift));
        AssertThat(layout.Pieces[1].Offset).IsEqual(new Vector2(280f, -150f - Engine_HudLayout.AndroidDpadLift));
        AssertThat(layout.Pieces[2].Offset).IsEqual(new Vector2(280f, -150f));

        // Before the HUD states its slots, the touch D-pad stands where the lifted cross is drawn,
        // its Down slot clear of Options by more than a fingertip.
        (Vector2 centre, float reach) = AndroidTouchLayout.DefaultItemSlots(Phone);
        Rect2 options = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World).First(c => c.Id == "Options").Bounds;
        AssertThat(options.Position.Y - (centre.Y + reach) > 50f).IsTrue();
    }

    /// <summary>
    /// Spells: a tap is the touch picker's and presses nothing; a hold presses RT once it is one,
    /// drawing in the experience orbs, and lets go with the finger.
    /// </summary>
    [TestCase]
    public void SpellsTapsForThePickerAndHoldsForTheTrigger()
    {
        AndroidTouchPad pad = Pad(TouchContext.World);
        Vector2 spells = Centre(pad, "RT");
        pad.Down(0, spells, 0.0);
        AssertThat(pad.Sample(0.1).RightTrigger).IsEqual(0u);
        pad.Up(0, spells, 0.15);
        AssertThat(pad.TakeActions().ToArray()).ContainsExactly("RT");
        AssertThat(pad.Sample(0.2).RightTrigger).IsEqual(0u);

        pad.Down(0, spells, 1.0);
        AssertThat(pad.Sample(1.1).RightTrigger).IsEqual(0u);
        AssertThat(pad.HoldProgress("RT", 1.15)).IsEqual(0.5f);
        AssertThat(pad.Sample(1.35).RightTrigger).IsEqual(255u);
        AssertThat(pad.HoldProgress("RT", 1.35)).IsEqual(1f);
        pad.Up(0, spells, 1.5);
        AssertThat(pad.TakeActions().Count).IsEqual(0);
        AssertThat(pad.Sample(1.6).RightTrigger).IsEqual(0u);
        AssertThat(pad.HoldProgress("RT", 1.6)).IsEqual(0f);
    }

    /// <summary>
    /// The touch spell picker: every spell a target on the screen, none touching
    /// another, and modal — a tap off the spells closes it and presses nothing in the game.
    /// </summary>
    [TestCase]
    public void TheSpellPickerIsModal()
    {
        var eight = Enumerable.Range(0, 8).Select(will => (will, "Spell " + will)).ToList();
        foreach (float scale in new[] { AndroidTouchLayout.MinScale, 1f, AndroidTouchLayout.MaxScale })
        {
            var picker = AndroidTouchLayout.BuildSpellPicker(Phone, scale, eight);
            AssertThat(picker.Count).IsEqual(9);
            AssertThat(picker[^1].Id).IsEqual(AndroidTouchLayout.SpellsClose);
            var icons = picker.Take(8).ToArray();
            foreach (TouchControl icon in icons)
            {
                AssertThat(new Rect2(Vector2.Zero, Phone).Encloses(icon.Bounds)).IsTrue();
                foreach (TouchControl other in icons)
                    if (other != icon) AssertThat(Overlaps(icon, other)).IsFalse();
            }
        }

        var pad = new AndroidTouchPad();
        var offered = new List<(int, string)> { (0, "Shock"), (1, "Blast"), (4, "Vortex") };
        pad.SetLayout(TouchContext.World, AndroidTouchLayout.BuildSpellPicker(Phone, 1f, offered)
            .Concat(AndroidTouchLayout.Build(Phone, 1f, TouchContext.World)).ToList(), System.Array.Empty<Rect2>());
        pad.Down(0, Centre(pad, AndroidTouchLayout.SpellPrefix + 4), 0.0);
        pad.Up(0, Centre(pad, AndroidTouchLayout.SpellPrefix + 4), 0.1);
        AssertThat(pad.TakeActions().ToArray()).ContainsExactly(AndroidTouchLayout.SpellPrefix + 4);

        Vector2 a = Centre(pad, "A");
        pad.Down(1, a, 1.0);
        AssertThat(pad.Sample(1.05).Buttons).IsEqual(0u);
        pad.Up(1, a, 1.1);
        AssertThat(pad.TakeActions().ToArray()).ContainsExactly(AndroidTouchLayout.SpellsClose);
    }

    /// <summary>
    /// A picked spell goes into every power level that offers it and is not already holding it,
    /// through the game's own assignment — as A carries a slot's spell down on the game's selectors.
    /// </summary>
    [TestCase]
    public void APickedSpellFillsEveryLevelThatOffersIt()
    {
        var recorded = new List<(int Slot, int Will)>();
        Engine_UiSource saved = Engine_UiSource.Current;
        Engine_UiModel.SpellAssignState savedSpells = Engine_UiSpellAssignSet.HeroSpells;
        try
        {
            Engine_UiSource.Current = new RecordingSource(recorded);
            // Vortex (will 4) learned to level 3; level 2 already casts it.
            Engine_UiSpellAssignSet.HeroSpells = new Engine_UiModel.SpellAssignState(
                1u, new[] { 0, 4, 0, 0, -1 }, new uint[] { 0b10011, 0b10011, 0b10011, 0b00011, 0 }, 0, false, false);
            AssertThat(Engine_UiSpellAssignSet.ChooseForEveryLevel(4)).IsEqual(2);
            AssertThat(recorded).ContainsExactly((0, 4), (2, 4));
        }
        finally
        {
            Engine_UiSource.Current = saved;
            Engine_UiSpellAssignSet.HeroSpells = savedSpells;
        }
    }

    private sealed class RecordingSource : Engine_UiSource
    {
        private readonly List<(int Slot, int Will)> _recorded;

        public RecordingSource(List<(int Slot, int Will)> recorded) => _recorded = recorded;

        public override uint AssignSpell(int slot, int spell, int player = 1)
        {
            _recorded.Add((slot, spell));
            return 1u;
        }
    }

    /// <summary>
    /// Murgo's mirror: the settings' D-pad and the drag, which turns the hero; a finger on the
    /// mirror's panel is the panel's, and a tap off it presses nothing in the game.
    /// </summary>
    [TestCase]
    public void TheMirrorTakesPanelTapsAndTurnsOnADrag()
    {
        AssertThat(AndroidTouchLayout.Build(Phone, 1f, TouchContext.Mirror).Select(c => c.Id).ToArray()).ContainsExactly("DPad", "Look");
        Rect2 panel = GameMorphMirror.PanelRectFor(Phone);
        AssertThat(panel).IsEqual(new Rect2(20f, 40f, 480f, 640f));

        var pad = new AndroidTouchPad();
        pad.SetLayout(TouchContext.Mirror, AndroidTouchLayout.Build(Phone, 1f, TouchContext.Mirror), new[] { panel });
        var onPanel = new Vector2(250f, 600f);
        pad.Down(0, onPanel, 0.0);
        AssertThat(pad.TakePointerEvents().Count).IsEqual(1);
        pad.Up(0, onPanel, 0.1);
        pad.TakePointerEvents();

        var off = new Vector2(1000f, 300f);
        pad.Down(1, off, 1.0);
        pad.Move(1, off + new Vector2(60f, 0f), new Vector2(60f, 0f));
        AssertThat(pad.TakeLook().X).IsEqual(60f);
        pad.Up(1, off + new Vector2(60f, 0f), 1.2);
        pad.Down(1, off, 2.0);
        pad.Up(1, off, 2.05);
        pad.Down(1, off, 2.1);
        pad.Up(1, off, 2.15);
        AssertThat(pad.Sample(2.2).Buttons).IsEqual(0u);
        AssertThat(pad.TakeActions().Count).IsEqual(0);
    }

    /// <summary>The touch D-pad drives photo mode's and the mirror's settings as the pad's D-pad does.</summary>
    [TestCase]
    public void TheTouchDPadNavigatesTheSettings()
    {
        GameInput.TouchHold saved = GameInput.Touch;
        try
        {
            GameInput.Touch = new GameInput.TouchHold(GameInput.PadUp | GameInput.PadRight, 0u, 0u, 0, 0);
            AssertThat(GamePhotoMode.TouchDPad()).IsEqual(16u | 128u);
            GameInput.Touch = new GameInput.TouchHold(GameInput.PadDown | GameInput.PadLeft | GameInput.PadA, 0u, 0u, 0, 0);
            AssertThat(GamePhotoMode.TouchDPad()).IsEqual(32u | 64u);
        }
        finally
        {
            GameInput.Touch = saved;
        }
    }

    /// <summary>Menu, Emotes, Options and View are pills a finger tall, each with its word, none touching another.</summary>
    [TestCase]
    public void ThePillsAreAFingerTall()
    {
        var world = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World);
        var pills = new[] { "Start", "Back", "Options", "LB" }.Select(id => world.First(c => c.Id == id)).ToArray();
        foreach (TouchControl pill in pills)
        {
            AssertThat(pill.Round).IsFalse();
            AssertThat(pill.Bounds.Size.Y >= 54f).IsTrue();
            AssertThat(pill.Label.Length > 0).IsTrue();
            foreach (TouchControl other in pills)
                if (other != pill) AssertThat(pill.Bounds.Intersects(other.Bounds)).IsFalse();
        }
    }

    /// <summary>
    /// The D-pad drawn in menus reaches a thumb's width each arm, and a finger holding a D-pad
    /// direction keeps it through a wobble past the diagonal, changing only once it is well into
    /// the next arm.
    /// </summary>
    [TestCase]
    public void TheDPadIsLargeAndSteady()
    {
        TouchControl menuDPad = AndroidTouchLayout.Build(Phone, 1f, TouchContext.Menu).First(c => c.Id == "DPad");
        AssertThat(menuDPad.Bounds.Size.X * 0.5f >= AndroidTouchLayout.MenuDPadReach - 0.5f).IsTrue();
        AssertThat(new Rect2(Vector2.Zero, Phone).Encloses(menuDPad.Bounds)).IsTrue();

        TouchControl dpad = AndroidTouchLayout.Build(Phone, 1f, TouchContext.World).First(c => c.Id == "DPad");

        Vector2 centre = dpad.Bounds.GetCenter();
        Vector2 At(float degrees) => centre + Vector2.Right.Rotated(Mathf.DegToRad(-degrees)) * 60f;
        AssertThat(AndroidTouchPad.DPadDirection(dpad, At(0f))).IsEqual(GameInput.PadRight);
        // Just past the diagonal toward up: a fresh press reads up, a held right stays right.
        AssertThat(AndroidTouchPad.DPadDirection(dpad, At(50f))).IsEqual(GameInput.PadUp);
        AssertThat(AndroidTouchPad.DPadDirection(dpad, At(50f), GameInput.PadRight)).IsEqual(GameInput.PadRight);
        // Well into up: the held right gives way.
        AssertThat(AndroidTouchPad.DPadDirection(dpad, At(70f), GameInput.PadRight)).IsEqual(GameInput.PadUp);
        // The centre presses nothing.
        AssertThat(AndroidTouchPad.DPadDirection(dpad, centre + new Vector2(5f, 5f), GameInput.PadRight)).IsEqual(0u);

        var pad = new AndroidTouchPad();
        pad.SetLayout(TouchContext.World, AndroidTouchLayout.Build(Phone, 1f, TouchContext.World), System.Array.Empty<Rect2>());
        pad.Down(0, At(0f), 0.0);
        AssertThat(pad.Sample(0.05).Buttons & GameInput.PadRight).IsEqual(GameInput.PadRight);
        pad.Move(0, At(52f), At(52f) - At(0f));
        AssertThat(pad.Sample(0.1).Buttons & (GameInput.PadRight | GameInput.PadUp)).IsEqual(GameInput.PadRight);
        pad.Move(0, At(75f), At(75f) - At(52f));
        AssertThat(pad.Sample(0.15).Buttons & (GameInput.PadRight | GameInput.PadUp)).IsEqual(GameInput.PadUp);
    }

    /// <summary>A menu's previous and next tab stand mirrored at the side edges, above the menus' D-pad.</summary>
    [TestCase]
    public void TheTabsTurnFromTheSideEdges()
    {
        foreach (float scale in new[] { AndroidTouchLayout.MinScale, 1f, AndroidTouchLayout.MaxScale })
        {
            var menu = AndroidTouchLayout.Build(Phone, scale, TouchContext.Menu);
            TouchControl previous = menu.First(c => c.Id == "LB"), next = menu.First(c => c.Id == "RB");
            AssertThat(previous.Caption).IsEqual("Prev");
            AssertThat(next.Caption).IsEqual("Next");
            AssertThat(Mathf.Abs(previous.Bounds.Position.X - AndroidTouchLayout.OptionsInset * scale) < 0.01f).IsTrue();
            AssertThat(Mathf.Abs(next.Bounds.End.X - (Phone.X - AndroidTouchLayout.OptionsInset * scale)) < 0.01f).IsTrue();
            AssertThat(previous.Bounds.GetCenter().Y).IsEqual(next.Bounds.GetCenter().Y);
            AssertThat(previous.Bounds.End.Y < menu.First(c => c.Id == "DPad").Bounds.Position.Y).IsTrue();
        }
    }

    /// <summary>Each control that stands for a controller button the face does not name carries that button's name, as the prompts say it.</summary>
    [TestCase]
    public void TheControlsCarryTheControllersNames()
    {
        string Named(TouchContext context, string id) =>
            AndroidTouchLayout.PadName(AndroidTouchLayout.Build(Phone, 1f, context).First(c => c.Id == id));

        AssertThat(Named(TouchContext.World, "RT")).IsEqual("RT");
        AssertThat(Named(TouchContext.World, "LT")).IsEqual("LT");
        AssertThat(Named(TouchContext.World, "Start")).IsEqual("Start");
        AssertThat(Named(TouchContext.World, "Back")).IsEqual("Back");
        AssertThat(Named(TouchContext.World, "LB")).IsEqual("LB");
        AssertThat(Named(TouchContext.Menu, "RB")).IsEqual("RB");
        // The face buttons say it on their faces; Options and the D-pad press no one button.
        AssertThat(Named(TouchContext.World, "A")).IsEmpty();
        AssertThat(Named(TouchContext.World, "Options")).IsEmpty();
        AssertThat(Named(TouchContext.World, "DPad")).IsEmpty();
    }

    /// <summary>
    /// A pub game has the pad its legend names: the D-pad, A, B, Y (the bank), LB (the largest bet),
    /// Menu (Start: the pause menu), and the tips and standings on the triggers; Keystone alone X
    /// and RB. No stick, no camera, nothing of the world's.
    /// </summary>
    [TestCase]
    public void APubGameHasThePadItsLegendNames()
    {
        string[] Ids(string game) => AndroidTouchLayout.Build(Phone, 1f, TouchContext.PubGame, pubGame: game).Select(c => c.Id).ToArray();
        foreach (string game in PubGames)
        {
            AssertThat(Ids(game)).Contains("DPad", "A", "B", "Y", "LB", "Start", "Odds", "Stats", "Options");
            AssertThat(Ids(game)).NotContains("Stick", "Look", "LT", "RT", "Back");
        }

        AssertThat(Ids("keystone")).Contains("X", "RB");
        AssertThat(Ids("magedice")).NotContains("X", "RB");
        AssertThat(Ids("wizardstower")).NotContains("X", "RB");

        var keystone = AndroidTouchLayout.Build(Phone, 1f, TouchContext.PubGame, pubGame: "keystone");
        TouchControl lb = keystone.First(c => c.Id == "LB"), rb = keystone.First(c => c.Id == "RB");
        AssertThat(lb.Bits).IsEqual(GameInput.PadLeftShoulder);
        AssertThat(rb.Bits).IsEqual(GameInput.PadRightShoulder);
        // Lettered as the board's legend names them, where a menu's tabs stand.
        AssertThat(lb.Label).IsEqual("LB");
        AssertThat(rb.Label).IsEqual("RB");
        AssertThat(Mathf.Abs(lb.Bounds.Position.X - AndroidTouchLayout.OptionsInset) < 0.01f).IsTrue();
        AssertThat(Mathf.Abs(rb.Bounds.End.X - (Phone.X - AndroidTouchLayout.OptionsInset)) < 0.01f).IsTrue();
        AssertThat(lb.Bounds.GetCenter().Y).IsEqual(rb.Bounds.GetCenter().Y);
        AssertThat(lb.Bounds.End.Y < keystone.First(c => c.Id == "DPad").Bounds.Position.Y).IsTrue();
        AssertThat(keystone.First(c => c.Id == "Y").Caption).IsEqual("Bank");
        // The face buttons keep the world's places and sizes: a bet is pressed and held there.
        foreach (string id in new[] { "A", "X", "Y", "B" })
            AssertThat(keystone.First(c => c.Id == id).Bounds)
                .IsEqual(AndroidTouchLayout.Build(Phone, 1f, TouchContext.World).First(c => c.Id == id).Bounds);
        // The triggers in the top row, RT's beside Menu and LT's mirroring it at the top left.
        Rect2 standings = keystone.First(c => c.Id == "Stats").Bounds, tips = keystone.First(c => c.Id == "Odds").Bounds;
        Rect2 menu = keystone.First(c => c.Id == "Start").Bounds;
        AssertThat(standings.Position.Y).IsEqual(menu.Position.Y);
        AssertThat(standings.End.X < menu.Position.X).IsTrue();
        AssertThat(tips.Position.X).IsEqual(AndroidTouchLayout.OptionsInset);
        AssertThat(tips.Position.Y).IsEqual(menu.Position.Y);
    }

    /// <summary>
    /// The tips and standings latch on a tap, holding their trigger fully down until tapped again,
    /// and end with the board; a long press is a plain hold.
    /// </summary>
    [TestCase]
    public void TheTipsAndStandingsLatch()
    {
        var pad = new AndroidTouchPad();
        pad.SetLayout(TouchContext.PubGame, AndroidTouchLayout.Build(Phone, 1f, TouchContext.PubGame, pubGame: "magedice"),
            System.Array.Empty<Rect2>());
        pad.Down(0, Centre(pad, "Stats"), 0.0);
        pad.Up(0, Centre(pad, "Stats"), 0.1);
        AssertThat(pad.Sample(1.0).RightTrigger).IsEqual(255u);
        AssertThat(pad.Sample(1.0).LeftTrigger).IsEqual(0u);
        pad.Down(0, Centre(pad, "Stats"), 2.0);
        pad.Up(0, Centre(pad, "Stats"), 2.1);
        AssertThat(pad.Sample(3.0).RightTrigger).IsEqual(0u);

        pad.Down(1, Centre(pad, "Odds"), 4.0);
        AssertThat(pad.Sample(4.5).LeftTrigger).IsEqual(255u);
        pad.Up(1, Centre(pad, "Odds"), 5.0);
        AssertThat(pad.Sample(5.5).LeftTrigger).IsEqual(0u);

        // Latched when the board closes, it lets go: the world has no such control.
        pad.Down(0, Centre(pad, "Odds"), 6.0);
        pad.Up(0, Centre(pad, "Odds"), 6.1);
        pad.SetLayout(TouchContext.World, AndroidTouchLayout.Build(Phone, 1f, TouchContext.World), System.Array.Empty<Rect2>());
        AssertThat(pad.Sample(7.0)).IsEqual(TouchPadOutput.Neutral);
    }

    /// <summary>
    /// A finger on the board presses nothing and is no pointer — the pub games read the mouse's
    /// left button as A — while the diagnostics' rectangles still take theirs.
    /// </summary>
    [TestCase]
    public void ThePubGameBoardTakesNoTaps()
    {
        var diagnostics = new[] { new Rect2(40f, 400f, 240f, 56f) };
        var pad = new AndroidTouchPad();
        pad.SetLayout(TouchContext.PubGame, AndroidTouchLayout.Build(Phone, 1f, TouchContext.PubGame, pubGame: "keystone"), diagnostics);
        var board = new Vector2(780f, 360f);
        pad.Down(0, board, 0.0);
        pad.Move(0, board + new Vector2(40f, 0f), new Vector2(40f, 0f));
        pad.Up(0, board + new Vector2(40f, 0f), 0.1);
        AssertThat(pad.TakePointerEvents().Count).IsEqual(0);
        AssertThat(pad.TakeLook()).IsEqual(Vector2.Zero);
        AssertThat(pad.Sample(0.05)).IsEqual(TouchPadOutput.Neutral);

        pad.Down(1, new Vector2(100f, 420f), 1.0);
        AssertThat(pad.TakePointerEvents().Count).IsEqual(1);
    }

    /// <summary>
    /// The touch controls' hold is the pub game's pad: the same XInput bits, triggers and left
    /// stick (GamePubGames.TouchPad, PubGameInput's Touch device), merged with a controller's as
    /// the guest merges devices.
    /// </summary>
    [TestCase]
    public void TheTouchPadReachesThePubGame()
    {
        GameInput.TouchHold saved = GameInput.Touch;
        try
        {
            GameInput.Touch = new GameInput.TouchHold(GameInput.PadA | GameInput.PadUp | GameInput.PadLeftShoulder, 255u, 0u, 0, 20000);
            PubGameInput.Sample touch = GamePubGames.TouchPad();
            AssertThat(touch.Buttons).IsEqual(GamePad.AButton | GamePad.DpadUp | GamePad.LeftShoulderButton);
            AssertThat(touch.LeftTrigger).IsEqual(255);
            AssertThat(touch.ThumbLY).IsEqual(20000);

            var controller = new PubGameInput.Sample(0, 128, 0, -30000, 9000, 0, GamePad.XButton);
            PubGameInput.Sample merged = PubGameInput.Merge(controller, touch);
            AssertThat(merged.Buttons).IsEqual(GamePad.AButton | GamePad.DpadUp | GamePad.LeftShoulderButton | GamePad.XButton);
            AssertThat(merged.LeftTrigger).IsEqual(255);
            AssertThat(merged.RightTrigger).IsEqual(128);
            AssertThat(merged.ThumbLY).IsEqual(-30000);
            AssertThat(merged.ThumbRX).IsEqual(9000);

            GameInput.Touch = default;
            AssertThat(GamePubGames.TouchPad()).IsEqual(default(PubGameInput.Sample));
        }
        finally
        {
            GameInput.Touch = saved;
        }
    }

    /// <summary>
    /// The Android host takes the title's session nodes (Fable 2's pub games among them), as the
    /// desktop's GameSession does, and steps the title each frame so it reads the guest's table:
    /// without them the guest plays its own movie unseen, a black screen.
    /// </summary>
    [TestCase]
    public void TheAndroidHostPlaysThePubGames()
    {
        string source = HostSource();
        int attach = source.IndexOf("private void AttachFrontEnd()", System.StringComparison.Ordinal);
        int added = source.IndexOf("Engine_TitleGame.Active.AddSessionNodes(", System.StringComparison.Ordinal);
        AssertThat(attach >= 0 && added > attach).IsTrue();
        int follow = source.IndexOf("FollowFrontEnd();", System.StringComparison.Ordinal);
        int poll = source.IndexOf("Engine_TitleGame.Active.Step();", System.StringComparison.Ordinal);
        AssertThat(follow >= 0 && poll > follow && poll < attach).IsTrue();
    }

    private static string HostSource([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(source)!, "../../src/android/AndroidGameHost.cs")));
}
