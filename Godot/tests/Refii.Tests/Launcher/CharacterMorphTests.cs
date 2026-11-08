using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using GdUnit4;
using Refii.Core;
using Refii.Bnk;
using Refii.Gdb;
using Refii.Hero;
using Refii.Host;
using Refii.Launcher.Core;
using Refii.Models;
using Refii.Save;
using Refii.Textures;
using Refii.Ui;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

namespace Refii.Tests.Launcher;

[TestSuite]
public class CharacterMorphTests
{
    [TestCase]
    public void TanUsesTheOriginalMaleAndFemaleSkinTexturesAndKeepsOtherLayers()
    {
        foreach (bool female in new[] { false, true })
        foreach (string part in new[] { "face", "torso", "legs" })
        {
            string root = "Art/Characters/Heros/Unclothed " + (female ? "Female" : "Male") + "/Pictures/_SharedTextures/";
            string prefix = (female ? "fhero_" : "mhero_") + part;
            string original = root + prefix + (female ? "_02.tex" : "_01.tex");
            string pale = root + prefix + "_pale_01.tex";
            string tanned = root + prefix + "_tanned_01.tex";
            Engine_TexPathIndex.SetPathSource(() => new[] { original, pale, tanned });
            try
            {
                var source = new[]
                {
                    new HostBridge.GuestTextureLayer(Engine_TexPathIndex.PathHash(original), 0, 1, false, 0, 0),
                    new HostBridge.GuestTextureLayer(42, 1, 0.3f, true, 0x12345678, 0.2f)
                };
                foreach (var (tan, texture, weight) in new[]
                {
                    (0f, pale, 1f), (0.25f, pale, 0.5f), (0.75f, tanned, 0.5f), (1f, tanned, 1f)
                })
                {
                    var layers = Engine_HeroTextureMorph.SkinLayers(original.Replace('/', '\\'), source, tan)!;
                    AssertThat(layers.Length).IsEqual(3);
                    AssertThat(layers[0]).IsEqual(source[0]);
                    AssertThat(layers[1].Texture).IsEqual(Engine_TexPathIndex.PathHash(texture));
                    AssertThat(layers[1].Weight).IsEqual(weight);
                    AssertThat(layers[1].Mode).IsEqual(1);
                    AssertThat(layers[2]).IsEqual(source[1]);
                    AssertThat(source.Length).IsEqual(2);
                }
                AssertThat(ReferenceEquals(Engine_HeroTextureMorph.SkinLayers(original, source, 0.5f), source)).IsTrue();
                AssertThat(ReferenceEquals(Engine_HeroTextureMorph.SkinLayers(original, source, float.NaN), source)).IsTrue();
                var onlyTan = Engine_HeroTextureMorph.SkinLayers(original, null, 1)!;
                AssertThat(onlyTan.Length).IsEqual(2);
                AssertThat(onlyTan[0].Texture).IsEqual(Engine_TexPathIndex.PathHash(original));
            }
            finally { Engine_TexPathIndex.SetPathSource(null); }
        }
    }

    [TestCase]
    public void TanLeavesClothingHairEyesHornsAndMissingTexturesUnchanged()
    {
        const string root = "Art/Characters/Heros/Unclothed Male/Pictures/_SharedTextures/";
        var source = new[] { new HostBridge.GuestTextureLayer(1, 0, 1, false, 0, 0) };
        Engine_TexPathIndex.SetPathSource(() => new[] { root + "mhero_face_tanned_01.tex" });
        try
        {
            foreach (string path in new[]
            {
                root + "NORM_mhero_face_01.tex", root + "SPEC_mhero_face_02.tex",
                root + "ILL_mhero_face_01.tex", root + "mhero_horns_01.tex",
                root + "mhero_legs_01.tex", root + "mhero_face_old_01.tex",
                "Art/Characters/Heros/Hair/Pictures/hero_hair_01.tex",
                "Art/Characters/Heros/Clothing/Pictures/mhero_torso_01.tex",
                "Art/Characters/Shared assets/Pictures/_SharedTextures/eye_brown_01.tex"
            }) AssertThat(ReferenceEquals(Engine_HeroTextureMorph.SkinLayers(path, source, 1), source)).IsTrue();
            AssertThat(ReferenceEquals(Engine_HeroTextureMorph.SkinLayers(root + "mhero_face_01.tex", source, 0), source)).IsTrue();
        }
        finally { Engine_TexPathIndex.SetPathSource(null); }
    }

