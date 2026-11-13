using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Launcher.Recompile;
using Refii.Mods;

namespace Refii.AndroidRuntime;

/// <summary>
/// Hard mods on a phone: the enabled, consented mods' native sources, compiled into the game module
/// in the phone's own build by the desktop's steps (<see cref="GameCodeBuild"/>).
/// </summary>
/// <remarks>
/// Refii's own hooks are read from the bundle's hook sources the build compiles, as the desktop
/// reads its staged ones. Withheld mods (enabled without consent) are named in the build's log.
/// </remarks>
public static class AndroidHardMods
{
    /// <summary>The build step: <see cref="AndroidPhoneCompiler.AddMods"/> for this phone's mods folder.</summary>
    /// <param name="log">Where the build's lines about the mods go.</param>
    public static AndroidPhoneCompiler.AddMods ForBuild(Action<string> log) => (generated, config, hooks) =>
    {
        List<ModNativeSources> mods = GameCodeBuild.NativeModsForBuild(out List<string> withheld);
        var sources = new List<string>();
        var arguments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        GameCodeBuild.Result added = GameCodeBuild.AddNativeModSources(mods, withheld, () => RefiiOverrides(hooks),
            Refii.Assets.Engine_GameAssets.ModsRoot, generated, config, Path.Combine(generated, "mods"),
            sources, arguments, hooks, log, out string hash);
        if (!added.Ok) throw new InvalidDataException(added.Error);
        return new AndroidPhoneCompiler.ModSources(sources, arguments, hash);
    };

    /// <summary>Refii's own hooks, by function, from the hook sources a build compiles.</summary>
    /// <param name="hooks">Hook source paths.</param>
    public static Dictionary<string, string> RefiiOverrides(IEnumerable<string> hooks)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string hook in hooks)
            foreach (string function in ModNative.DeriveOverrides(File.ReadAllText(hook)))
                result[function] = ModNative.RefiiOwner;
        return result;
    }

    /// <summary>The build's lines about mods, in the Godot log.</summary>
    public static void Log(string text) => GD.Print("[hard-mods] " + text);
}
