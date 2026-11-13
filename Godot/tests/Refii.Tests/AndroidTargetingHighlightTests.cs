using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_targeting_highlight` (<see cref="AndroidTargetingHighlight"/>): the phone's highlight passes, on unless turned off.</summary>
[TestSuite]
public sealed class AndroidTargetingHighlightTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>The phone composites only with the setting on and the post chain not compositing it.</summary>
    [TestCase]
    public void CompositesOnlyWhenOnAndNeeded()
    {
        AssertThat(AndroidTargetingHighlight.Composites(enabled: true, postChainComposites: false)).IsTrue();
        AssertThat(AndroidTargetingHighlight.Composites(enabled: false, postChainComposites: false)).IsFalse();
        AssertThat(AndroidTargetingHighlight.Composites(enabled: true, postChainComposites: true)).IsFalse();
    }

    /// <summary>The gate is the phone composite's own, which the highlight layer's activity reads.</summary>
    [TestCase]
    public void TheHookIsTheCompositeGate()
    {
        string mobile = FileText("Godot/src/android/Engine_MobileHighlight.cs");
        AssertThat(mobile).Contains("public static bool Composites => Refii.AndroidRuntime.AndroidTargetingHighlight.Composites(");
        string layer = FileText("Godot/src/Engine/entities/Engine_EntityHighlightLayer.cs");
        AssertThat(layer).Contains("Engine_MobileHighlight.Composites");
    }

    /// <summary>A phone Tuning row, on by default (the picture as it is), with a player's name.</summary>
    [TestCase]
    public void TheKeyIsRegisteredOn()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidTargetingHighlight.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("true");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidTargetingHighlight.Key)).IsTrue();
    }
}
