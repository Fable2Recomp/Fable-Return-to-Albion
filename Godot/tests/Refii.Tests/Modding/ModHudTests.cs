namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Fx;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The HUD half of the mod API (Part H.2, H.4, H.5): a play at a model helper, the world markers,
/// the per-player fade and the meters — the bus requests they cross as, and the pure rules each
/// surface shares. No Godot runtime object is made here (Godot structs only).
/// </summary>
[TestSuite]
public class ModHudTests
{
    // ---- H.5b: meters in the game's own meter art --------------------------------------------

    [TestCase]
    public void CopyStylesCloneTheGamesMeterArtOnTheirOwnFaderSlide()
    {
        // The three copy styles name the pieces hud.bgf binds (read in the HUD meter lab,
        // 2026-10-22) and the fader's raise; the primitives stay primitives.
        foreach ((string name, string of, string meter) in new[]
                 {
                     ("generic", "slidefader2.GenericMeter.GenericMeter", "GenericMeter"),
                     ("crucible", "slidefader2.CrucibleMeter.CrucibleMeter", "CrucibleMeter"),
                     ("price", "slidefader2.PriceWidget.PriceWidget", ""),
                 })
        {
            Engine_HudModMeters.Style style = Engine_HudModMeters.StyleNamed(name);
            AssertThat(style.Name).IsEqual(name);
            AssertThat(style.IsCopy).IsTrue();
            AssertThat(style.CloneOf).IsEqual(of);
            AssertThat(style.MeterName).IsEqual(meter);
            AssertThat(style.RaiseSlide).IsEqual(Engine_HudModMeters.FaderRaise);
        }

        AssertThat(Engine_HudModMeters.StyleNamed("price").ValueText).IsTrue();
        foreach (string primitive in new[] { "bar", "boss", "timer" })
        {
            AssertThat(Engine_HudModMeters.StyleNamed(primitive).IsCopy).IsFalse();
        }

        Engine_HudModMeters.Clear();
        AssertThat(Engine_HudModMeters.Set("t.generic", 0, "Gen", 60f, 100f, "generic")).IsTrue();
        AssertThat(Engine_HudModMeters.Set("t.price", 0, "Cost", 1250f, 2000f, "price")).IsTrue();
        AssertThat(Engine_HudModMeters.Set("t.bar", 0, "Bar", 50f, 100f, "bar")).IsTrue();

        // The copies: one clone record per copy-style meter, cut from the fader piece, its bar
        // remapped to the meter's value, its own element on the raise slide — never the original's.
        List<Engine_HudModMeters.Copy> copies = Engine_HudModMeters.CopiesFor(1, false);
        AssertThat(copies.Count).IsEqual(2);
        Engine_HudModMeters.Copy generic = copies[0];
        AssertThat(generic.Record.Id).IsEqual("group.GenericMeter.meter.t.generic");
        AssertThat(generic.Record.Of).IsEqual("slidefader2.GenericMeter.GenericMeter");
        AssertThat(generic.Record.Values["GenericMeter"]).IsEqual("mod:meter.t.generic.value/mod:meter.t.generic.max");
        AssertThat(generic.Record.Slides!["GenericMeter"]).IsEqual(2);
        AssertThat(generic.Record.Slides!.Count).IsEqual(1);
        AssertThat(copies[1].Record.Of).IsEqual("slidefader2.PriceWidget.PriceWidget");
        AssertThat(copies[1].Record.Values.Count).IsEqual(0);
        AssertThat(copies[1].Offset.Y > generic.Offset.Y).IsTrue();

        // The primitives beside them: a label per meter, a bar only for the primitive style, the
        // price's number as a text piece reading the meter's value.
        List<Engine_HudModPieces.Piece> pieces = Engine_HudModMeters.PiecesFor(1, false);
        AssertThat(pieces.Exists(p => p.Id == "meter.t.generic.label")).IsTrue();
        AssertThat(pieces.Exists(p => p.Id == "meter.t.generic")).IsFalse();
        AssertThat(pieces.Exists(p => p.Id == "meter.t.bar" && p.Kind == Engine_HudModPieces.Kind.Meter)).IsTrue();
        Engine_HudModPieces.Piece price = pieces.Find(p => p.Id == "meter.t.price.value")!;
        AssertThat(price.Kind).IsEqual(Engine_HudModPieces.Kind.Text);
        AssertThat(price.Text).IsEqual("mod:meter.t.price.value");
        AssertThat(price.Format).IsEqual("0");
        AssertThat(Engine_HudModMeters.Resolve("mod:meter.t.price.value")).IsEqual(1250f);

        // A view shows the copies shown to its player: player two's meter joins player two's
        // view beside the every-player ones and stays out of player one's.
        AssertThat(Engine_HudModMeters.Set("t.two", 2, "Two", 1f, 2f, "crucible")).IsTrue();
        AssertThat(Engine_HudModMeters.CopiesFor(1, false).Count).IsEqual(2);
        AssertThat(Engine_HudModMeters.CopiesFor(2, false).Count).IsEqual(3);
        AssertThat(Engine_HudModMeters.CopiesFor(2, false).Exists(c => c.Meter.Id == "t.two")).IsTrue();
        AssertThat(Engine_HudModMeters.CopiesFor(1, false).Exists(c => c.Meter.Id == "t.two")).IsFalse();
        Engine_HudModMeters.Clear();
    }

