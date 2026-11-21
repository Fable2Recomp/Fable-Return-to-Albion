using System.Linq;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`tree_billboards` (<see cref="AndroidTreeCards"/>): the cards' band, cells, records, capture rules and levers.</summary>
[TestSuite]
public sealed class TreeCardTests
{
    [TestCase]
    public void CardsFadeInFromThreeQuartersOfFAndOutBeforeTheCull()
    {
        const float f = 64f, cull = 1024f;
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 40f)).IsEqual(0f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 48f)).IsEqual(0f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 52f)).IsEqualApprox(0.5f, 1e-5f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 56f)).IsEqual(1f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 500f)).IsEqual(1f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 896f)).IsEqual(1f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 960f)).IsEqualApprox(0.5f, 1e-5f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 1024f)).IsEqual(0f);
        AssertThat(AndroidTreeCardMaths.CardFade(f, cull, 2000f)).IsEqual(0f);
        AssertThat(AndroidTreeCardMaths.CardFade(0f, cull, 60f)).IsEqual(0f);
    }

    [TestCase]
    public void TreeFarReachTrimsTheCardsOnlyWhenNearer()
    {
        AssertThat(AndroidTreeCardMaths.CardCull(1024f, 0f)).IsEqual(1024f);
        AssertThat(AndroidTreeCardMaths.CardCull(1024f, 200f)).IsEqual(200f);
        AssertThat(AndroidTreeCardMaths.CardCull(150f, 200f)).IsEqual(150f);
        AssertThat(AndroidTreeCardMaths.CardCull(1024f, float.NaN)).IsEqual(1024f);
        AssertThat(AndroidTreeCardMaths.CardCull(1024f, -5f)).IsEqual(1024f);
    }

    [TestCase]
    public void CellsTileTheAtlasEdgeToEdge()
    {
        const int columns = AndroidTreeCardMaths.Columns;
        AssertThat(AndroidTreeCardMaths.Rows(0)).IsEqual(1);
        AssertThat(AndroidTreeCardMaths.Rows(8)).IsEqual(1);
        AssertThat(AndroidTreeCardMaths.Rows(9)).IsEqual(2);

        Rect2 first = AndroidTreeCardMaths.Cell(0, columns, 2);
        AssertThat(first.Position).IsEqual(Vector2.Zero);
        AssertThat(first.Size).IsEqual(new Vector2(1f / 8f, 0.5f));

        // Neighbours share their edges, across and down.
        for (int i = 0; i < 15; i++)
        {
            Rect2 a = AndroidTreeCardMaths.Cell(i, columns, 2), b = AndroidTreeCardMaths.Cell(i + 1, columns, 2);
            if ((i + 1) % columns != 0)
            {
                AssertThat(b.Position.X).IsEqualApprox(a.End.X, 1e-6f);
                AssertThat(b.Position.Y).IsEqual(a.Position.Y);
            }
            else
            {
                AssertThat(b.Position.X).IsEqual(0f);
                AssertThat(b.Position.Y).IsEqualApprox(a.End.Y, 1e-6f);
            }
        }

        // The last cell ends at the atlas's corner.
        Rect2 last = AndroidTreeCardMaths.Cell(15, columns, 2);
        AssertThat(last.End.X).IsEqualApprox(1f, 1e-6f);
        AssertThat(last.End.Y).IsEqualApprox(1f, 1e-6f);
    }

    [TestCase]
    public void CellCentresFollowTheClassicLayout()
    {
        const int columns = AndroidTreeCardMaths.Columns;
        for (int rows = 1; rows <= 3; rows++)
        {
            for (int index = 0; index < rows * columns; index++)
            {
                int col = index % columns, row = index / columns;
                // Engine_ClassicBillboards.Place, in its 2 m cells: (2 col - (C - 1), (R - 1) - 2 row).
                var classic = new Vector2((2f * col) - (columns - 1), (rows - 1) - (2f * row));
                AssertThat(AndroidTreeCardMaths.CellCentre(index, columns, rows) * 2f).IsEqual(classic);
            }
        }

        AssertThat(AndroidTreeCardMaths.CellCentre(0, columns, 1)).IsEqual(new Vector2(-3.5f, 0f));
        AssertThat(AndroidTreeCardMaths.CellCentre(15, columns, 2)).IsEqual(new Vector2(3.5f, -0.5f));
    }

    [TestCase]
    public void ARecordIsTheWorldSphere()
    {
        // File (x, y, z) is Godot model (x, z, -y).
        var sphere = new Vector4(1f, 2f, 3f, 4f);
        AssertThat(AndroidTreeCardMaths.Record(Transform3D.Identity, sphere, out Vector3 centre, out float radius)).IsTrue();
        AssertThat(centre).IsEqual(new Vector3(1f, 3f, -2f));
        AssertThat(radius).IsEqual(4f);

        var placed = new Transform3D(Basis.FromScale(new Vector3(2f, 2f, 2f)), new Vector3(10f, 0f, -5f));
        AssertThat(AndroidTreeCardMaths.Record(placed, sphere, out centre, out radius)).IsTrue();
        AssertThat(centre).IsEqual(new Vector3(12f, 6f, -9f));
        AssertThat(radius).IsEqual(8f);

        // A slot a live entity took over is collapsed to a point: no card.
        var taken = new Transform3D(Basis.FromScale(Vector3.Zero), new Vector3(10f, 0f, -5f));
        AssertThat(AndroidTreeCardMaths.Record(taken, sphere, out _, out _)).IsFalse();
    }

    [TestCase]
    public void TheClockAloneRecapturesOnceAMinuteAtMost()
    {
        Vector3 sun = new Vector3(0f, -1f, -1f).Normalized(), colour = new(2f, 1.8f, 1.5f), ambient = new(0.3f, 0.3f, 0.35f);
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, colour, colour, ambient, ambient, 600.0)).IsNull();

        // The game's clock turns the sun about 2 degrees in 10 s: no capture until a minute has passed.
        Vector3 turned3 = sun.Rotated(Vector3.Right, Mathf.DegToRad(3f));
        AssertThat(AndroidTreeCardMaths.LightReason(sun, turned3, colour, colour, ambient, ambient, 10.0)).IsNull();
        AssertThat(AndroidTreeCardMaths.LightReason(sun, turned3, colour, colour, ambient, ambient, 59.0)).IsNull();
        AssertThat(AndroidTreeCardMaths.LightReason(sun, turned3, colour, colour, ambient, ambient, 60.0)).StartsWith("sun angle 3.0");
        // Under the soft limit nothing is due however long it has been.
        Vector3 turned1 = sun.Rotated(Vector3.Right, Mathf.DegToRad(1f));
        AssertThat(AndroidTreeCardMaths.LightReason(sun, turned1, colour, colour, ambient, ambient, 600.0)).IsNull();
        // In a minute the clock moves the sun about 13 degrees: under the limit that captures at once.
        Vector3 turned13 = sun.Rotated(Vector3.Right, Mathf.DegToRad(13f));
        AssertThat(AndroidTreeCardMaths.LightReason(sun, turned13, colour, colour, ambient, ambient, 59.0)).IsNull();
        // A clock jump (sleep, a script) captures at once.
        Vector3 turned20 = sun.Rotated(Vector3.Right, Mathf.DegToRad(20f));
        AssertThat(AndroidTreeCardMaths.LightReason(sun, turned20, colour, colour, ambient, ambient, 1.0)).StartsWith("sun angle 20");
    }

    [TestCase]
    public void DawnAndDuskRecaptureWithoutWaiting()
    {
        Vector3 sun = Vector3.Down, colour = new(2f, 1.8f, 1.5f), ambient = new(0.3f, 0.3f, 0.35f);
        // 4% of the sun's colour waits for the minute; 8% goes at once.
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, colour, colour * 1.04f, ambient, ambient, 5.0)).IsNull();
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, colour, colour * 1.04f, ambient, ambient, 61.0)).StartsWith("sun colour 4.0%");
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, colour, colour * 0.92f, ambient, ambient, 5.0)).StartsWith("sun colour 8.0%");
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, colour, colour, ambient, ambient * 1.1f, 5.0)).StartsWith("ambient 10.0%");
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, colour, colour, ambient, ambient * 1.04f, 5.0)).IsNull();
        // A sun that appears or goes.
        AssertThat(AndroidTreeCardMaths.LightReason(Vector3.Zero, sun, Vector3.Zero, colour, ambient, ambient, 1.0)).IsEqual("sun on");
        AssertThat(AndroidTreeCardMaths.LightReason(sun, Vector3.Zero, colour, Vector3.Zero, ambient, ambient, 1.0)).IsEqual("sun off");
        // A dark sun's small drift is not a change, even after a minute.
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, new Vector3(0.010f, 0.010f, 0.010f),
            new Vector3(0.012f, 0.012f, 0.012f), ambient, ambient, 120.0)).IsNull();
        // The fable model has no Godot ambient: zero to zero is no change.
        AssertThat(AndroidTreeCardMaths.LightReason(sun, sun, colour, colour, Vector3.Zero, Vector3.Zero, 120.0)).IsNull();
    }

    [TestCase]
    public void TheHeadingWrapsAtNorth()
    {
        AssertThat(AndroidTreeCardMaths.YawBetween(359f, 1f)).IsEqualApprox(2f, 1e-4f);
        AssertThat(AndroidTreeCardMaths.YawBetween(10f, 350f)).IsEqualApprox(20f, 1e-4f);
        AssertThat(AndroidTreeCardMaths.Turned(359f, 1f, 10f)).IsFalse();
        AssertThat(AndroidTreeCardMaths.Turned(355f, 10f, 10f)).IsTrue();
        AssertThat(AndroidTreeCardMaths.Turned(0f, 180f, 10f)).IsTrue();
        // A limit of 0 leaves the heading out.
        AssertThat(AndroidTreeCardMaths.Turned(0f, 180f, 0f)).IsFalse();

        AssertThat(AndroidTreeCardMaths.YawDegrees(new Vector3(0f, 0f, -1f))).IsEqualApprox(0f, 1e-4f);
        AssertThat(AndroidTreeCardMaths.YawDegrees(new Vector3(1f, 0f, 0f))).IsEqualApprox(90f, 1e-4f);
        AssertThat(AndroidTreeCardMaths.YawDegrees(new Vector3(-1f, 0f, 0f))).IsEqualApprox(270f, 1e-4f);
    }

    [TestCase]
    public void ACaptureIsSpreadOverFrames()
    {
        // 0 is every model in one frame, in the atlas's own layout.
        AssertThat(AndroidTreeCardMaths.CaptureFrames(37, 0)).IsEqual(1);
        AssertThat(AndroidTreeCardMaths.BatchGrid(0, 37)).IsEqual((8, 5));
        // Four a frame: a 4 x 1 grid, ten frames for Brightwood's 37 models.
        AssertThat(AndroidTreeCardMaths.CaptureFrames(37, 4)).IsEqual(10);
        AssertThat(AndroidTreeCardMaths.BatchGrid(4, 4)).IsEqual((4, 1));
        AssertThat(AndroidTreeCardMaths.BatchGrid(12, 12)).IsEqual((8, 2));
        AssertThat(AndroidTreeCardMaths.CaptureFrames(0, 4)).IsEqual(0);
        AssertThat(AndroidTreeCardMaths.KindsPerFrame(-3)).IsEqual(0);
        AssertThat(AndroidTreeCardMaths.KindsPerFrame(500)).IsEqual(64);

        // A batch's slot and its atlas cell, in pixels.
        AssertThat(AndroidTreeCardMaths.CellPixel(0)).IsEqual(Vector2I.Zero);
        AssertThat(AndroidTreeCardMaths.CellPixel(9)).IsEqual(new Vector2I(128, 128));
        AssertThat(AndroidTreeCardMaths.CellPixel(3, 4)).IsEqual(new Vector2I(384, 0));
        // A cell's mips stay inside it: its corner and side halve together down to mip 3.
        Vector2I cell = AndroidTreeCardMaths.CellPixel(37);
        for (int mip = 1; mip < AndroidTreeCardMaths.Mips; mip++)
        {
            AssertThat(cell.X % (1 << mip)).IsEqual(0);
            AssertThat(cell.Y % (1 << mip)).IsEqual(0);
            AssertThat(AndroidTreeCardMaths.CellPixels % (1 << mip)).IsEqual(0);
        }
    }

    [TestCase]
    public void CardsTakeOverAtAMultipleOfF()
    {
        // 1 is the stated F; 2 twice it; never past the cull, where the model ended without cards.
        AssertThat(AndroidTreeCardMaths.Start(64f, 1024f, 1f)).IsEqual(64f);
        AssertThat(AndroidTreeCardMaths.Start(64f, 1024f, 2f)).IsEqual(128f);
        AssertThat(AndroidTreeCardMaths.Start(64f, 70f, 2f)).IsEqual(70f);
        AssertThat(AndroidTreeCardMaths.Start(64f, 1024f, 0.5f)).IsEqual(64f);
        AssertThat(AndroidTreeCardMaths.Start(64f, 1024f, float.NaN)).IsEqual(64f);
        AssertThat(AndroidTreeCardMaths.Start(64f, 1024f, 100f)).IsEqual(512f);

        // A card exists only where it ends past the start.
        AssertThat(AndroidTreeCardMaths.CardEnd(64f, 1024f, 0f)).IsEqual(1024f);
        AssertThat(AndroidTreeCardMaths.CardEnd(128f, 1024f, 200f)).IsEqual(200f);
        AssertThat(AndroidTreeCardMaths.CardEnd(70f, 70f, 0f)).IsEqual(0f);
        AssertThat(AndroidTreeCardMaths.CardEnd(128f, 1024f, 100f)).IsEqual(0f);

        // Split screen: the model to its stated cull, as without cards.
        AssertThat(AndroidTreeCardMaths.ModelEnd(64f, 1024f, 2f, true)).IsEqual(128f);
        AssertThat(AndroidTreeCardMaths.ModelEnd(64f, 1024f, 2f, false)).IsEqual(1024f);
        AssertThat(AndroidTreeCardMaths.ModelEnd(64f, 40f, 1f, false)).IsEqual(64f);
    }

    [TestCase]
    public void AModelsEndMovesWithItsFadeAndSpread()
    {
        // A `_LOD1` node handed over at L = 40 m and ending at F = 64 m, its batch spread 10 m.
        float[] band = { 35f, 1f / 5f, 1f, 64f, 1f / 8f, 1f };
        float[] stated = { 25f, 0f, 74f, 0f, 0f };
        (float[] same, float[] sameStated) = AndroidTreeCardMaths.WithEnd(band, stated, 64f);
        AssertThat(same).IsSame(band);
        AssertThat(sameStated).IsSame(stated);

        (float[] moved, float[] movedStated) = AndroidTreeCardMaths.WithEnd(band, stated, 128f);
        AssertThat(moved).ContainsExactly(35f, 1f / 5f, 1f, 128f, 1f / 16f, 1f);
        AssertThat(movedStated).ContainsExactly(25f, 0f, 138f, 0f, 0f);
        // The arrays read from the node are left as they were.
        AssertThat(band[3]).IsEqual(64f);

        // Back again (split screen over): the same band as at first.
        (float[] back, float[] backStated) = AndroidTreeCardMaths.WithEnd(moved, movedStated, 64f);
        AssertThat(back).ContainsExactly(band);
        AssertThat(backStated).ContainsExactly(stated);

        // Never before the fade-in has finished (40 m / 0.875).
        (float[] floor, _) = AndroidTreeCardMaths.WithEnd(band, stated, 30f);
        AssertThat(floor[3]).IsEqualApprox(40f / 0.875f, 1e-3f);
    }

    [TestCase]
    public void TheCaptureWearsTheFarBuildWhereTheTreesDo()
    {
        AssertThat(AndroidTreeCardMaths.CaptureFar(0f, 64f)).IsFalse();
        AssertThat(AndroidTreeCardMaths.CaptureFar(30f, 64f)).IsTrue();
        AssertThat(AndroidTreeCardMaths.CaptureFar(56f, 64f)).IsTrue();
        AssertThat(AndroidTreeCardMaths.CaptureFar(60f, 64f)).IsFalse();
        AssertThat(AndroidTreeCardMaths.CaptureFar(60f, 128f)).IsTrue();
    }

    [TestCase]
    public void TheAtlasMemoryCountsItsMips()
    {
        AssertThat(AndroidTreeCardMaths.AtlasBytes(0)).IsEqual(0L);
        // 1024 x 128 RGBA16F and its three smaller mips.
        long one = 1024L * 128 * 8 + 512L * 64 * 8 + 256L * 32 * 8 + 128L * 16 * 8;
        AssertThat(AndroidTreeCardMaths.AtlasBytes(1)).IsEqual(one);
        AssertThat(AndroidTreeCardMaths.AtlasBytes(8)).IsEqual(one);
        // Sixteen models, two rows: about 2.7 MB.
        AssertThat(AndroidTreeCardMaths.AtlasBytes(16)).IsEqual(2 * one);
    }

    [TestCase]
    public void AModelBandEndingAtFIsLeftByTreeFarReach()
    {
        // A `_LOD1` node handed over at L = 40 m and ending at F = 64 m, its batch spread 10 m.
        float[] band = { 35f, 1f / 5f, 1f, 64f, 1f / 8f, 1f };
        float[] stated = { 25f, 0f, 74f, 0f, 0f };
        (float[] capped, float[] cappedStated) = Engine_TreeReach.CapTreeReach(band, stated, 200f);
        AssertThat(capped).IsSame(band);
        AssertThat(cappedStated).IsSame(stated);
    }

    [TestCase]
    public void TheSettingIsAPhoneRowOffByDefault()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.EffectsAndCanvas.SingleOrDefault(d => d.Name == AndroidTreeCards.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(row.Description)).IsTrue();
        // The phone's name and words show on a phone only (GraphicsPresets.Offered).
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidTreeCards.Key)).IsTrue();
    }

    [TestCase]
    public void TheLeversAreNeutralPhoneRowsThatWaitForTheNextStart()
    {
        foreach ((string key, string neutral) in new[]
        {
            // The pacing defaults: four kinds a frame (2026-11-16), and since 2026-11-17 (preset revision 25) no new
            // heading as the view turns, which swapped every card's picture while panning.
            (AndroidTreeCards.StartKey, "1"), (AndroidTreeCards.PerFrameKey, "4"), (AndroidTreeCards.TurnKey, "0"),
        })
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.EffectsAndCanvas.SingleOrDefault(d => d.Name == key);
            AssertThat(row).IsNotNull();
            AssertThat(row!.DefaultValue).IsEqual(neutral);
            AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(row.Description)).IsTrue();
            AssertThat(AndroidSettingNames.Keys.Contains(key)).IsTrue();
        }

        bool offered = GraphicsPresets.Offered;
        try
        {
            // The phone's words for a setting that waits for the next start.
            GraphicsPresets.Offered = true;
            foreach (string key in new[] { AndroidTreeCards.StartKey, AndroidTreeCards.PerFrameKey, AndroidTreeCards.TurnKey })
            {
                AssertThat(AndroidSettingNames.About(key)).EndsWith(AndroidRestartNotes.SettingNote);
            }
        }
        finally
        {
            GraphicsPresets.Offered = offered;
        }

        // Unset: the rows' defaults, which the code holds too.
        var none = new System.Collections.Generic.Dictionary<string, string>();
        AssertThat(AndroidTreeCards.Levers(none)).IsEqual((0f, 4, 1f));
        AssertThat(AndroidTreeCards.TurnDefault).IsEqual(0f);
        AssertThat(AndroidTreeCards.PerFrameDefault).IsEqual(4);
        // 0 still asks for stage 1's behaviour: one heading, every kind in one frame.
        var zero = new System.Collections.Generic.Dictionary<string, string>
        {
            [AndroidTreeCards.TurnKey] = "0", [AndroidTreeCards.PerFrameKey] = "0",
        };
        AssertThat(AndroidTreeCards.Levers(zero)).IsEqual((0f, 0, 1f));
        var set = new System.Collections.Generic.Dictionary<string, string>
        {
            [AndroidTreeCards.TurnKey] = "2", [AndroidTreeCards.PerFrameKey] = "4", [AndroidTreeCards.StartKey] = "2",
        };
        // A turn limit under 5 degrees is held at 5.
        AssertThat(AndroidTreeCards.Levers(set)).IsEqual((5f, 4, 2f));
    }

    [TestCase]
    public void TheDebugSwitchesAreOffDeveloperRowsInNoPreset()
    {
        string[] keys =
        {
            AndroidTreeCards.HideCardsKey, AndroidTreeCards.OpaqueCutKey, AndroidTreeCards.DrawLastKey,
            AndroidTreeCards.FirstCaptureOnlyKey,
        };
        foreach (string key in keys)
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.EffectsAndCanvas.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key + " has no row").IsNotNull();
            AssertThat(row!.DefaultValue).IsEqual("false");
            AssertThat(row.Type).IsEqual(RuntimeCVarType.Bool);
            AssertThat(row.Category).IsEqual(RuntimeCVarsGodot.DeveloperCategory);
            AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(row.Description)).IsTrue();
            AssertThat(RuntimeCVarsAndroid.AllKeys.Contains(key)).IsTrue();
            foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
            {
                AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(key)).OverrideFailureMessage(key + " in " + preset).IsFalse();
            }
        }

        // Unset: everything off, the cards as they ship.
        var none = new System.Collections.Generic.Dictionary<string, string>();
        AssertThat(AndroidTreeCards.Debug(none)).IsEqual(new AndroidTreeCards.DebugSwitches(false, false, false, false));
        AssertThat(AndroidTreeCards.Debug(none).Any).IsFalse();
        // Each key moves its own switch only.
        var hide = new System.Collections.Generic.Dictionary<string, string> { [AndroidTreeCards.HideCardsKey] = "true" };
        AssertThat(AndroidTreeCards.Debug(hide)).IsEqual(new AndroidTreeCards.DebugSwitches(true, false, false, false));
        var cut = new System.Collections.Generic.Dictionary<string, string> { [AndroidTreeCards.OpaqueCutKey] = "true" };
        AssertThat(AndroidTreeCards.Debug(cut)).IsEqual(new AndroidTreeCards.DebugSwitches(false, true, false, false));
        var last = new System.Collections.Generic.Dictionary<string, string> { [AndroidTreeCards.DrawLastKey] = "true" };
        AssertThat(AndroidTreeCards.Debug(last)).IsEqual(new AndroidTreeCards.DebugSwitches(false, false, true, false));
        var once = new System.Collections.Generic.Dictionary<string, string> { [AndroidTreeCards.FirstCaptureOnlyKey] = "true" };
        AssertThat(AndroidTreeCards.Debug(once)).IsEqual(new AndroidTreeCards.DebugSwitches(false, false, false, true));
        AssertThat(AndroidTreeCards.Debug(once).Any).IsTrue();
        // The levers do not read the debug keys.
        AssertThat(AndroidTreeCards.Levers(hide)).IsEqual(AndroidTreeCards.Levers(none));
    }

    [TestCase]
    public void TheOpaqueCutIsTheSameCutWithoutAlpha()
    {
        // The define follows the fog defines, only when asked.
        AssertThat(AndroidTreeCards.CardDefines(AndroidLeanShading.FogPath.Lean, false))
            .ContainsExactly("FABLE_LITE", AndroidLeanShading.FogLeanDefine);
        AssertThat(AndroidTreeCards.CardDefines(AndroidLeanShading.FogPath.Lean, true))
            .ContainsExactly("FABLE_LITE", AndroidLeanShading.FogLeanDefine, AndroidTreeCards.OpaqueCutDefine);
        AssertThat(AndroidTreeCards.CardDefines(AndroidLeanShading.FogPath.Full, true))
            .ContainsExactly("FABLE_LITE", AndroidTreeCards.OpaqueCutDefine);

        // In the shader, the define's branch discards below the scissor's 0.5 and writes no ALPHA (Godot
        // blends any material that writes ALPHA); the other branch is the shipped scissor.
        string code = System.IO.File.ReadAllText(System.IO.Path.Combine(GodotRoot(), "src/android/tree_cards.gdshader"));
        // The scissor first, so the cut's discard is the shader's tail (shader-dxil-safety.md).
        int ifndef = code.IndexOf("#ifndef " + AndroidTreeCards.OpaqueCutDefine, System.StringComparison.Ordinal);
        int otherwise = code.IndexOf("#else", ifndef, System.StringComparison.Ordinal);
        int end = code.IndexOf("#endif", otherwise, System.StringComparison.Ordinal);
        AssertThat(ifndef).IsGreater(0);
        string scissorBranch = code[ifndef..otherwise];
        string cutBranch = code[otherwise..end];
        AssertThat(cutBranch).Contains("if (picture.a * v_fade < 0.5)");
        AssertThat(cutBranch).Contains("discard;");
        AssertThat(System.Text.RegularExpressions.Regex.IsMatch(cutBranch, @"^\s*ALPHA\b", System.Text.RegularExpressions.RegexOptions.Multiline)).IsFalse();
        AssertThat(scissorBranch).Contains("ALPHA = picture.a * v_fade;");
        AssertThat(scissorBranch).Contains("ALPHA_SCISSOR_THRESHOLD = 0.5;");
        // Nothing else in the shader writes ALPHA.
        AssertThat(System.Text.RegularExpressions.Regex.Matches(code, @"^\s*ALPHA\s*=", System.Text.RegularExpressions.RegexOptions.Multiline).Count).IsEqual(1);
    }

    [TestCase]
    public void DrawLastLandsInTheLastBandAndLayer()
    {
        // godot-patches/0012: depth = distance - sorting offset; band = clamp(int(log2(depth / 2) * 2) + 1, 1, 15),
        // stock layer = clamp(int(depth * 16 / z range), 0, 15). From the camera's own spot, with a 10 km range.
        float depth = 0f - AndroidTreeCards.DrawLastOffset;
        AssertThat(System.Math.Clamp((int)(System.Math.Log2(depth * 0.5) * 2.0) + 1, 1, 15)).IsEqual(15);
        AssertThat(System.Math.Clamp((int)(depth * 16f / 10000f), 0, 15)).IsEqual(15);
    }

    private static string GodotRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../.."));

    [TestCase]
    [RequireGodotRuntime]
    public void OffAwayFromAPhoneAndFoggedAsTheLitePropsAre()
    {
        AssertThat(AndroidTreeCards.On).IsFalse();
        AssertThat(AndroidTreeCards.WarmCards).IsNull();
        AssertThat(AndroidTreeCards.FirstCaptureWaiting).IsFalse();
        AssertThat(AndroidTreeCards.FogDefines(AndroidLeanShading.FogPath.Full)).ContainsExactly("FABLE_LITE");
        AssertThat(AndroidTreeCards.FogDefines(AndroidLeanShading.FogPath.Lean))
            .ContainsExactly("FABLE_LITE", AndroidLeanShading.FogLeanDefine);
        AssertThat(AndroidTreeCards.FogDefines(AndroidLeanShading.FogPath.Table))
            .ContainsExactly("FABLE_LITE", AndroidLeanShading.FogLeanDefine, AndroidLeanShading.FogTableDefine);
    }

    /// <summary>With turn recaptures off every pass in an area faces its first pass's way (2026-11-17: light passes
    /// faced the view and swapped the pictures while the player panned); with them on a pass faces the view.</summary>
    [TestCase]
    public void TestCapturesKeepTheAreasHeadingWithTurnRecapturesOff()
    {
        var first = new Vector3(0f, 0f, -1f);
        var later = new Vector3(1f, 0f, 0f);
        (Vector3 h1, Vector3? area) = AndroidTreeCardMaths.CaptureHeading(0f, null, first);
        AssertThat(h1).IsEqual(first);
        (Vector3 h2, Vector3? area2) = AndroidTreeCardMaths.CaptureHeading(0f, area, later);
        AssertThat(h2).IsEqual(first);
        AssertThat(area2).IsEqual(first);
        (Vector3 h3, _) = AndroidTreeCardMaths.CaptureHeading(10f, area, later);
        AssertThat(h3).IsEqual(later);
    }
}