    [TestCase]
    public void MirrorTanPreviewResetAndAutomaticModeReachTheSkinLayers()
    {
        const string root = "Art/Characters/Heros/Unclothed Female/Pictures/_SharedTextures/";
        const string original = root + "fhero_face_02.tex";
        var stored = new Dictionary<string, string>
        {
            [Engine_CharacterMorphs.AutomaticKey] = "false",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Tan)] = "0.75"
        };
        string Read(string key) => stored.GetValueOrDefault(key, "");
        var mirror = new Engine_MorphMirrorSettings(Read);
        int row = Array.FindIndex(Engine_CharacterMorphs.Sliders, slider => slider.Input == HeroMorphInput.Tan) + 1;
        Engine_TexPathIndex.SetPathSource(() => new[] { root + "fhero_face_pale_01.tex", root + "fhero_face_tanned_01.tex" });
        try
        {
            HostBridge.GuestTextureLayer[]? Preview()
            {
                var values = mirror.Values();
                float tan = Engine_CharacterMorphs.ManualTan(key => values.GetValueOrDefault(key, ""));
                return Engine_HeroTextureMorph.SkinLayers(original, null, tan);
            }
            AssertThat(Preview()![1].Weight).IsEqual(0.5f);
            mirror.Adjust(row, -100);
            AssertThat(Preview()![1].Texture).IsEqual(Engine_TexPathIndex.PathHash(root + "fhero_face_pale_01.tex"));
            AssertThat(Preview()![1].Weight).IsEqual(1f);
            AssertThat(stored[Engine_CharacterMorphs.Key(HeroMorphInput.Tan)]).IsEqual("0.75");
            mirror.Reset();
            AssertThat(Preview()![1].Texture).IsEqual(Engine_TexPathIndex.PathHash(root + "fhero_face_tanned_01.tex"));
            AssertThat(Preview()![1].Weight).IsEqual(0.5f);
            mirror.Adjust(0, 1);
            AssertThat(Preview() is null).IsTrue();
            mirror.Adjust(0, -1);
            AssertThat(Preview()![1].Weight).IsEqual(0.5f);
            mirror.Adjust(row, 100);
            foreach (var (key, value) in mirror.Values()) stored[key] = value;
            var reopened = new Engine_MorphMirrorSettings(Read);
            AssertThat(Engine_CharacterMorphs.ManualTan(key => reopened.Values().GetValueOrDefault(key, ""))).IsEqual(1f);
        }
        finally { Engine_TexPathIndex.SetPathSource(null); }
    }

    [TestCase]
    public void MirrorAdjustmentsUsePauseMenuSoundsOnlyWhenTheValueChanges()
    {
        var sounds = new List<int>();
        var mirror = new Engine_MorphMirrorSettings(_ => "", sounds.Add);
        AssertThat(sounds.Count).IsEqual(0);
        AssertThat(mirror.Adjust(1, 1)).IsFalse();
        AssertThat(mirror.Adjust(0, -1)).IsTrue();
        AssertThat(mirror.Adjust(0, -1)).IsFalse();
        AssertThat(mirror.Adjust(1, 1)).IsTrue();
        AssertThat(mirror.Adjust(1, 0)).IsFalse();
        AssertThat(mirror.Adjust(1, -1)).IsTrue();
        AssertThat(mirror.Adjust(1, -1)).IsFalse();
        AssertThat(mirror.Adjust(1, 100)).IsTrue();
        AssertThat(mirror.Adjust(1, 1)).IsFalse();
        AssertThat(mirror.Adjust(1, -100)).IsTrue();
        AssertThat(mirror.Adjust(-1, 1)).IsFalse();
        AssertThat(mirror.Adjust(10, 1)).IsFalse();
        mirror.Select(1);
        mirror.Rotate(1, 0, 0.1f);
        mirror.Reset();
        AssertThat(mirror.Count).IsEqual(1);
        AssertThat(mirror.Adjust(1, 1)).IsFalse();
        AssertThat(sounds.ToArray()).IsEqual(new[] { -1, 1, -1, 1, -1, 1 });
    }

    [TestCase]
    public void MirrorNavigationUsesPauseMenuSoundsOnlyWhenTheSelectedRowChanges()
    {
        var sounds = new List<int>();
        var mirror = new Engine_MorphMirrorSettings(_ => "", sounds.Add);
        mirror.Select(1);
        mirror.Select(-1);
        AssertThat(sounds.Count).IsEqual(0);
        mirror.Adjust(0, -1);
        sounds.Clear();
        var values = mirror.Values();
        mirror.Select(1);
        AssertThat(mirror.Selected).IsEqual(1);
        mirror.Select(-1);
        mirror.Select(-1);
        mirror.Select(0);
        AssertThat(mirror.Selected).IsEqual(0);
        mirror.Select(200);
        mirror.Select(1);
        AssertThat(mirror.Selected).IsEqual(9);
        mirror.Select(-200);
        AssertThat(mirror.Selected).IsEqual(0);
        AssertThat(mirror.Values()).IsEqual(values);
        AssertThat(sounds.ToArray()).IsEqual(new[] { 1, -1, 1, -1 });
        mirror.Adjust(0, 1);
        sounds.Clear();
        mirror.Select(1);
        mirror.Select(-1);
        AssertThat(sounds.Count).IsEqual(0);
    }

    [TestCase]
    public void MirrorRotationFiltersStickDriftAndHasTheSameSpeedAtDifferentFrameRates()
    {
        foreach (int fps in new[] { 30, 60, 120 })
        {
            var mirror = new Engine_MorphMirrorSettings(_ => "");
            foreach (float drift in new[] { 0f, 0.1f, -0.1f, 0.18f, -0.18f }) mirror.Rotate(drift, 0, 0.1f);
            AssertThat(mirror.Rotation).IsEqual(0f);
            for (int frame = 0; frame < fps; frame++) mirror.Rotate(1, 0, 1f / fps);
            AssertThat(Math.Abs(mirror.Rotation - 2.2f) < 0.0001f).IsTrue();
            for (int frame = 0; frame < fps; frame++) mirror.Rotate(-1, 0, 1f / fps);
            AssertThat(Math.Abs(mirror.Rotation) < 0.0001f).IsTrue();
        }
    }

    [TestCase]
    public void MirrorZoomFiltersStickDriftAndHasTheSameSpeedAtDifferentFrameRates()
    {
        foreach (int fps in new[] { 30, 60, 120 })
        {
            var mirror = new Engine_MorphMirrorSettings(_ => "");
            foreach (float drift in new[] { 0f, 0.1f, -0.1f, 0.18f, -0.18f }) mirror.ZoomBy(drift, 0, 0.1f);
            AssertThat(mirror.Zoom).IsEqual(0f);
            for (int frame = 0; frame < fps; frame++) mirror.ZoomBy(1, 0, 1f / fps);
            AssertThat(Math.Abs(mirror.Zoom - 0.65f) < 0.0001f).IsTrue();
            for (int frame = 0; frame < fps; frame++) mirror.ZoomBy(-1, 0, 1f / fps);
            AssertThat(mirror.Zoom < 0.0001f).IsTrue();
        }
    }

    [TestCase]
    public void MirrorWheelZoomClampsAtBothEndsAndRejectsInvalidInput()
    {
        var mirror = new Engine_MorphMirrorSettings(_ => "");
        mirror.ZoomBy(0, 1, 0);
        AssertThat(Math.Abs(mirror.Zoom - 0.12f) < 0.0001f).IsTrue();
        mirror.ZoomBy(0, -0.5f, 0);
        AssertThat(Math.Abs(mirror.Zoom - 0.06f) < 0.0001f).IsTrue();
        mirror.ZoomBy(float.NaN, float.PositiveInfinity, float.NaN);
        AssertThat(Math.Abs(mirror.Zoom - 0.06f) < 0.0001f).IsTrue();
        mirror.ZoomBy(0, 100000, 0);
        AssertThat(mirror.Zoom).IsEqual(1f);
        for (int i = 0; i < 40; i++) mirror.ZoomBy(1, 1, 0.1f);
        AssertThat(mirror.Zoom).IsEqual(1f);
        mirror.ZoomBy(0, -100000, 0);
        AssertThat(mirror.Zoom).IsEqual(0f);
        for (int i = 0; i < 40; i++) mirror.ZoomBy(-1, -1, 0.1f);
        AssertThat(mirror.Zoom).IsEqual(0f);
        mirror.ZoomBy(1, 0, -1);
        AssertThat(mirror.Zoom).IsEqual(0f);
        mirror.ZoomBy(1, 0, 60);
        AssertThat(Math.Abs(mirror.Zoom - 0.065f) < 0.0001f).IsTrue();
    }

    [TestCase]
    public void MirrorFaceZoomKeepsTheCameraAtTheMirrorAndTheFaceClearOfTheMenu()
    {
        var eye = new Vector3(285.62756f, 100.256615f, 90.376884f);
        var hero = new Vector3(286.92944f, 98.94005f, 88.06957f);
        foreach (float aspect in new[] { 4f / 3f, 16f / 9f, 21f / 9f })
        foreach (float height in new[] { 1.35f, 1.7f, 2.3f })
        foreach (float zoom in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            float panel = (Engine_UiMorphMirror.Width + 20) / (720 * aspect);
            float fov = GameMorphMirror.ZoomFov(60, zoom);
            AssertThat(fov >= 20 && fov <= 60).IsTrue();
            Transform3D camera = GameMorphMirror.FrameCamera(eye, hero, fov, aspect, panel, zoom, height);
            AssertThat(camera.Origin).IsEqual(eye);
            Vector3 target = hero + Vector3.Up * Mathf.Lerp(1.15f, height, zoom);
            Vector3 focus = camera.AffineInverse() * target;
            float tan = MathF.Tan(Mathf.DegToRad(fov / 2));
            float screenX = (1 + focus.X / (-focus.Z * tan * aspect)) / 2;
            AssertThat(Math.Abs(screenX - (1 + panel) / 2) < 0.0001f).IsTrue();
            if (zoom != 1) continue;
            foreach (float side in new[] { -0.22f, 0.22f })
            {
                Vector3 point = camera.AffineInverse() * (target + camera.Basis.X * side + Vector3.Up * side);
                AssertThat(point.Z < 0).IsTrue();
                AssertThat(Math.Abs(point.Y / (-point.Z * tan)) < 1).IsTrue();
                float x = (1 + point.X / (-point.Z * tan * aspect)) / 2;
                AssertThat(x > panel && x < 1).IsTrue();
            }
        }
        AssertThat(GameMorphMirror.ZoomFov(60, -10)).IsEqual(60f);
        AssertThat(GameMorphMirror.ZoomFov(60, 10)).IsEqual(20f);
        AssertThat(GameMorphMirror.ZoomFov(15, 1)).IsEqual(15f);
        AssertThat(GameMorphMirror.ZoomFov(float.NaN, float.NaN)).IsEqual(60f);
    }

    [TestCase]
    public void MirrorZoomAndRotationAreIndependentAndResetWithoutChangingSavedAppearance()
    {
        var mirror = new Engine_MorphMirrorSettings(_ => "");
        var saved = mirror.Values();
        mirror.Rotate(1, 20, 0.1f);
        float rotation = mirror.Rotation;
        mirror.ZoomBy(1, 1, 0.1f);
        AssertThat(mirror.Rotation).IsEqual(rotation);
        float zoom = mirror.Zoom;
        mirror.Rotate(-1, 0, 0.1f);
        AssertThat(mirror.Zoom).IsEqual(zoom);
        AssertThat(mirror.Values()).IsEqual(saved);
        AssertThat(GameMorphMirror.ZoomFov(60, mirror.Zoom) < 60).IsTrue();
        mirror.Reset();
        AssertThat(mirror.Zoom).IsEqual(0f);
        AssertThat(mirror.Rotation).IsEqual(0f);
        AssertThat(GameMorphMirror.ZoomFov(60, mirror.Zoom)).IsEqual(60f);
        AssertThat(mirror.Values()).IsEqual(saved);
        var reopened = new Engine_MorphMirrorSettings(key => saved.GetValueOrDefault(key, ""));
        AssertThat(reopened.Zoom).IsEqual(0f);
    }

    [TestCase]
    public void MirrorRotationAcceptsMouseDragAndResetsWithoutSavingThePreviewAngle()
    {
        var mirror = new Engine_MorphMirrorSettings(_ => "");
        var saved = mirror.Values();
        mirror.Rotate(0, 100, 0);
        AssertThat(Math.Abs(mirror.Rotation - 0.8f) < 0.0001f).IsTrue();
        mirror.Rotate(0, -200, 0);
        AssertThat(Math.Abs(mirror.Rotation + 0.8f) < 0.0001f).IsTrue();
        mirror.Rotate(float.NaN, float.PositiveInfinity, float.NaN);
        AssertThat(Math.Abs(mirror.Rotation + 0.8f) < 0.0001f).IsTrue();
        mirror.Rotate(0, 100000, 0);
        AssertThat(float.IsFinite(mirror.Rotation) && Math.Abs(mirror.Rotation) <= MathF.PI).IsTrue();
        AssertThat(mirror.Values()).IsEqual(saved);
        mirror.Reset();
        AssertThat(mirror.Rotation).IsEqual(0f);
        mirror.Rotate(1, 0, 60);
        AssertThat(Math.Abs(mirror.Rotation - 0.22f) < 0.0001f).IsTrue();
    }

    [TestCase]
    public void MirrorPreviewPlacesAnUprightHeroInFrontOfItsReflectiveFace()
    {
        var mirror = new Transform3D(new Vector3(-1.3095684f, -0.021519741f, -0.73114115f),
            new Vector3(-0.14392641f, 1.4776199f, 0.21429984f),
            new Vector3(-0.71715814f, -0.2572472f, 1.2920946f), new Vector3(285.57062f, 98.936325f, 90.51775f));
        foreach (Transform3D pose in new[] { Transform3D.Identity, mirror, mirror.ScaledLocal(new Vector3(2, 2, 2)) })
        {
            var (hero, eye) = Engine_MorphMirrorPlacements.PreviewFor(pose);
            Vector3 front = new Vector3(-pose.Basis.Z.X, 0, -pose.Basis.Z.Z).Normalized();
            AssertThat((hero.Origin - pose.Origin).DistanceTo(front * 2.8f) < 0.0001f).IsTrue();
            AssertThat(hero.Origin.Y).IsEqual(pose.Origin.Y);
            AssertThat(hero.Basis.Y.DistanceTo(Vector3.Up) < 0.0001f).IsTrue();
            AssertThat(hero.Basis.Determinant() > 0.999f).IsTrue();
            Vector3 towardMirror = eye - hero.Origin;
            towardMirror.Y = 0;
            AssertThat((-hero.Basis.Z).Dot(towardMirror.Normalized()) > 0.999f).IsTrue();
            AssertThat((pose.AffineInverse() * eye).DistanceTo(new Vector3(0, 0.85f, -0.25f)) < 0.0001f).IsTrue();
        }
    }

    [TestCase]
    public void MirrorCameraStaysAtTheMirrorAndFramesTheHeroBesideTheMenu()
    {
        var eye = new Vector3(285.62756f, 100.256615f, 90.376884f);
        var hero = new Vector3(286.92944f, 98.94005f, 88.06957f);
        foreach (float aspect in new[] { 4f / 3f, 16f / 9f, 21f / 9f })
        {
            float panel = (Engine_UiMorphMirror.Width + 20) / (720 * aspect);
            Transform3D camera = GameMorphMirror.FrameCamera(eye, hero, 60, aspect, panel);
            AssertThat(camera.Origin).IsEqual(eye);
            Vector3 chest = camera.AffineInverse() * (hero + Vector3.Up * 1.15f);
            float tan = MathF.Tan(Mathf.DegToRad(30));
            float screenX = (1 + chest.X / (-chest.Z * tan * aspect)) / 2;
            AssertThat(Math.Abs(screenX - (1 + panel) / 2) < 0.0001f).IsTrue();
            foreach (float height in new[] { 0f, 2.3f })
            {
                Vector3 point = camera.AffineInverse() * (hero + Vector3.Up * height);
                AssertThat(point.Z < 0).IsTrue();
                AssertThat(Math.Abs(point.Y / (-point.Z * tan)) < 1).IsTrue();
            }
        }
    }

    [TestCase]
    public void AppearanceChangesKeepTheRigOnlyWhenItsEntireBindingMatches()
    {
        var rig = new Engine_MdlRig(new[] { "Root", "Head" }, new[] { -1, 0 },
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 0, 1, 0, 1, 1, 1, 0 });
        var copy = new Engine_MdlRig((string[])rig.Names.Clone(), (int[])rig.Parents.Clone(), (float[])rig.Rest.Clone());
        AssertThat(rig.HasSameBinding(copy)).IsTrue();
        AssertThat(default(Engine_MdlRig).HasSameBinding(rig)).IsFalse();
        AssertThat(rig.HasSameBinding(default)).IsFalse();
        AssertThat(rig.HasSameBinding(copy with { Names = new[] { "Head", "Root" } })).IsFalse();
        AssertThat(rig.HasSameBinding(copy with { Parents = new[] { -1, -1 } })).IsFalse();
        AssertThat(rig.HasSameBinding(copy with { Rest = new float[10] })).IsFalse();
        copy.Rest[15] += 0.25f;
        AssertThat(rig.HasSameBinding(copy)).IsFalse();
    }

    [TestCase]
    public void MirrorModelInstallsAnIndependentNativeTargetWithoutChangingOtherMirrors()
    {
        using var mounted = new BnkFileSystem();
        const string originalPath = "art/props/bs_murgo_mirror.mdl";
        byte[] geometry = { 1, 2, 3, 4 };
        var light = new GmdAttachment("Prop.Layout.Light",
            new Transform3D(Basis.Identity, new Vector3(0.1f, 2, 0)), -1);
        byte[] metadata = Engine_GmdWriter.Write(null, new[] { light });
        mounted.AddVirtualFile(originalPath, originalPath, geometry.Length, () => geometry);
        mounted.AddVirtualFile(originalPath + ".gmd", originalPath + ".gmd", metadata.Length, () => metadata);
        for (int pass = 0; pass < 2; pass++)
        {
            Engine_MorphMirrorRuntime.AttachModel(mounted, originalPath);
            AssertThat(mounted.TryGetFile(Engine_MorphMirrorRuntime.ModelPath, out var model)).IsTrue();
            AssertThat(model.ReadAllBytes()).IsEqual(geometry);
            AssertThat(mounted.TryGetFile(Engine_MorphMirrorRuntime.ModelPath + ".gmd", out var target)).IsTrue();
            var installed = Engine_GmdSkeleton.TryParse(target.ReadAllBytes())!;
            AssertThat(installed.Attachments.Length).IsEqual(2);
            AssertThat(installed.TryResolve(Engine_MorphMirrorRuntime.TargetMarker, out var marker)).IsTrue();
            AssertThat(marker.Origin.DistanceTo(new Vector3(0, 0.85f, -0.5f)) < 0.00001f).IsTrue();
            AssertThat(installed.TryResolve(light.Name, out var kept)).IsTrue();
            AssertThat(kept.Origin.DistanceTo(light.Local.Origin) < 0.00001f).IsTrue();
            byte[] rebuilt = Engine_MorphMirrorRuntime.BuildModelMetadata(target.ReadAllBytes());
            AssertThat(rebuilt).IsEqual(target.ReadAllBytes());
        }
        AssertThat(mounted.TryGetFile(originalPath + ".gmd", out var unchanged)).IsTrue();
        AssertThat(unchanged.ReadAllBytes()).IsEqual(metadata);
        AssertThat(Engine_GmdSkeleton.TryParse(metadata)!.TryResolve(Engine_MorphMirrorRuntime.TargetMarker, out _)).IsFalse();
        bool rejectedMetadata = false;
        try { Engine_MorphMirrorRuntime.BuildModelMetadata(Array.Empty<byte>()); }
        catch (InvalidOperationException) { rejectedMetadata = true; }
        AssertThat(rejectedMetadata).IsTrue();
        bool rejectedModel = false;
        try { Engine_MorphMirrorRuntime.AttachModel(mounted, "missing.mdl"); }
        catch (InvalidOperationException) { rejectedModel = true; }
        AssertThat(rejectedModel).IsTrue();
    }

    [TestCase]
    public void MirrorTargetFacesTheHeroInsteadOfTheWallAtInstalledPlacement()
    {
        byte[] source = Engine_GmdWriter.Write(null, Array.Empty<GmdAttachment>());
        var installed = Engine_GmdSkeleton.TryParse(Engine_MorphMirrorRuntime.BuildModelMetadata(source))!;
        AssertThat(installed.TryResolve(Engine_MorphMirrorRuntime.TargetMarker, out var marker)).IsTrue();
        Vector3 local = Engine_GmdSkeleton.ToGameSpace(marker).Origin;
        var rotation = new Quaternion(0, 0, BitConverter.UInt32BitsToSingle(0x3F77D3F7), BitConverter.UInt32BitsToSingle(0xBE80550C));
        var origin = new Vector3(285.57062f, 90.51775f, 98.936325f);
        var hero = new Vector3(BitConverter.UInt32BitsToSingle(0x438EDE29),
            BitConverter.UInt32BitsToSingle(0x42B2D44A), BitConverter.UInt32BitsToSingle(0x42C5DC87));
        Vector3 target = origin + rotation * local;
        AssertThat(local.Y > 0).IsTrue();
        AssertThat((target - origin).Dot(hero - origin) > 0).IsTrue();
        AssertThat(target.DistanceSquaredTo(hero + new Vector3(0, 0, 0.5f)) < 1).IsTrue();
    }

    [TestCase]
    public void MirrorInstallsOnceInAnAlwaysOnLayerAndPreservesOtherPlacements()
    {
        byte[] source = Encoding.UTF8.GetBytes("""
            <Level>
                <id_00112233 name="DefaultLayer" load="AlwaysOn"><Entity name="Table">0x00000011</Entity></id_00112233>
                <id_00223344 name="QuestLayer" load="InitiallyOff"><Entity name="QuestActor">0x00000022</Entity></id_00223344>
            </Level>
            """);
        var first = Engine_GdbSaveMap.ParseDocument(Engine_MorphMirrorRuntime.BuildLevelSave(source), out _);
        byte[] twice = Engine_MorphMirrorRuntime.BuildLevelSave(Engine_MorphMirrorRuntime.BuildLevelSave(source));
        var installed = Engine_GdbSaveMap.ParseDocument(twice, out _);
        AssertThat(installed.GetAllEntities(true).Count).IsEqual(3);
        AssertThat(installed.TryGetEntity(Engine_MorphMirrorRuntime.Record, out var mirror, out _)).IsTrue();
        AssertThat(mirror.EntityName).IsEqual(Engine_MorphMirrorRuntime.EntityName);
        AssertThat(mirror.LoadState).IsEqual(GdbSaveLoadState.AlwaysOn);
        AssertThat(installed.FindLayer("QuestLayer")!.LoadState).IsEqual(GdbSaveLoadState.InitiallyOff);
        AssertThat(installed.FindLayer("DefaultLayer")!.LayerTag).IsEqual("id_00112233");
        AssertThat(first.EntriesByHash).IsEqual(installed.EntriesByHash);
        string lua = Engine_MorphMirrorRuntime.ScriptFor("caves/bowerlake/thagscave", "CVector3(1,2,3)", "CVector3(4,5,6)", "CVector3(0,1,0)");
        AssertThat(lua.Contains("mirror:Destroy()", StringComparison.Ordinal)).IsFalse();
        AssertThat(lua.Contains("not player:IsAlive()", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void MirrorDatabaseUsesTheAuthoredTransformWithoutChangingTheTemplateOrOtherOverlays()
    {
        var source = new GdbPatchDatabase();
        GdbPatchEntry Entry(uint id)
        {
            var entry = new GdbPatchEntry(id, 0);
            source.Entries.Add(entry);
            return entry;
        }
        void Property(GdbPatchEntry entry, string name, GdbFieldType type, uint value) =>
            entry.Properties.Add(new GdbPatchProperty(Engine_GameHash.Fnv1(name), type, value));
        var template = Entry(Engine_GoldenCockRuntime.TemplateRecord);
        uint id = 1;
        foreach (string component in new[] { "OnActionUseComponent", "TargetedComponent", "GraphicAppearanceStaticMeshComponent" })
        {
            Property(template, component, GdbFieldType.Reference, id);
            Entry(id++);
        }
        var physics = Entry(Engine_GameHash.Fnv1("PhysicsSimpleComponent"));
        Property(physics, "Radius", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(1));
        byte[] original = GdbWriter.Serialize(source);
        byte[] golden = Engine_GoldenCockRuntime.BuildDatabase(original);
        var pose = new Transform3D(GdbTransformMath.RotationBasis(0.2f, 1.3f, -0.1f, 1.5f), new Vector3(285.57f, 98.94f, 90.52f));
        const string model = Engine_MorphMirrorRuntime.ModelPath;
        byte[] installed = Engine_MorphMirrorRuntime.BuildDatabase(original, golden, model, pose);
        var multi = GdbMulti.Build(new[] { original, installed });
        uint Field(uint record, string name)
        {
            AssertThat(multi.TryLookup(record, out var cursor)).IsTrue();
            return multi.InheritedFields(cursor).Single(field => field.Hash == Engine_GameHash.Fnv1(name)).Raw;
        }
        uint placedPhysics = Field(Engine_MorphMirrorRuntime.Record, "PhysicsSimpleComponent");
        uint position = Field(placedPhysics, "Position");
        AssertThat(BitConverter.UInt32BitsToSingle(Field(position, "X"))).IsEqual(pose.Origin.X);
        AssertThat(BitConverter.UInt32BitsToSingle(Field(position, "Y"))).IsEqual(pose.Origin.Z);
        AssertThat(BitConverter.UInt32BitsToSingle(Field(position, "Z"))).IsEqual(pose.Origin.Y);
        uint rotation = Field(placedPhysics, "Rotation");
        GdbTransformMath.ExtractGdbTransform(pose, out _, out _, out _, out float rx, out float ry, out float rz, out float scale);
        AssertThat(BitConverter.UInt32BitsToSingle(Field(rotation, "X"))).IsEqual(rx);
        AssertThat(BitConverter.UInt32BitsToSingle(Field(rotation, "Y"))).IsEqual(ry);
        AssertThat(BitConverter.UInt32BitsToSingle(Field(rotation, "Z"))).IsEqual(rz);
        AssertThat(BitConverter.UInt32BitsToSingle(Field(placedPhysics, "Scale"))).IsEqual(scale);
        uint mesh = Field(Engine_MorphMirrorRuntime.Record, "GraphicAppearanceStaticMeshComponent");
        AssertThat(Field(mesh, "ModelFile")).IsEqual(Engine_GameHash.ResourcePath(model));
        uint use = Field(Engine_MorphMirrorRuntime.Record, "OnActionUseComponent");
        AssertThat(Field(use, "ScriptToRun")).IsEqual(Engine_GameHash.Fnv1(Engine_MorphMirrorRuntime.ActionName));
        AssertThat(Field(Engine_GoldenCockRuntime.TemplateRecord, "OnActionUseComponent")).IsEqual(1u);
        uint cockUse = Field(Engine_GoldenCockRuntime.Record, "OnActionUseComponent");
        AssertThat(Field(cockUse, "ScriptToRun")).IsEqual(Engine_GameHash.Fnv1(Engine_GoldenCockRuntime.ActionName));
        AssertThat(GdbWriter.Serialize(source)).IsEqual(original);
    }

    [TestCase]
    public void MirrorUsesTheFullCavePathWithLuaEscapedSeparators()
    {
        foreach (string path in new[] { "caves/bowerlake/thagscave", @"caves\bowerlake\thagscave" })
        {
            string lua = Engine_MorphMirrorRuntime.ScriptFor(path, "CVector3(1,2,3)", "CVector3(4,5,6)", "CVector3(0,1,0)");
            string levelCheck = "IsLevelLoaded(\"caves\\\\bowerlake\\\\thagscave\")";
            AssertThat(lua.Split(levelCheck).Length - 1).IsEqual(3);
            AssertThat(lua.Contains("IsLevelLoaded(\"thagscave\")", StringComparison.Ordinal)).IsFalse();
        }
    }

    [TestCase]
    public void MirrorPreviewsRemainSeparateFromSavedSettingsAndResetRestoresThem()
    {
        var stored = new Dictionary<string, string>
        {
            [Engine_CharacterMorphs.AutomaticKey] = "false",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Strength)] = "0.35"
        };
        var mirror = new Engine_MorphMirrorSettings(key => stored.GetValueOrDefault(key, ""));
        AssertThat(mirror.Count).IsEqual(10);
        AssertThat(mirror.Adjust(1, 1)).IsTrue();
        AssertThat(mirror.Values()[Engine_CharacterMorphs.Key(HeroMorphInput.Strength)]).IsEqual("0.4");
        AssertThat(stored[Engine_CharacterMorphs.Key(HeroMorphInput.Strength)]).IsEqual("0.35");
        mirror.Select(200);
        AssertThat(mirror.Selected).IsEqual(9);
        AssertThat(mirror.Adjust(0, 1)).IsTrue();
        AssertThat(mirror.Count).IsEqual(1);
        AssertThat(mirror.Selected).IsEqual(0);
        AssertThat(mirror.Adjust(1, 1)).IsFalse();
        mirror.Reset();
        AssertThat(mirror.Count).IsEqual(10);
        AssertThat(mirror.Values()[Engine_CharacterMorphs.Key(HeroMorphInput.Strength)]).IsEqual("0.35");
    }

    [TestCase]
    public void MirrorClearsWeightCompletelyAndResetRestoresTheSavedAppearance()
    {
        var saved = new Dictionary<string, string>
        {
            [Engine_CharacterMorphs.AutomaticKey] = "false",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Fat)] = "1"
        };
        var mirror = new Engine_MorphMirrorSettings(key => saved.GetValueOrDefault(key, ""));
        int weightRow = Array.FindIndex(Engine_CharacterMorphs.Sliders, slider => slider.Input == HeroMorphInput.Fat) + 1;
        AssertThat(mirror.Adjust(weightRow, -100)).IsTrue();
        var published = new Dictionary<string, float>();
        var values = mirror.Values();
        Engine_CharacterMorphs.Publish(Engine_CharacterMorphs.StoreValues(key => values.GetValueOrDefault(key, "")),
            (key, value) => published[key] = value, 1, 1);
        AssertThat(published[Engine_CharacterMorphs.BridgePrefix(1) + Engine_HeroMorphState.TypeFat]).IsEqual(0f);
        AssertThat(saved[Engine_CharacterMorphs.Key(HeroMorphInput.Fat)]).IsEqual("1");
        mirror.Reset();
        values = mirror.Values();
        Engine_CharacterMorphs.Publish(Engine_CharacterMorphs.StoreValues(key => values.GetValueOrDefault(key, "")),
            (key, value) => published[key] = value, 1, 2);
        AssertThat(published[Engine_CharacterMorphs.BridgePrefix(1) + Engine_HeroMorphState.TypeFat]).IsEqual(1f);
    }

    [TestCase]
    public void MirrorManualSlidersClampAndAutomaticModePreservesTheManualValues()
    {
        var mirror = new Engine_MorphMirrorSettings(_ => "");
        AssertThat(mirror.Count).IsEqual(1);
        AssertThat(mirror.Adjust(0, -1)).IsTrue();
        for (int i = 1; i < 10; i++)
        {
            mirror.Adjust(i, 1000);
            var slider = Engine_CharacterMorphs.Sliders[i - 1];
            var values = mirror.Values();
            AssertThat(Engine_CharacterMorphs.Read(k => values.GetValueOrDefault(k, ""))[slider.Input]).IsEqual(slider.Maximum);
            AssertThat(mirror.Adjust(i, 1)).IsFalse();
        }
        var manual = mirror.Values();
        mirror.Adjust(0, 1);
        mirror.Adjust(0, -1);
        AssertThat(mirror.Values()).IsEqual(manual);
        AssertThat(mirror.Adjust(-1, 1)).IsFalse();
        AssertThat(mirror.Adjust(10, 1)).IsFalse();
    }

    [TestCase]
    public void MirrorInteractionIsRestrictedToItsNamedInstanceAndHasAReleaseWatchdog()
    {
        string lua = Engine_MorphMirrorRuntime.ScriptFor("ThagsCave", "CVector3(1,2,3)", "CVector3(4,5,6)", "CVector3(0,1,0)");
        AssertThat(lua.Contains("GetName() ~= \"RefiiMorphMirror\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("local player = Refii.player_of(Refii.handle(self.EntityUsingItem))", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("not Refii.player_here(player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.publish(\"" + Engine_MorphMirrorRuntime.PlayerKey + "\", player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("now - touched > 150", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("hero:SetPosition(originalPosition)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Physics.SetFacingVector(hero, originalFacing)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Physics.SetFacingVector(hero, previewFacing)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.value(\"" + Engine_MorphMirrorRuntime.RotationKey + "\")", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("coroutine.yield", StringComparison.Ordinal)).IsFalse();
        AssertThat(Engine_MorphMirrorPlacements.IsMirror("art/objects/BS_MURGO_MIRROR.MDL")).IsTrue();
        AssertThat(Engine_MorphMirrorPlacements.IsMirror("art/objects/mirror.mdl")).IsFalse();
    }

    [TestCase]
    public void MirrorHoldsAStationaryIdleWithoutRestartingItWhileRotating()
    {
        string lua = Engine_MorphMirrorRuntime.ScriptFor("ThagsCave", "CVector3(1,2,3)", "CVector3(4,5,6)", "CVector3(0,1,0)");
        AssertThat(lua.Contains("if idleAction and Action.IsActionPluggedIn(hero, idleAction) then return true end", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Anim = \"Idle\", HasMovement = false, OverrideLooking = true", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("CanBeOverridenBySamePriority = false", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("NumLoops = 0", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("if idleAction and Action.IsActionPluggedIn(hero, idleAction) then Action.FinishAllActions(hero) end", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Split("if not RefiiMorphMirror.HoldIdle() then RefiiMorphMirror.End(); return end").Length - 1).IsEqual(2);
    }

    [TestCase]
    public void DefaultsRetainAutomaticMorphsAndNeutralManualAppearance()
    {
        AssertThat(Engine_CharacterMorphs.Automatic(_ => "")).IsTrue();
        foreach (string off in new[] { "false", "FALSE", "0", "off", "\"false\"" })
            AssertThat(Engine_CharacterMorphs.Automatic(_ => off)).IsFalse();
        foreach (string on in new[] { "true", "1", "on", "invalid" })
            AssertThat(Engine_CharacterMorphs.Automatic(_ => on)).IsTrue();
        var stats = Engine_CharacterMorphs.Read(_ => "");
        for (uint type = 2; type <= 18; type++) AssertThat(Engine_HeroMorphState.Weight(stats, type)).IsEqual(0f);
        AssertThat(Engine_CharacterMorphs.Sliders.Select(s => s.Input).Distinct().Count()).IsEqual(9);
    }

    [TestCase]
    public void ManualRangesClampAndRejectInvalidNumbers()
    {
        foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "bad", "1e99" })
        {
            var stats = Engine_CharacterMorphs.Read(_ => invalid);
            AssertThat(stats.Age).IsEqual(30f);
            AssertThat(stats.Morality).IsEqual(0.5f);
            AssertThat(stats.Strength).IsEqual(0f);
        }
        var high = Engine_CharacterMorphs.Read(_ => "999");
        var low = Engine_CharacterMorphs.Read(_ => "-999");
        foreach (var slider in Engine_CharacterMorphs.Sliders)
        {
            AssertThat(high[slider.Input]).IsEqual(slider.Maximum);
            AssertThat(low[slider.Input]).IsEqual(slider.Minimum);
            AssertThat(Engine_CharacterMorphs.Value(slider, -100)).IsEqual(slider.Minimum);
            AssertThat(Engine_CharacterMorphs.Value(slider, 999)).IsEqual(slider.Maximum);
        }
    }

    [TestCase]
    public void ManualAppearanceRoundTripsThroughConfigWithoutChangingOtherSettings()
    {
        string file = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, Guid.NewGuid() + ".toml");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var settings = Engine_CharacterMorphs.Values(row => row == 0 ? 0 : row + 2);
            var values = new SortedDictionary<string, string>(settings) { ["master_volume"] = "0.7" };
            AssertThat(RuntimeConfig.WriteValues(file, values, out _)).IsTrue();
            var restored = RuntimeConfig.ReadValues(file);
            string Read(string key) => restored.GetValueOrDefault(key, "");
            AssertThat(Engine_CharacterMorphs.Automatic(Read)).IsFalse();
            var stats = Engine_CharacterMorphs.Read(Read);
            for (int i = 0; i < Engine_CharacterMorphs.Sliders.Length; i++)
            {
                var slider = Engine_CharacterMorphs.Sliders[i];
                AssertThat(Math.Abs(stats[slider.Input] - Engine_CharacterMorphs.Value(slider, i + 3)) < 0.0001f).IsTrue();
            }
            AssertThat(restored["master_volume"]).IsEqual("0.7");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            System.IO.File.Delete(file);
        }
    }

    [TestCase]
    public void RuntimePublishesVisualWeightsThenCommitsTheGeneration()
    {
        var values = new Dictionary<string, string>
        {
            [Engine_CharacterMorphs.AutomaticKey] = "false",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Strength)] = "0.8",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Skill)] = "0.9",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Will)] = "0.75",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Morality)] = "0",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Purity)] = "0",
        };
        var sent = new Dictionary<string, float>();
        string last = "";
        Engine_CharacterMorphs.Publish(Engine_CharacterMorphs.StoreValues(key => values.GetValueOrDefault(key, "")),
            (key, value) => { sent[key] = value; last = key; }, 1, 7);
        string prefix = Engine_CharacterMorphs.BridgePrefix(1);
        AssertThat(last).IsEqual(prefix + "generation");
        AssertThat(sent[last]).IsEqual(7f);
        AssertThat(sent[prefix + "automatic"]).IsEqual(0f);
        AssertThat(sent[prefix + "5"]).IsEqual(0.6f);
        AssertThat(sent[prefix + "7"]).IsEqual(0.75f);
        AssertThat(sent[prefix + "14"]).IsEqual(1f);
        values[Engine_CharacterMorphs.AutomaticKey] = "true";
        Engine_CharacterMorphs.Publish(Engine_CharacterMorphs.StoreValues(key => values.GetValueOrDefault(key, "")), (key, value) => sent[key] = value, 1, 8);
        AssertThat(sent[prefix + "automatic"]).IsEqual(1f);
        AssertThat(sent[prefix + "7"]).IsEqual(0.75f);
    }

    [TestCase]
    public void RuntimePublishesTheInputsVillagersJudgeBeforeTheGeneration()
    {
        var values = new Dictionary<string, string>
        {
            [Engine_CharacterMorphs.AutomaticKey] = "false",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Fat)] = "0.9",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Strength)] = "0.8",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Skill)] = "0.9",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Height)] = "0.2",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Tan)] = "0.2",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Will)] = "0.75",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Purity)] = "0.1",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Age)] = "45",
        };
        var order = new List<string>();
        var sent = new Dictionary<string, float>();
        Engine_CharacterMorphs.Publish(Engine_CharacterMorphs.StoreValues(key => values.GetValueOrDefault(key, "")),
            (key, value) => { sent[key] = value; order.Add(key); }, 1, 3);
        string prefix = Engine_CharacterMorphs.BridgePrefix(1);

        // The raw inputs, not the capped weights: the reaction path loads the fields themselves.
        AssertThat(sent[Engine_CharacterMorphs.BridgeKey(HeroMorphInput.Fat, 1)]).IsEqual(0.9f);
        AssertThat(sent[prefix + Engine_HeroMorphState.TypeFat]).IsEqual(Engine_HeroMorphState.FatCap);
        AssertThat(sent[prefix + "strength"]).IsEqual(0.8f);
        AssertThat(sent[prefix + Engine_HeroMorphState.TypeStrength]).IsEqual(Engine_HeroMorphState.StrengthCap);
        AssertThat(sent[prefix + "height"]).IsEqual(0.2f);
        AssertThat(sent[prefix + "tan"]).IsEqual(0.2f);
        AssertThat(sent[prefix + "will"]).IsEqual(0.75f);
        AssertThat(sent[prefix + "purity"]).IsEqual(0.1f);
        AssertThat(sent[prefix + "age"]).IsEqual(45f);
        AssertThat(sent.ContainsKey(prefix + "skill")).IsFalse();
        AssertThat(sent.ContainsKey(prefix + "morality")).IsFalse();

        // The hook reads every value when the generation moves, so each is sent before it.
        int generation = order.IndexOf(prefix + "generation");
        AssertThat(generation).IsEqual(order.Count - 1);
        foreach (HeroMorphInput input in Engine_CharacterMorphs.JudgedInputs)
            AssertThat(order.IndexOf(Engine_CharacterMorphs.BridgeKey(input, 1)) < generation).IsTrue();
    }

    [TestCase]
    public void MorphsAreReachedThroughTheMirrorInsteadOfSettings()
    {
        var game = Engine_UiTemplates.ComposeGame();
        var options = game.Menus["OptionsMenu"];
        AssertThat(options.Entries.Any(e => e.Name == "CharacterSettings")).IsFalse();
        CheckEntries(options.Entries);
        AssertThat(Engine_UiHostOptionsPage.For(Engine_UiCharacterOptions.OpenCommand)).IsNull();

        static void CheckEntries(IEnumerable<Engine_UiMenuEntry> entries)
        {
            foreach (var entry in entries)
            {
                AssertThat(entry.Name == "CharacterMorphs" || entry.Command == Engine_UiCharacterOptions.OpenCommand).IsFalse();
                CheckEntries(entry.Children);
            }
        }
    }

    [TestCase]
    public void FailedSaveKeepsManualEditsOpenAndCancelRestoresTheStoredMode()
    {
        var page = new Engine_UiCharacterOptions(_ => "", _ => false);
        Engine_UiOptionsPending.Leave();
        try
        {
            Engine_UiOptionsPending.EnterHost(page);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
            AssertThat(Engine_UiHostOptionsPage.Current).IsSame(page);
            AssertThat(page.Count).IsEqual(10);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Back);
            Engine_UiOptionsPending.EnterHost(page);
            AssertThat(page.Count).IsEqual(1);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(0);
        }
        finally { Engine_UiOptionsPending.Leave(); }
    }

    [TestCase]
    public void OptionsLifecycleHidesManualRowsUntilOffAndPreservesThemAcrossToggles()
    {
        var stored = new Dictionary<string, string>();
        int writes = 0;
        var page = new Engine_UiCharacterOptions(key => stored.GetValueOrDefault(key, ""), values =>
        {
            foreach (var pair in values) stored[pair.Key] = pair.Value;
            writes++;
            return true;
        });
        Engine_UiOptionsPending.Leave();
        try
        {
            Engine_UiOptionsPending.EnterHost(page);
            AssertThat(page.Count).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
            AssertThat(Engine_UiOptionsPending.Row).IsEqual(0);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
            AssertThat(page.Count).IsEqual(10);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(1);
            AssertThat(writes).IsEqual(0);
            var paint = page.Paint(Engine_UiOptionsPending.IndexOf, true, 9);
            AssertThat(paint.Values[Engine_UiHostOptionsPage.TitleField]).IsEqual("Character - Morphs (6-10/10)");
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
            AssertThat(writes).IsEqual(1);
            AssertThat(stored[Engine_CharacterMorphs.AutomaticKey]).IsEqual("false");
            AssertThat(stored[Engine_CharacterMorphs.Key(HeroMorphInput.Strength)]).IsEqual("0.05");

            Engine_UiOptionsPending.EnterHost(page);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
            AssertThat(page.Count).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
            AssertThat(stored[Engine_CharacterMorphs.AutomaticKey]).IsEqual("true");
            Engine_UiOptionsPending.EnterHost(page);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Back);
            AssertThat(writes).IsEqual(2);
            Engine_UiOptionsPending.EnterHost(page);
            AssertThat(page.Count).IsEqual(1);
        }
        finally { Engine_UiOptionsPending.Leave(); }
    }

    // ---- Murgo's Mirror for both players --------------------------------------------------------

    [TestCase]
    public void EachSettingsSlotKeepsItsOwnLookUnderThePadmapShape()
    {
        AssertThat(Engine_CharacterMorphs.AutomaticKeyOf(1)).IsEqual("character_morphs");
        AssertThat(Engine_CharacterMorphs.AutomaticKeyOf(2)).IsEqual("character_morphs2");
        AssertThat(Engine_CharacterMorphs.Key(HeroMorphInput.Fat, 1)).IsEqual("character_morph_fat");
        AssertThat(Engine_CharacterMorphs.Key(HeroMorphInput.Fat, 2)).IsEqual("character_morph2_fat");
        AssertThat(Engine_CharacterMorphs.SlotKey("master_volume", 2)).IsEqual("master_volume");

        var stored = new Dictionary<string, string>
        {
            ["character_morphs"] = "false", ["character_morph_fat"] = "0.25",
            ["character_morphs2"] = "false", ["character_morph2_fat"] = "0.75",
        };
        string Read(string key) => stored.GetValueOrDefault(key, "");
        var one = new Engine_MorphMirrorSettings(Engine_CharacterMorphs.ForSlot(Read, 1));
        var two = new Engine_MorphMirrorSettings(Engine_CharacterMorphs.ForSlot(Read, 2));
        int fatRow = Array.FindIndex(Engine_CharacterMorphs.Sliders, slider => slider.Input == HeroMorphInput.Fat) + 1;
        AssertThat(one.Values()[Engine_CharacterMorphs.Key(HeroMorphInput.Fat)]).IsEqual("0.25");
        AssertThat(two.Values()[Engine_CharacterMorphs.Key(HeroMorphInput.Fat)]).IsEqual("0.75");

        // Player two's Apply writes player two's keys only.
        AssertThat(two.Adjust(fatRow, 1)).IsTrue();
        var written = Engine_CharacterMorphs.ForSlot(two.Values(), 2);
        AssertThat(written.Keys.All(key => key == "character_morphs2" || key.StartsWith("character_morph2_", StringComparison.Ordinal))).IsTrue();
        AssertThat(written["character_morph2_fat"]).IsEqual("0.8");
        foreach (var (key, value) in written) stored[key] = value;
        AssertThat(stored["character_morph_fat"]).IsEqual("0.25");
        AssertThat(Engine_CharacterMorphs.StoreValues(Engine_CharacterMorphs.ForSlot(Read, 2))[Engine_CharacterMorphs.StoreKey(HeroMorphInput.Fat)]).IsEqual(0.8f);
    }

    [TestCase]
    public void TheStoredLookRoundTripsExactlyThroughEitherSlotsSettings()
    {
        var look = new Dictionary<string, float> { [Engine_CharacterMorphs.StoreAutomaticKey] = 0 };
        foreach (var slider in Engine_CharacterMorphs.Sliders) look[Engine_CharacterMorphs.StoreKey(slider.Input)] = slider.Minimum + 0.333f;
        foreach (int slot in new[] { 1, 2 })
        {
            var settings = Engine_CharacterMorphs.SettingsOf(look, slot);
            AssertThat(settings[Engine_CharacterMorphs.AutomaticKeyOf(slot)]).IsEqual("false");
            var back = Engine_CharacterMorphs.StoreValues(Engine_CharacterMorphs.ForSlot(key => settings.GetValueOrDefault(key, ""), slot));
            AssertThat(Engine_CharacterMorphs.SameLook(back, look)).IsTrue();
        }

        AssertThat(Engine_CharacterMorphs.StoredLook(key => look.TryGetValue(key, out float v) ? v : null)!.Count).IsEqual(10);
        var partial = new Dictionary<string, float>(look);
        partial.Remove(Engine_CharacterMorphs.StoreKey(HeroMorphInput.Age));
        AssertThat(Engine_CharacterMorphs.StoredLook(key => partial.TryGetValue(key, out float v) ? v : null)).IsNull();

        // appearance_state P AUTOMATIC then refii_mod_appearance_t's order: tan fat height strength skill will morality purity age.
        string line = Engine_CharacterMorphs.StateCommand(2, look);
        string[] words = line.Split(' ');
        AssertThat(words[0]).IsEqual("appearance_state");
        AssertThat(words[1]).IsEqual("2");
        AssertThat(words[2]).IsEqual("0");
        AssertThat(words.Length).IsEqual(12);
        for (int i = 0; i < Engine_CharacterMorphs.StateOrder.Length; i++)
            AssertThat(float.Parse(words[3 + i], CultureInfo.InvariantCulture)).IsEqual(look[Engine_CharacterMorphs.StoreKey(Engine_CharacterMorphs.StateOrder[i])]);
        AssertThat(string.Join(",", Engine_CharacterMorphs.StateOrder.Select(i => i.ToString().ToLowerInvariant())))
            .IsEqual("tan,fat,height,strength,skill,will,morality,purity,age");
    }

    [TestCase]
    public void EachPlayersLookIsPublishedUnderItsOwnPrefixAndNoLookIsAutomatic()
    {
        var look = Engine_CharacterMorphs.StoreValues(key => key == Engine_CharacterMorphs.AutomaticKey ? "false" :
            key == Engine_CharacterMorphs.Key(HeroMorphInput.Will) ? "0.75" : "");
        var sent = new Dictionary<string, float>();
        Engine_CharacterMorphs.Publish(look, (key, value) => sent[key] = value, 2, 5);
        AssertThat(sent["refii.character_morphs.p2.7"]).IsEqual(0.75f);
        AssertThat(sent["refii.character_morphs.p2.automatic"]).IsEqual(0f);
        AssertThat(sent["refii.character_morphs.p2.generation"]).IsEqual(5f);
        AssertThat(sent.Keys.Any(key => key.StartsWith("refii.character_morphs.p1.", StringComparison.Ordinal))).IsFalse();
        AssertThat(Engine_CharacterMorphs.LookTan(look)).IsEqual(0.5f);

        sent.Clear();
        Engine_CharacterMorphs.Publish(null, (key, value) => sent[key] = value, 1, 6);
        AssertThat(sent["refii.character_morphs.p1.automatic"]).IsEqual(1f);
        AssertThat(Engine_CharacterMorphs.LookTan(null)).IsEqual(0.5f);
    }

    [TestCase]
    public void TheSlotFollowsTheSavedLookStatesTheMirrorAndNeverWritesForAnotherMachine()
    {
        Dictionary<string, float> Look(float fat, bool automatic = false)
        {
            var look = Engine_CharacterMorphs.StoreValues(_ => "");
            look[Engine_CharacterMorphs.StoreAutomaticKey] = automatic ? 1 : 0;
            look[Engine_CharacterMorphs.StoreKey(HeroMorphInput.Fat)] = fat;
            return look;
        }
        var sync = new Engine_MorphLookSync();
        var local = Look(0.2f);

        // No player for the slot (front end, or its player is played on the other machine): nothing.
        AssertThat(sync.Next(0, local, null, 0, out _)).IsEqual(Engine_MorphLookSync.Step.None);
        // Bound: the slot takes the hero's saved look, never the other way round.
        var saved = Look(0.9f);
        AssertThat(sync.Next(1, local, saved, 0, out var values)).IsEqual(Engine_MorphLookSync.Step.Persist);
        AssertThat(Engine_CharacterMorphs.SameLook(values, saved)).IsTrue();
        AssertThat(sync.Next(1, saved, saved, 0.1, out _)).IsEqual(Engine_MorphLookSync.Step.None);
        // The Mirror's preview (or Apply) is stated; until it lands the old store value is not a change.
        var preview = Look(1f);
        AssertThat(sync.Next(1, preview, saved, 1, out values)).IsEqual(Engine_MorphLookSync.Step.Push);
        AssertThat(Engine_CharacterMorphs.SameLook(values, preview)).IsTrue();
        AssertThat(sync.Next(1, preview, saved, 1.2, out _)).IsEqual(Engine_MorphLookSync.Step.None);
        AssertThat(sync.Next(1, preview, preview, 1.3, out _)).IsEqual(Engine_MorphLookSync.Step.None);
        // A save load (or a mod's write, here or from the other machine) is kept in the slot's settings.
        var other = Look(0.6f);
        AssertThat(sync.Next(1, preview, other, 5, out values)).IsEqual(Engine_MorphLookSync.Step.Persist);
        AssertThat(Engine_CharacterMorphs.SameLook(values, other)).IsTrue();
        AssertThat(sync.Next(1, other, other, 5.1, out _)).IsEqual(Engine_MorphLookSync.Step.None);
        // A save made without a look, or a new game: the game's own look, the slot automatic.
        AssertThat(sync.Next(1, other, null, 6, out values)).IsEqual(Engine_MorphLookSync.Step.Persist);
        AssertThat(values[Engine_CharacterMorphs.StoreAutomaticKey]).IsEqual(1f);
        var automatic = new Dictionary<string, float>(values);
        AssertThat(sync.Next(1, automatic, null, 6.1, out _)).IsEqual(Engine_MorphLookSync.Step.None);
        // A join moves slot 1 to player two: the slot takes player two's look; player one's is the host's.
        AssertThat(sync.Next(2, automatic, Look(0.1f), 7, out values)).IsEqual(Engine_MorphLookSync.Step.Persist);
        AssertThat(sync.Player).IsEqual(2);
        AssertThat(sync.Next(0, automatic, null, 8, out _)).IsEqual(Engine_MorphLookSync.Step.None);
        AssertThat(sync.Player).IsEqual(0);
    }

    [TestCase]
    public void TheKeptFormsAnswerAsTheFreshOnesAndKeepNothing()
    {
        var settings = new Dictionary<string, string>
        {
            ["character_morphs"] = "false",
            ["character_morph_tan"] = "0.25",
            ["character_morph_age"] = "99",
            ["character_morph_will"] = "nan",
        };
        Func<string, string> read = key => settings.TryGetValue(key, out string? value) ? value : string.Empty;
        var kept = new Dictionary<string, float> { ["left over"] = 1 };
        var fresh = Engine_CharacterMorphs.StoreValues(read);
        AssertThat(ReferenceEquals(Engine_CharacterMorphs.StoreValues(read, kept), kept)).IsTrue();
        AssertThat(Engine_CharacterMorphs.SameLook(fresh, kept)).IsTrue();

        // The read before leaves no mark on the next: an input the settings stop stating is its default again.
        settings.Clear();
        AssertThat(Engine_CharacterMorphs.SameLook(Engine_CharacterMorphs.StoreValues(read), Engine_CharacterMorphs.StoreValues(read, kept))).IsTrue();
        AssertThat(kept[Engine_CharacterMorphs.StoreKey(HeroMorphInput.Tan)]).IsEqual(0.5f);
        AssertThat(kept[Engine_CharacterMorphs.StoreKey(HeroMorphInput.Age)]).IsEqual(30f);

        Func<string, float?> whole = key => key.EndsWith("automatic") ? 0 : 0.25f;
        AssertThat(Engine_CharacterMorphs.StoredLook(whole, kept)).IsTrue();
        AssertThat(Engine_CharacterMorphs.SameLook(Engine_CharacterMorphs.StoredLook(whole), kept)).IsTrue();
        Func<string, float?> lacking = key => key.EndsWith("age") ? null : 0.25f;
        AssertThat(Engine_CharacterMorphs.StoredLook(lacking, kept)).IsFalse();
        AssertThat(Engine_CharacterMorphs.StoredLook(lacking)).IsNull();
    }

    [TestCase]
    public void TheLookCheckAllocatesNothingOnAStandingWorld()
    {
        Func<string, string> slot1 = _ => string.Empty;
        Func<string, string> slot2 = Engine_CharacterMorphs.ForSlot(slot1, 2);
        Func<string, float?> store = _ => 0.5f;
        var local = new Dictionary<string, float>();
        var stored = new Dictionary<string, float>();
        void Check()
        {
            Engine_CharacterMorphs.StoreValues(slot1, local);
            Engine_CharacterMorphs.StoreValues(slot2, local);
            Engine_CharacterMorphs.StoredLook(store, stored);
            Engine_CharacterMorphs.SameLook(stored, stored);
        }

        Check();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Check();
        AssertThat(GC.GetAllocatedBytesForCurrentThread() - before).IsEqual(0L);
    }

    [TestCase]
    public void TheKeysAreTheInputsNamedInLowerCase()
    {
        foreach (HeroMorphInput input in Enum.GetValues<HeroMorphInput>())
        {
            string name = input.ToString().ToLowerInvariant();
            AssertThat(Engine_CharacterMorphs.Key(input)).IsEqual("character_morph_" + name);
            AssertThat(Engine_CharacterMorphs.Key(input, 1)).IsEqual("character_morph_" + name);
            AssertThat(Engine_CharacterMorphs.Key(input, 2)).IsEqual("character_morph2_" + name);
            AssertThat(Engine_CharacterMorphs.StoreKey(input)).IsEqual("character_morphs." + name);
            AssertThat(Engine_CharacterMorphs.SlotKey("character_morph_" + name, 2)).IsEqual("character_morph2_" + name);
        }

        AssertThat(Engine_CharacterMorphs.SlotKey("character_morph_other", 2)).IsEqual("character_morph2_other");
        AssertThat(Engine_CharacterMorphs.SlotKey("character_morphs", 2)).IsEqual("character_morphs2");
        AssertThat(Engine_CharacterMorphs.SlotKey("something_else", 2)).IsEqual("something_else");
    }
}