    // ---- H.11: a mod-placed light ------------------------------------------------------------

    [TestCase]
    public void LightRequestCrossesTheEffectKind()
    {
        // The native surface's line, as refii_mod_light posts it: the entity a handle, the helper a name.
        string fields = "op='light',id=5,entity=12,x=0,y=0,z=0,r=1,g=0.5,b=0.25,energy=2,range=4,ox=0,oy=0.1,oz=0,at='Weapon.Weapon.Top',flicker=0.3";
        AssertThat(Refii.Lighting.Engine_ModLights.IsLightRequest(fields)).IsTrue();
        Refii.Lighting.Engine_ModLights.Request? light = Refii.Lighting.Engine_ModLights.ParseRequest(fields);
        AssertThat(light).IsNotNull();
        AssertThat(light!.Value.IsPlace).IsTrue();
        AssertThat(light.Value.Entity).IsEqual(12u);
        AssertThat(light.Value.At).IsEqual("Weapon.Weapon.Top");
        AssertThat(light.Value.G).IsEqual(0.5f);
        AssertThat(light.Value.Energy).IsEqual(2f);
        AssertThat(light.Value.Range).IsEqual(4f);
        AssertThat(light.Value.OffsetY).IsEqual(0.1f);
        AssertThat(light.Value.Flicker).IsEqual(0.3f);

        // A world point with the defaults: white, energy 1, the default range.
        Refii.Lighting.Engine_ModLights.Request? point = Refii.Lighting.Engine_ModLights.ParseRequest("op='light',id=6,entity=0,x=1,y=2,z=3");
        AssertThat(point!.Value.Entity).IsEqual(0u);
        AssertThat(point.Value.Z).IsEqual(3f);
        AssertThat(point.Value.R).IsEqual(1f);
        AssertThat(point.Value.Range).IsEqual(Refii.Lighting.Engine_ModLights.DefaultRange);

        // Removal, and what is not a light at all.
        AssertThat(Refii.Lighting.Engine_ModLights.ParseRequest("op='light_off',id=5")!.Value.IsPlace).IsFalse();
        AssertThat(Refii.Lighting.Engine_ModLights.ParseRequest("op='light',id=0")).IsNull();
        AssertThat(Refii.Lighting.Engine_ModLights.IsLightRequest("op='play',id=1,name='x'")).IsFalse();
        AssertThat(Refii.Fx.Engine_FxPlayer.ParseRequest("op='light',id=5")).IsNull();
    }

    [TestCase]
    public void LightAxesRoundTripBetweenGodotAndTheGuest()
    {
        // The classic list takes the node's point in the guest's axes: Godot (x, y, z) is guest (-x, z, y).
        var guest = new Vector3(3f, 4f, 5f);
        Vector3 godot = Refii.Game.GameFx.GuestToGodot(guest);
        AssertThat(godot.IsEqualApprox(new Vector3(-3f, 5f, 4f))).IsTrue();
        AssertThat(Refii.Lighting.Engine_ModLights.GodotToGuest(godot).IsEqualApprox(guest)).IsTrue();
        AssertThat(Refii.Lighting.Engine_ModLights.FirstClassicIndex).IsEqual(128);
    }

