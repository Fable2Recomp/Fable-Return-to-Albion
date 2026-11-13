using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_near_fog` (<see cref="AndroidNearFog"/>): no near-fog cancel where no volumetric fog draws.</summary>
[TestSuite]
public sealed class AndroidNearFogTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>The cancel is published only where Godot draws the volumetric fog it makes room for.</summary>
    [TestCase]
    public void TheCancelNeedsAVolumetricRenderer()
    {
        const float density = 0.0031f;
        AssertThat(AndroidNearFog.Published(density, keepNearFog: true, rendererDrawsVolumetricFog: false)).IsEqual(0f);
        AssertThat(AndroidNearFog.Published(density, keepNearFog: true, rendererDrawsVolumetricFog: true)).IsEqual(density);
        AssertThat(AndroidNearFog.Published(density, keepNearFog: false, rendererDrawsVolumetricFog: false)).IsEqual(density);
    }

    /// <summary>Forward+ alone draws volumetric fog; Mobile (every phone) and Compatibility do not.</summary>
    [TestCase]
    public void OnlyForwardPlusDrawsIt()
    {
        AssertThat(AndroidNearFog.DrawsVolumetricFog("forward_plus")).IsTrue();
        AssertThat(AndroidNearFog.DrawsVolumetricFog("mobile")).IsFalse();
        AssertThat(AndroidNearFog.DrawsVolumetricFog("gl_compatibility")).IsFalse();
    }

    /// <summary>The desktop publishes the density as it did.</summary>
    [TestCase]
    public void TheDesktopIsUntouched()
    {
        AssertThat(AndroidNearFog.Published(0.0031f)).IsEqual(0.0031f);
        AssertThat(AndroidNearFog.Published(0f)).IsEqual(0f);
    }

    /// <summary>The hook wraps the one publish, and the shaders still read that global.</summary>
    [TestCase]
    public void TheHookWrapsThePublish()
    {
        string lighting = FileText("Godot/src/Engine/Lighting/Engine_LevelLighting.cs");
        AssertThat(lighting).Contains(
            "LastVolumetricFogEnd.Set(FableVolumetricFogEndName, Refii.AndroidRuntime.AndroidNearFog.Published(volBaseDensity));");
        AssertThat(lighting.Split("(FableVolumetricFogEndName").Length - 1).IsEqual(1);
        AssertThat(FileText("Godot/src/Engine/level/environment/fable_atmosphere.gdshaderinc"))
            .Contains("global uniform float fable_volumetric_fog_end");
    }

    /// <summary>A phone Tuning row, on by default, with a player's name.</summary>
    [TestCase]
    public void TheKeyIsRegisteredOn()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidNearFog.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("true");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidNearFog.Key)).IsTrue();
    }
}
