namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>Hard mods in the phone's build: GameCodeBuild's shared step run on the phone's own folders.</summary>
[TestSuite]
public class AndroidHardModsTests
{
    /// <summary>The phone's build plans a hard mod from its own attempt's folders, as the desktop's does:
    /// the mod's files extracted under the generated code, its own arguments, the chain when refii hooks the
    /// same function, and a refusal naming the config's name for an unknown <c>sub_</c>.</summary>
    [TestCase]
    public void PhoneBuildPlansHardModsFromItsOwnFolders()
    {
        string root = Path.Combine(Refii.Tests.TestTemp.Root, "hard-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            string generated = Path.Combine(root, "generated");
            Directory.CreateDirectory(generated);
            File.WriteAllText(Path.Combine(generated, "refii_init.h"), "DECLARE_REX_FUNC(rex_StaminaGate)\n");
            string config = Path.Combine(root, "refii_config.toml");
            // The phone's mods folder, as AndroidHardMods passes Engine_GameAssets.ModsRoot: none installed here.
            string modsRoot = Path.Combine(root, "mods-root");
            File.WriteAllText(config, "0x826F57E8 = { name = \"rex_Named\" }\n");
            string hook = Path.Combine(root, "hooks", "gate.cpp");
            Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
            File.WriteAllText(hook, "REX_EXTERN(rex_StaminaGate) {\n  __imp__rex_StaminaGate(ctx, base);\n}\n");
            var refii = Refii.AndroidRuntime.AndroidHardMods.RefiiOverrides(new[] { hook });
            AssertThat(refii["rex_StaminaGate"]).IsEqual(ModNative.RefiiOwner);

            var mod = new ModNativeSources { ModName = "souls", ContentHash = "AB", Overrides = { "rex_StaminaGate" } };
            mod.Files.Add(new ModNativeFile("native/gate.cpp", Encoding.UTF8.GetBytes(
                "REX_EXTERN(rex_StaminaGate) {\n  __imp__rex_StaminaGate(ctx, base);\n}\n")));
            var sources = new List<string>();
            var arguments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var lines = new List<string>();
            var planned = Refii.Launcher.Recompile.GameCodeBuild.AddNativeModSources(new List<ModNativeSources> { mod },
                new[] { "unconsented" }, () => refii, modsRoot, generated, config, Path.Combine(generated, "mods"),
                sources, arguments, new[] { hook }, lines.Add, out string hash);
            AssertThat(planned.Ok).IsTrue();
            string extracted = Path.Combine(generated, "mods", "souls", "native", "gate.cpp");
            AssertThat(File.Exists(extracted)).IsTrue();
            AssertThat(sources).Contains(extracted, Path.Combine(generated, "mods", "refii_mod_chains.cpp"));
            AssertThat(arguments[extracted]).Contains("-DREFII_MOD_NAME=\"souls\"");
            AssertThat(arguments.ContainsKey(hook)).IsTrue();
            AssertThat(hash).IsEqual(ModNative.HashSet(new[] { mod }));
            AssertThat(lines.Any(line => line.Contains("unconsented", StringComparison.Ordinal))).IsTrue();

            var unknown = new ModNativeSources { ModName = "bad", ContentHash = "CD", Overrides = { "sub_826F57E8" } };
            var refused = Refii.Launcher.Recompile.GameCodeBuild.AddNativeModSources(new List<ModNativeSources> { unknown },
                Array.Empty<string>(), () => refii, modsRoot, generated, config, Path.Combine(generated, "mods"), new List<string>(),
                new Dictionary<string, List<string>>(StringComparer.Ordinal), new[] { hook }, _ => { }, out _);
            AssertThat(refused.Ok).IsFalse();
            AssertThat(refused.Error).Contains("rex_Named");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>With no hard mod (most builds) the step logs the withheld and returns before refii's own hooks are read,
    /// as the per-title build did before it took the phone's folders.</summary>
    [TestCase]
    public void NoHardModReadsNoHooks()
    {
        var lines = new List<string>();
        var added = Refii.Launcher.Recompile.GameCodeBuild.AddNativeModSources(new List<ModNativeSources>(),
            new[] { "unconsented" }, () => throw new InvalidOperationException("the hooks were read"), "", "", "", "",
            new List<string>(), new Dictionary<string, List<string>>(StringComparer.Ordinal), Array.Empty<string>(), lines.Add, out string hash);
        AssertThat(added.Ok).IsTrue();
        AssertThat(hash).IsEmpty();
        AssertThat(lines.Any(line => line.Contains("unconsented", StringComparison.Ordinal))).IsTrue();
    }
}