    // ---- H.2: play_fx_on at a helper ---------------------------------------------------------

    [TestCase]
    public void PlayRequestCarriesTheHelper()
    {
        Engine_FxPlayer.Request? at = Engine_FxPlayer.ParseRequest(
            "op='play',id=7,name='fxspl_damocles_trail',x=0,y=0,z=0,entity=12,duration=0,scale=1,r=1,g=1,b=1,intensity=1,loop=1,ox=0,oy=0.1,oz=0,at='Weapon.Weapon.Top'");
        AssertThat(at).IsNotNull();
        AssertThat(at!.Value.At).IsEqual("Weapon.Weapon.Top");
        AssertThat(at.Value.Entity).IsEqual(12u);

        // A request without the field (an older payload) is the entity's own frame.
        Engine_FxPlayer.Request? plain = Engine_FxPlayer.ParseRequest(
            "op='play',id=8,name='fxspl_damocles_trail',x=0,y=0,z=0,entity=12,duration=0,scale=1,r=1,g=1,b=1,intensity=1,loop=1,ox=0,oy=0,oz=0");
        AssertThat(plain!.Value.At).IsEqual(string.Empty);

        // The classic command never carries the name: the helper's rest point is folded into the offset first.
        AssertThat(Engine_FxPlayer.GuestPlayCommand(at.Value, 0x1234u).Contains("Weapon")).IsFalse();
    }

