using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// A CHARACTER's lite surfaces (a model with skinned parts: the body, its skinned clothes, hair and
/// fringes) on builds of their own, with what no character uses compiled out and, by choice, the
/// cheaper ways of drawing what it does use: four settings, read once at the first material build.
/// </summary>
/// <remarks>
/// <para>WHY. On the S10e at the Bowerstone market by day, the ten people in view cost about 2 of 15.4
/// Mcycles a frame when only their drawing is taken away (`shaderCeiling` `characters: nodraw`), while
/// 370K rigid-prop triangles hidden cost nothing. A townsperson at 40% resolution is a few thousand
/// triangles over a few hundred pixels: nearly every triangle is a 2x2 quad of its own, so a character
/// pays its pixel shader and its interpolants per triangle more than per pixel, and its vertex stage
/// per pixel too. On an Adreno 640 the pixel stage's cost steps with the interpolated components it
/// reads (at most ~16 is the cheap side; AndroidLeanVaryings). A character's lite build under the S10e's
/// settings (Low + modern lighting) reads 23: Godot's position 3, normal 3, tangent 3, binormal 3, UV 2,
/// UV2 2, COLOR 4 (the self-AO in a, the ground height in r) and fable_world 3; its vertex stage runs 376
/// SPIR-V instructions (425 two-sided) and hands on 27 components.</para>
/// <para>`android_character_lite` (off): EXACT. FABLE_SPEC_CHARACTER (fable_lite_varyings): no UV2 (a
/// skinned model carries none, Engine_MdlMesh, so the coordinate is Godot's constant zero) and no
/// tree band or arrival fade (an entity states neither: GameEntities applies its draw range without a
/// band and Engine_PropFade dresses props only), and, under the modern model, FABLE_SPEC_BAKE_NONE: an
/// entity's bake mode is the probe (1) or none (0), never amb3, and under the modern model every mode
/// but amb3 is the same picture, so the amb3 fetch goes. Two components, the two-sided builds' fade
/// discard and 56 vertex instructions fewer. Also, where the low-memory tier folds no material flags
/// (`lite_material_folds_low_ram = "off"`, no longer the default), a character's builds take the flags that split no build
/// (FoldScope.Const), since they are builds of their own already.</para>
/// <para>`android_character_normal_maps` (`full`): `fast` gives characters `lite_normal_maps = "fast"`
/// (FABLE_LITE_NM_DERIV): the tangent frame from screen derivatives, flat across each triangle, so the
/// vertex stage drops the tangent decode and the pixel reads 6 components fewer. With the exact set a
/// character reads 15, under the step. The Low (lean) preset (revisions 17-18) drew everything so.</para>
/// <para>`android_character_fragment_world` (off): `lite_prop_fragment_world` for characters, the world
/// position rebuilt per pixel from Godot's (12 instructions for 3 components): 12 read. The same picture to
/// float rounding.</para>
/// <para>`android_character_ground_ao` (`near`): the contact shadow at a character's feet. `origin`: the
/// per-vertex gate of `lite_ground_ao = "near"` is the vertex's height above the model's origin instead of
/// four taps of the floor map (FABLE_SPEC_GROUND_FROM_ORIGIN): 151 of the vertex stage's 369 instructions and
/// its four fetches go, and a pixel within the reach takes the same exact taps. Different only where a floor
/// stands 0.58 m or more above the character's feet within a texel of a pixel more than a metre up (a ledge
/// at chest height). `off`: characters take no contact shadow (FABLE_LITE_NO_PROP_GROUND_AO), for pricing it.</para>
/// <para>Only models with skinned parts (MdlInfo.SkinnedCount, as <see cref="AndroidCharacterCull"/>), keyed
/// apart in the shared material cache (<see cref="AndroidTranslucentPass.KeyPart(Refii.Models.MdlInfo)"/>), so
/// the world's props keep their builds. Each set is a build of each character surface shader more; their
/// pipelines compile at first sight as the other specialisations' do. Nothing on the desktop.</para>
/// </remarks>
public static class AndroidCharacterShading
{
    public const string LiteKey = "android_character_lite";
    public const string NormalMapsKey = "android_character_normal_maps";
    public const string FragmentWorldKey = "android_character_fragment_world";
    public const string GroundAoKey = "android_character_ground_ao";

    /// <summary>The character build's marker (fable_lite_varyings.gdshaderinc).</summary>
    public const string Define = "FABLE_SPEC_CHARACTER";

    /// <summary>The near ground AO's per-vertex gate from the model's origin (fable_lite_varyings.gdshaderinc).</summary>
    public const string GroundFromOriginDefine = "FABLE_SPEC_GROUND_FROM_ORIGIN";

    /// <summary>The modern model's bake-mode fold a character build takes (fable_lighting.gdshaderinc).</summary>
    public const string BakeNoneDefine = "FABLE_SPEC_BAKE_NONE";

    /// <summary>`android_character_ground_ao`'s values.</summary>
    public enum GroundAo
    {
        /// <summary>As every other model draws it (`lite_ground_ao`).</summary>
        Near,

        /// <summary>The near gate from the height above the model's origin.</summary>
        Origin,

        /// <summary>No contact shadow on characters.</summary>
        Off,
    }

    /// <summary>The four settings.</summary>
    public readonly record struct Levers(bool Lite, bool FastNormalMaps, bool FragmentWorld, GroundAo Ground)
    {
        /// <summary>Whether any of them changes a character's build.</summary>
        public bool Any => Lite || FastNormalMaps || FragmentWorld || Ground != GroundAo.Near;
    }

