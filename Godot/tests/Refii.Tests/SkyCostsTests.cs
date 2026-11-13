using System;
using System.Text.Json;
using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The sky's GPU shortcuts (Engine_SkyCosts): the radiance map held lean.</summary>
[TestSuite]
public sealed class SkyCostsTests
{
    /// <summary>The debug lever takes a boolean or nothing.</summary>
    [TestCase]
    public void TheLeverReadsABoolean()
    {
        using JsonDocument on = JsonDocument.Parse("{\"skyRadianceLean\": true}");
        using JsonDocument bad = JsonDocument.Parse("{\"skyRadianceLean\": null}");
        AssertThat(Engine_SkyCosts.ReadRequest(on.RootElement, Engine_SkyCosts.RadianceLeanRequestKey)).IsEqual(true);
        AssertThat(Engine_SkyCosts.ReadRequest(on.RootElement, "other") is null).IsTrue();
        AssertThrown(() => Engine_SkyCosts.ReadRequest(bad.RootElement, Engine_SkyCosts.RadianceLeanRequestKey))
            .IsInstanceOf<ArgumentException>();
    }

    private const Godot.Environment.ReflectionSource NoReflection = Godot.Environment.ReflectionSource.Disabled;
    private const Godot.Environment.AmbientSource NoAmbient = Godot.Environment.AmbientSource.Disabled;
    private const Godot.Environment.BGMode SkyBackground = Godot.Environment.BGMode.Sky;

    /// <summary>
    /// Volumetric fog reads the radiance only where the renderer draws it: Forward+. On a phone's Mobile
    /// renderer the modern model's fog leaves the map unread, so it can be held lean.
    /// </summary>
    [TestCase]
    public void VolumetricFogReadsOnlyWhereItIsDrawn()
    {
        AssertThat(Engine_SkyCosts.VolumetricFogReads(true, "forward_plus")).IsTrue();
        AssertThat(Engine_SkyCosts.VolumetricFogReads(true, "mobile")).IsFalse();
        AssertThat(Engine_SkyCosts.VolumetricFogReads(true, "gl_compatibility")).IsFalse();
        AssertThat(Engine_SkyCosts.VolumetricFogReads(false, "forward_plus")).IsFalse();
        AssertThat(Engine_SkyCosts.SdfgiReads(true, "forward_plus")).IsTrue();
        AssertThat(Engine_SkyCosts.SdfgiReads(true, "mobile")).IsFalse();
        bool mobileFog = Engine_SkyCosts.VolumetricFogReads(true, "mobile");
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Color, SkyBackground, false, mobileFog, 0.2f)).IsTrue();
    }

    /// <summary>
    /// The fable model on a phone: ambient and reflections off, no SDFGI, volumetric fog with no
    /// ambient energy to inject: nothing reads the map. The modern model's colour ambient does not
    /// either, but its SDFGI does.
    /// </summary>
    [TestCase]
    public void TheFableModelReadsNoRadiance()
    {
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, false, false, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, false, true, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Color, SkyBackground, false, false, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Color, SkyBackground, true, false, 0f)).IsFalse();
    }

    /// <summary>Every way Godot samples the map keeps it.</summary>
    [TestCase]
    public void AnyReaderKeepsTheMap()
    {
        AssertThat(Engine_SkyCosts.RadianceUnread(Godot.Environment.ReflectionSource.Sky, NoAmbient, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(Godot.Environment.ReflectionSource.Bg, NoAmbient, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(Godot.Environment.ReflectionSource.Bg, NoAmbient, Godot.Environment.BGMode.Color, false, false, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Sky, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Bg, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, true, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, false, true, 0.5f)).IsFalse();
    }

    /// <summary>Lean only while asked for and unread; otherwise the sky's own size comes back.</summary>
    [TestCase]
    public void TheSizeIsLeanOnlyWhileAskedAndUnread()
    {
        var own = Godot.Sky.RadianceSizeEnum.Size256;
        AssertThat(Engine_SkyCosts.RadianceSizeFor(true, true, own)).IsEqual(Godot.Sky.RadianceSizeEnum.Size32);
        AssertThat(Engine_SkyCosts.RadianceSizeFor(true, false, own)).IsEqual(own);
        AssertThat(Engine_SkyCosts.RadianceSizeFor(false, true, own)).IsEqual(own);
        AssertThat(Engine_SkyCosts.LeanRadianceSize).IsEqual(Godot.Sky.RadianceSizeEnum.Size32);
    }
}