    [TestCase]
    public void HelperFrameIsTheEntityFrameMovedToTheHelper()
    {
        // A helper 0.847 along the model's -Z (the Damocles blade's .Top in Godot Y-up space, the
        // guest's +Y) with no rotation: the effect stands there, its axes the entity's.
        var mount = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -0.847f));
        Transform3D local = Engine_FxPlayer.AtHelperLocal(mount, Vector3.Zero);
        AssertThat(local.Origin.IsEqualApprox(new Vector3(0f, 0f, -0.847f))).IsTrue();
        AssertThat(local.Basis.IsEqualApprox(Engine_FxPlayer.InEntityLocal(Vector3.Zero).Basis)).IsTrue();

        // An offset is along the helper's guest axes: guest +y (along the blade) is the node's -z.
        Transform3D lifted = Engine_FxPlayer.AtHelperLocal(mount, new Vector3(0f, 0.1f, 0f));
        AssertThat(lifted.Origin.IsEqualApprox(new Vector3(0f, 0f, -0.947f))).IsTrue();

        // A turned helper turns the effect with it: a quarter turn about the model's up.
        var turned = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), new Vector3(0.2f, 0f, 0f));
        Transform3D on = Engine_FxPlayer.AtHelperLocal(turned, Vector3.Zero);
        AssertThat(on.Basis.IsEqualApprox(turned.Basis * Engine_FxPlayer.InEntityLocal(Vector3.Zero).Basis)).IsTrue();
    }

    [TestCase]
    public void ClassicOffsetIsTheHelpersRestPointInGuestAxes()
    {
        // The same .Top: the guest's model axes put the blade along +Y, so fx_play's offset is (0, 0.847, 0).
        var rest = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -0.847f));
        Vector3 guest = Engine_FxPlayer.ClassicHelperOffset(rest, Vector3.Zero);
        AssertThat(guest.IsEqualApprox(new Vector3(0f, 0.847f, 0f))).IsTrue();

        // An offset along the helper's own axes: +y along the blade again.
        Vector3 past = Engine_FxPlayer.ClassicHelperOffset(rest, new Vector3(0f, 0.1f, 0f));
        AssertThat(past.IsEqualApprox(new Vector3(0f, 0.947f, 0f))).IsTrue();
    }

    // ---- H.4: markers ------------------------------------------------------------------------

    [TestCase]
    public void MarkRequestsCross()
    {
        Engine_UiModRequest.Request? mark = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(
            "op='mark',id=3,player=2,entity=44,x=1.5,y=-2,z=0.25,icon='art/gui/icons/quest.tex',text='Here'"));
        AssertThat(mark).IsNotNull();
        AssertThat(mark!.Value.Op).IsEqual(Engine_UiModRequest.Op.Mark);
        AssertThat(mark.Value.Id).IsEqual(3u);
        AssertThat(mark.Value.Player).IsEqual(2);
        AssertThat(mark.Value.Entity).IsEqual(44u);
        AssertThat(mark.Value.Icon).IsEqual("art/gui/icons/quest.tex");
        AssertThat(mark.Value.Text).IsEqual("Here");
        AssertThat(mark.Value.X).IsEqual(1.5f);

        Engine_UiModRequest.Request? point = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(
            "op='mark',id=4,player=0,entity=0,x=10,y=20,z=30,icon='',text='A point'"));
        AssertThat(point!.Value.Entity).IsEqual(0u);
        AssertThat(point.Value.Z).IsEqual(30f);

        // No icon and no text is nothing to draw; no id is not a request.
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='mark',id=5,player=0,entity=0,x=0,y=0,z=0,icon='',text=''"))).IsNull();
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='mark',id=0,player=0,entity=0,x=0,y=0,z=0,icon='x',text=''"))).IsNull();

        Engine_UiModRequest.Request? unmark = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='unmark',id=3"));
        AssertThat(unmark!.Value.Op).IsEqual(Engine_UiModRequest.Op.Unmark);
        AssertThat(unmark.Value.Id).IsEqual(3u);
    }

    [TestCase]
    public void MarkerSizeIsTheGuestsDistanceCurve()
    {
        // sub_821AA758: 1 - d²/400 under 100, 0.75 - 0.00075 (d² - 100) to 300, 0.6 beyond — continuous.
        AssertThat(Engine_HudModMarkers.DistanceSize(0f)).IsEqual(1f);
        AssertThat(Math.Abs(Engine_HudModMarkers.DistanceSize(100f) - 0.75f) < 1e-5f).IsTrue();
        AssertThat(Math.Abs(Engine_HudModMarkers.DistanceSize(99.999f) - 0.75f) < 1e-3f).IsTrue();
        AssertThat(Math.Abs(Engine_HudModMarkers.DistanceSize(300f) - 0.6f) < 1e-5f).IsTrue();
        AssertThat(Math.Abs(Engine_HudModMarkers.DistanceSize(299.9f) - 0.6f) < 1e-3f).IsTrue();
        AssertThat(Engine_HudModMarkers.DistanceSize(10000f)).IsEqual(0.6f);
        AssertThat(Engine_HudModMarkers.DistanceSize(float.NaN)).IsEqual(1f);
    }

    [TestCase]
    public void MarkersAndMetersAreShownToTheirPlayer()
    {
        // 0 is every player; a player's own view; the other local player on a shared couch screen only.
        AssertThat(Engine_HudModMarkers.ShownTo(0, 1, false)).IsTrue();
        AssertThat(Engine_HudModMarkers.ShownTo(0, 2, false)).IsTrue();
        AssertThat(Engine_HudModMarkers.ShownTo(1, 1, false)).IsTrue();
        AssertThat(Engine_HudModMarkers.ShownTo(2, 1, false)).IsFalse();
        AssertThat(Engine_HudModMarkers.ShownTo(1, 2, false)).IsFalse();
        AssertThat(Engine_HudModMarkers.ShownTo(2, 1, true)).IsTrue();
        AssertThat(Engine_HudModMarkers.ShownTo(1, 2, true)).IsTrue();

        // The sprite key stands outside every guest address the layer keys on.
        AssertThat(Engine_HudModMarkers.SpriteKey(1) >= 0xE0000000u).IsTrue();
        AssertThat(Engine_HudModMarkers.SpriteKey(Engine_HudModMarkers.FirstHostId) != Engine_HudModMarkers.SpriteKey(1)).IsTrue();
    }

    [TestCase]
    public void TheGamesOwnWorldIconsKeepTheirPerPlayerRule()
    {
        // H.16: the game's icons go through the markers' composition; the per-player rule is the
        // one GameUi composed inline before (pinned here, truth table): view 1 takes player one's
        // always, player two's only on one unsplit screen that is not an online host; view 2 (split
        // only) takes player two's and every non-opinion icon.
        foreach (bool opinion in new[] { false, true })
        {
            AssertThat(Engine_HudModMarkers.GameIconShownTo(false, opinion, 1, false, false)).IsTrue();
            AssertThat(Engine_HudModMarkers.GameIconShownTo(false, opinion, 1, true, false)).IsTrue();
            AssertThat(Engine_HudModMarkers.GameIconShownTo(false, opinion, 1, false, true)).IsTrue();
            AssertThat(Engine_HudModMarkers.GameIconShownTo(true, opinion, 1, false, false)).IsTrue();   // couch, or the online joiner
            AssertThat(Engine_HudModMarkers.GameIconShownTo(true, opinion, 1, true, false)).IsFalse();   // split: theirs is in their half
            AssertThat(Engine_HudModMarkers.GameIconShownTo(true, opinion, 1, false, true)).IsFalse();   // the online host: the joiner draws it
            AssertThat(Engine_HudModMarkers.GameIconShownTo(true, opinion, 2, true, false)).IsTrue();
            AssertThat(Engine_HudModMarkers.GameIconShownTo(true, opinion, 2, false, false)).IsFalse();  // view 2 exists only split
        }

        AssertThat(Engine_HudModMarkers.GameIconShownTo(false, false, 2, true, false)).IsTrue();   // a sign in both halves
        AssertThat(Engine_HudModMarkers.GameIconShownTo(false, true, 2, true, false)).IsFalse();   // player one's heart stays in their half

        // The opinion kinds and the heart badge, as before: 1..20 and 22..26 are opinions, the
        // hearts 22..26 carry the player's number on one shared couch screen only.
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(0)).IsFalse();
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(1)).IsTrue();
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(20)).IsTrue();
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(21)).IsFalse();
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(22)).IsTrue();
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(26)).IsTrue();
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(27)).IsFalse();
        AssertThat(Engine_HudModMarkers.IsOpinionIcon(Engine_HudModMarkers.SpriteType)).IsFalse();
        AssertThat(Engine_HudModMarkers.GameIconBadge(24, false, false, true)).IsEqual("1");
        AssertThat(Engine_HudModMarkers.GameIconBadge(24, true, false, true)).IsEqual("2");
        AssertThat(Engine_HudModMarkers.GameIconBadge(24, true, true, true)).IsEqual("");
        AssertThat(Engine_HudModMarkers.GameIconBadge(24, true, false, false)).IsEqual("");
        AssertThat(Engine_HudModMarkers.GameIconBadge(5, true, false, true)).IsEqual("");
    }

    // ---- H.5: fade ---------------------------------------------------------------------------

    [TestCase]
    public void FadeRampsPerPlayer()
    {
        Engine_UiModFade.Clear();
        AssertThat(Engine_UiModFade.Request(1, true, 2f)).IsTrue();
        AssertThat(Engine_UiModFade.AlphaFor(1)).IsEqual(0f);
        AssertThat(Engine_UiModFade.AlphaFor(2)).IsEqual(0f);
        Engine_UiModFade.Advance(1.0);
        AssertThat(Math.Abs(Engine_UiModFade.AlphaFor(1) - 0.5f) < 1e-4f).IsTrue();
        AssertThat(Engine_UiModFade.AlphaFor(2)).IsEqual(0f);
        Engine_UiModFade.Advance(5.0);
        AssertThat(Engine_UiModFade.AlphaFor(1)).IsEqual(1f);
        AssertThat(Engine_UiModFade.Covers).IsTrue();

        // Back from black, from the alpha standing; 0 seconds snaps; player 0 is both.
        Engine_UiModFade.Request(1, false, 1f);
        Engine_UiModFade.Advance(0.25);
        AssertThat(Math.Abs(Engine_UiModFade.AlphaFor(1) - 0.75f) < 1e-4f).IsTrue();
        Engine_UiModFade.Request(0, true, 0f);
        AssertThat(Engine_UiModFade.AlphaFor(1)).IsEqual(1f);
        AssertThat(Engine_UiModFade.AlphaFor(2)).IsEqual(1f);
        (float alpha, float target, float seconds) = Engine_UiModFade.Describe(2);
        AssertThat(alpha).IsEqual(1f);
        AssertThat(target).IsEqual(1f);
        AssertThat(seconds).IsEqual(0f);
        AssertThat(Engine_UiModFade.Request(3, true, 1f)).IsFalse();
        Engine_UiModFade.Clear();
        AssertThat(Engine_UiModFade.Covers).IsFalse();

        AssertThat(Engine_UiModFade.ToBlack("to_black")).IsEqual(true);
        AssertThat(Engine_UiModFade.ToBlack("in")).IsEqual(false);
        AssertThat(Engine_UiModFade.ToBlack("sideways")).IsNull();

        Engine_UiModRequest.Request? fade = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='fade',player=2,to=1,seconds=1.5"));
        AssertThat(fade!.Value.Op).IsEqual(Engine_UiModRequest.Op.Fade);
        AssertThat(fade.Value.Player).IsEqual(2);
        AssertThat(fade.Value.ToBlack).IsTrue();
        AssertThat(fade.Value.Seconds).IsEqual(1.5f);
    }

    // ---- H.5: meters -------------------------------------------------------------------------

    [TestCase]
    public void MetersStackPerPlayerAndReadLive()
    {
        Engine_HudModMeters.Clear();
        int generation = Engine_HudModMeters.Generation;
        AssertThat(Engine_HudModMeters.Set("boss.hp", 0, "Thag", 300f, 400f, "boss")).IsTrue();
        AssertThat(Engine_HudModMeters.Set("p2.timer", 2, "Time", 30f, 60f, "timer")).IsTrue();
        AssertThat(Engine_HudModMeters.Set("", 0, "x", 1f, 1f, "")).IsFalse();
        AssertThat(Engine_HudModMeters.Generation).IsEqual(generation + 2);

        // A value restated in place does not rebuild; a label does.
        Engine_HudModMeters.Set("boss.hp", 0, "Thag", 250f, 400f, "boss");
        AssertThat(Engine_HudModMeters.Generation).IsEqual(generation + 2);
        AssertThat(Engine_HudModMeters.Resolve("mod:meter.boss.hp.value")).IsEqual(250f);
        AssertThat(Engine_HudModMeters.Resolve("mod:meter.boss.hp.max")).IsEqual(400f);
        AssertThat(Engine_HudModMeters.Resolve("mod:meter.none.value")).IsNull();
        AssertThat(Engine_HudModMeters.Resolve("game:PlayerHealth")).IsNull();
        Engine_HudModMeters.Set("boss.hp", 0, "Thag the Great", 250f, 400f, "boss");
        AssertThat(Engine_HudModMeters.Generation).IsEqual(generation + 3);

        // Player one's view: the boss bar only; player two's: both; a shared screen: both.
        List<Engine_HudModPieces.Piece> one = Engine_HudModMeters.PiecesFor(1, false);
        AssertThat(one.Select(p => p.Id).ToArray()).IsEqual(new[] { "meter.boss.hp.label", "meter.boss.hp" });
        List<Engine_HudModPieces.Piece> two = Engine_HudModMeters.PiecesFor(2, false);
        AssertThat(two.Select(p => p.Id).ToArray()).IsEqual(new[] { "meter.boss.hp.label", "meter.boss.hp", "meter.p2.timer.label", "meter.p2.timer" });
        AssertThat(Engine_HudModMeters.PiecesFor(1, true).Count).IsEqual(4);

        // The bar reads the live sources, hangs under its label, and the stack grows downward.
        Engine_HudModPieces.Piece bar = one[1];
        AssertThat(bar.Kind).IsEqual(Engine_HudModPieces.Kind.Meter);
        AssertThat(bar.Value).IsEqual("mod:meter.boss.hp.value");
        AssertThat(bar.Max).IsEqual("mod:meter.boss.hp.max");
        AssertThat(bar.Anchor).IsEqual(Engine_HudLayout.Anchor.TopCentre);
        AssertThat(bar.Offset.Y > one[0].Offset.Y).IsTrue();
        AssertThat(two[2].Offset.Y > bar.Offset.Y).IsTrue();
        AssertThat(one[0].Kind).IsEqual(Engine_HudModPieces.Kind.Text);
        AssertThat(one[0].Text).IsEqual("Thag the Great");
        AssertThat(Engine_HudModMeters.StyleNamed("BOSS").Name).IsEqual("boss");
        AssertThat(Engine_HudModMeters.StyleNamed("nothing").Name).IsEqual("bar");

        AssertThat(Engine_HudModMeters.Remove("p2.timer")).IsTrue();
        AssertThat(Engine_HudModMeters.Remove("p2.timer")).IsFalse();
        AssertThat(Engine_HudModMeters.Count).IsEqual(1);
        Engine_HudModMeters.Clear();

        Engine_UiModRequest.Request? meter = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain(
            "op='meter',player=1,id='my.timer',label='Time left',value=12.5,max=60,style='timer'"));
        AssertThat(meter!.Value.Op).IsEqual(Engine_UiModRequest.Op.Meter);
        AssertThat(meter.Value.Name).IsEqual("my.timer");
        AssertThat(meter.Value.Title).IsEqual("Time left");
        AssertThat(meter.Value.Value).IsEqual(12.5f);
        AssertThat(meter.Value.Max).IsEqual(60f);
        AssertThat(meter.Value.Style).IsEqual("timer");
        Engine_UiModRequest.Request? remove = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='meter_remove',id='my.timer'"));
        AssertThat(remove!.Value.Op).IsEqual(Engine_UiModRequest.Op.MeterRemove);
        AssertThat(remove.Value.Name).IsEqual("my.timer");
    }

    // ---- H.4b: the game's own world-icon names ------------------------------------------------

    [TestCase]
    public void MarkerIconAliasesAreTheGuestsWorldIconTextures()
    {
        // sub_82820640 (TU1) names each world-icon kind's texture and builds "art\gui\" + name +
        // ".tex": the aliases are those names, and anything else is a path as given.
        AssertThat(Engine_HudModMarkers.IconPath("quest")).IsEqual("art/gui/hud/inworld_reward_icon_quest.tex");
        AssertThat(Engine_HudModMarkers.IconPath("Quest")).IsEqual("art/gui/hud/inworld_reward_icon_quest.tex");
        AssertThat(Engine_HudModMarkers.IconPath("follow")).IsEqual("art/gui/wheelicons/npcstatus/icon_npc_follow.tex");
        AssertThat(Engine_HudModMarkers.IconPath("gold")).IsEqual("art/gui/wheelicons/icon_gold_coin.tex");
        AssertThat(Engine_HudModMarkers.IconPath("talk")).IsEqual("art/gui/multiplayer/orb_icon_talk.tex");
        AssertThat(Engine_HudModMarkers.IconPath("opinion_love_gold")).IsEqual("art/gui/wheelicons/splashopinions/opinion_love_gold.tex");
        AssertThat(Engine_HudModMarkers.IconPath("morph_evil")).IsEqual("art/gui/wheelicons/splashopinions/morph_evil.tex");
        AssertThat(Engine_HudModMarkers.IconPath("lock")).IsEqual("art/gui/icon_lock.tex");
        AssertThat(Engine_HudModMarkers.IconPath("art/my_mod/flag.png")).IsEqual("art/my_mod/flag.png");
        AssertThat(Engine_HudModMarkers.IconPath("")).IsEqual("");
        AssertThat(Engine_HudModMarkers.IconAliases.Count).IsEqual(52);
        foreach (KeyValuePair<string, string> alias in Engine_HudModMarkers.IconAliases)
        {
            AssertThat(alias.Value.StartsWith("art/gui/", StringComparison.Ordinal)).OverrideFailureMessage(alias.Key).IsTrue();
            AssertThat(alias.Value.EndsWith(".tex", StringComparison.Ordinal)).OverrideFailureMessage(alias.Key).IsTrue();
        }
    }
}