    /// <summary>Unset: every character drawn as the props are.</summary>
    public static readonly Levers Defaults = new(false, false, false, GroundAo.Near);

    /// <summary>What the process already draws everything with, so a character build adds only what differs.</summary>
    public readonly record struct Process(bool NoTangentFrame, bool PropFragmentWorld, bool NearGroundAo, bool GroundAoLive);

    /// <summary>`android_character_ground_ao`'s value; anything unknown is <see cref="GroundAo.Near"/>. Pure.</summary>
    public static GroundAo ParseGround(string value) => value.Trim().Trim('"').ToLowerInvariant() switch
    {
        "origin" => GroundAo.Origin,
        "off" => GroundAo.Off,
        _ => GroundAo.Near,
    };

    /// <summary>The settings the values ask for; an unset key is its default. Pure.</summary>
    public static Levers Wanted(IReadOnlyDictionary<string, string> values) => new(
        Engine_RefiiConfig.Bool(values, LiteKey, Defaults.Lite),
        Engine_RefiiConfig.String(values, NormalMapsKey, "full").Trim().Trim('"').Equals("fast", StringComparison.OrdinalIgnoreCase),
        Engine_RefiiConfig.Bool(values, FragmentWorldKey, Defaults.FragmentWorld),
        ParseGround(Engine_RefiiConfig.String(values, GroundAoKey, "near")));

    /// <summary>Whether a model's surfaces take a character build: a setting on and skinned parts. Pure.</summary>
    public static bool Decide(Levers levers, uint skinnedParts) => levers.Any && skinnedParts != 0;

    /// <summary>
    /// A character pass's defines, in order, for a lite build of the modern model (<paramref name="modernBuild"/>)
    /// or the fable one: only what the process does not state already, so a setting that changes nothing
    /// splits no build. Pure.
    /// </summary>
    public static string[] DefinesFor(Levers levers, bool modernBuild, Process process)
    {
        var defines = new List<string>(5);
        if (levers.Lite)
        {
            defines.Add(Define);
            if (modernBuild) defines.Add(BakeNoneDefine);
        }

        if (levers.FastNormalMaps && !process.NoTangentFrame) defines.Add("FABLE_LITE_NM_DERIV");
        if (levers.FragmentWorld && !process.PropFragmentWorld) defines.Add(AndroidLeanVaryings.PropFragmentWorldDefine);
        if (levers.Ground == GroundAo.Origin && process.NearGroundAo) defines.Add(GroundFromOriginDefine);
        if (levers.Ground == GroundAo.Off && process.GroundAoLive) defines.Add(AndroidGroundShading.NoPropDefine);
        return defines.ToArray();
    }

    /// <summary>
    /// The material folds a character's pass takes: the process's, but at least those that split no build
    /// (Const) under the exact set, where the process folds none. Pure.
    /// </summary>
    public static Refii.Models.Engine_ShaderSpecialisation.FoldScope FoldScopeFor(
        Refii.Models.Engine_ShaderSpecialisation.FoldScope process, Levers levers) =>
        levers.Lite && process == Refii.Models.Engine_ShaderSpecialisation.FoldScope.Off
            ? Refii.Models.Engine_ShaderSpecialisation.FoldScope.Const
            : process;

    private static readonly Lazy<Levers> Chosen =new(Read, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's settings: the defaults off a phone or without the lite shaders.</summary>
    public static Levers On => Chosen.Value;

    /// <summary>Whether this model's surfaces take a character build (Engine_MdlMaterial.ApplyShaderVariant).</summary>
    public static bool For(Refii.Models.MdlInfo? info) => Decide(On, info?.SkinnedCount ?? 0);

    /// <summary>The defines a character pass on the lite build <paramref name="path"/> takes in this process.</summary>
    public static string[] Defines(string path) =>
        DefinesFor(On, Refii.Models.Engine_ShaderVariant.FoldsAmbient(path), ProcessNow());

    private static Process ProcessNow()
    {
        var ground = new HashSet<string>(AndroidGroundShading.ProcessDefines(), StringComparer.Ordinal);
        AndroidLeanVaryings.Levers varyings = AndroidLeanVaryings.On;
        return new Process(
            NoTangentFrame: Refii.Models.Engine_ShaderVariant.NormalMapsOn != Refii.Models.Engine_ShaderVariant.NormalMapPath.Full &&
                Refii.Models.Engine_ShaderVariant.NormalMapsOn != Refii.Models.Engine_ShaderVariant.NormalMapPath.Direct,
            PropFragmentWorld: varyings.PropFragmentWorld || varyings.FragmentWorld,
            NearGroundAo: ground.Contains(AndroidGroundShading.NearDefine),
            GroundAoLive: (ground.Contains(AndroidGroundShading.NearDefine) || ground.Contains(AndroidGroundShading.VertexDefine)) &&
                !ground.Contains(AndroidGroundShading.NoPropDefine));
    }

    private static Levers Read()
    {
        if (!Engine_Platform.IsAndroid || !Refii.Models.Engine_ShaderVariant.Lite)
        {
            return Defaults;
        }

        Levers levers;
        try
        {
            levers = Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            levers = Defaults;
        }

        if (levers.Any)
        {
            GD.Print($"[material] character builds: {LiteKey}={levers.Lite}, {NormalMapsKey}={(levers.FastNormalMaps ? "fast" : "full")}, " +
                $"{FragmentWorldKey}={levers.FragmentWorld}, {GroundAoKey}={levers.Ground.ToString().ToLowerInvariant()} (relaunch to change)");
        }

        return levers;
    }
}
