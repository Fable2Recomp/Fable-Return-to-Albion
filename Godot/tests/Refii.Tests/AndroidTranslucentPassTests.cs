using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_translucent_single_pass` (AndroidTranslucentPass): which translucent surfaces draw one pass.</summary>
[TestSuite]
public sealed class AndroidTranslucentPassTests
{
    /// <summary>Off unless the configuration turns it on.</summary>
    [TestCase]
    public void OffByDefault()
    {
        AssertThat(AndroidTranslucentPass.Wanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidTranslucentPass.Wanted(new Dictionary<string, string> { [AndroidTranslucentPass.Key] = "true" })).IsTrue();
        AssertThat(AndroidTranslucentPass.Wanted(new Dictionary<string, string> { [AndroidTranslucentPass.Key] = "false" })).IsFalse();
    }

    /// <summary>Only a translucent surface of a model with skinned parts, and only with the key on.</summary>
    [TestCase]
    public void OnlyCharactersTranslucentSurfaces()
    {
        AssertThat(AndroidTranslucentPass.Decide(on: true, translucent: true, skinnedParts: 4)).IsTrue();
        AssertThat(AndroidTranslucentPass.Decide(on: false, translucent: true, skinnedParts: 4)).IsFalse();
        AssertThat(AndroidTranslucentPass.Decide(on: true, translucent: false, skinnedParts: 4)).IsFalse();
        // World glass: a rigid model keeps both passes.
        AssertThat(AndroidTranslucentPass.Decide(on: true, translucent: true, skinnedParts: 0)).IsFalse();
    }

    /// <summary>Off, the shared material keys are as they were; on, a body's and the world's copy of one record differ.</summary>
    [TestCase]
    public void KeyPartSeparatesBodiesFromTheWorldOnlyWhenOn()
    {
        AssertThat(AndroidTranslucentPass.KeyPart(false, 4)).IsEqual(string.Empty);
        AssertThat(AndroidTranslucentPass.KeyPart(false, 0)).IsEqual(string.Empty);
        AssertThat(AndroidTranslucentPass.KeyPart(true, 4)).IsNotEqual(AndroidTranslucentPass.KeyPart(true, 0));
        AssertThat(AndroidTranslucentPass.KeyPart(true, 0)).IsNotEqual(string.Empty);
    }

    /// <summary>Off a phone nothing changes, whatever the model.</summary>
    [TestCase]
    public void TheDesktopKeepsBothPasses()
    {
        if (Refii.Core.Engine_Platform.IsAndroid)
        {
            return;
        }

        var body = new MdlInfo { SkinnedCount = 3 };
        AssertThat(AndroidTranslucentPass.SinglePass(true, body)).IsFalse();
        AssertThat(AndroidTranslucentPass.KeyPart(body)).IsEqual(string.Empty);
    }

    /// <summary>The pass that stays, the two-sided standard shader, has lite builds to map to.</summary>
    [TestCase]
    public void TheRemainingPassHasLiteBuilds() =>
        AssertThat(Engine_ShaderVariant.AllVariantPaths.Any(path =>
            Engine_ShaderVariant.FullPathOf(path) == Engine_ShaderFamily.StandardPath)).IsTrue();

    /// <summary>A launcher row, off by default, with a phone name.</summary>
    [TestCase]
    public void RegisteredOffWithAPhoneName()
    {
        RuntimeCVarDefinition row = RuntimeCVarSet.All.First(d => d.Name == AndroidTranslucentPass.Key);
        AssertThat(row.DefaultValue).IsEqual("false");
        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            AssertThat(AndroidSettingNames.Name(AndroidTranslucentPass.Key)).IsNotNull();
        }
        finally { GraphicsPresets.Offered = offered; }
    }
}
