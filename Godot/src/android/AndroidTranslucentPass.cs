using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_translucent_single_pass`: a CHARACTER's translucent surface (diffuse header flag 0x80:
/// hair cards, lace, veils) drawn in one pass on a phone instead of the title's two.
/// </summary>
/// <remarks>
/// THE TWO PASSES (Engine_MdlMaterial.TryCreate). The opaque pass, two-sided and alpha tested
/// GREATER 254, writes depth; the blended pass (standard_material_translucent, depth writes off,
/// two-sided) draws the whole card again, fully lit, over it. Hair pays two lit two-sided passes,
/// a blend, and a discard that already costs the draw its early depth (LRZ on Adreno).
///
/// THE PASS THAT STAYS IS THE ALPHA-TESTED ONE, cut at the title's own cutout reference (GREATER
/// 128, Engine_MdlMaterial.CutoutAlpha) instead of 254. It still writes depth and casts its shadow,
/// it sorts like every other surface of the body, and it is the plain two-sided standard shader,
/// so the lite set maps it like any cutout (Engine_ShaderVariant.ModelPath, StandardPath) and the
/// body's surface merge (Engine_BodyVariants) may join it. The blended pass alone would write no
/// depth, cast no shadow and order a hair mesh's cards by index rather than distance: its inner
/// layers over its outer ones, and every later transparent draw over it (the child hero's boots,
/// flags 0xF0, showed the cobbles through them when this pass drew alone). Its cost is aliased
/// card edges and the thinnest wisps (alpha 128 and under) gone.
///
/// CHARACTERS ONLY: models with skinned parts. World glass (windows, lamp panes) is a sheet of
/// even, partial alpha, which a cutoff would draw either solid or not at all, so it keeps both
/// passes. A model that is part skinned is all one model here.
///
/// Read once at the first material build; a relaunch changes it. Nothing on the desktop.
/// </remarks>
public static class AndroidTranslucentPass
{
    /// <summary>The refii.toml key; off while it is measured.</summary>
    public const string Key = "android_translucent_single_pass";

    /// <summary>Whether the configuration turns it on.</summary>
    /// <param name="values">The runtime configuration's values.</param>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>Whether a surface draws its alpha-tested pass alone. Pure.</summary>
    /// <param name="on">The key is on, on a phone.</param>
    /// <param name="translucent">The surface's diffuse is translucent (header flag 0x80).</param>
    /// <param name="skinnedParts">The model's skinned part count: nonzero for a character.</param>
    public static bool Decide(bool on, bool translucent, uint skinnedParts) => on && translucent && skinnedParts != 0;

    /// <summary>
    /// The shared material key's part for this choice: empty while it is off, so every key is as
    /// it was; otherwise whether the model is a character, since one record draws one way on a
    /// body and the other in the world. Pure.
    /// </summary>
    public static string KeyPart(bool on, uint skinnedParts) => !on ? string.Empty : skinnedParts != 0 ? "c" : "w";

    private static readonly Lazy<bool> On = new(Read, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool Read()
    {
        if (!Engine_Platform.IsAndroid)
        {
            return false;
        }

        bool on;
        try
        {
            on = Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            on = false;
        }

        GD.Print($"[material] {Key}={on}: characters' translucent surfaces draw " +
            (on ? "one alpha-tested pass (relaunch to change)" : "both passes"));
        return on;
    }

    /// <summary>Whether this model's translucent surface draws its alpha-tested pass alone (Engine_MdlMaterial.TryCreate).</summary>
    public static bool SinglePass(bool translucent, Refii.Models.MdlInfo? info) =>
        translucent && Decide(On.Value, translucent, info?.SkinnedCount ?? 0);

    /// <summary>This model's part of the shared material key (Engine_MdlMaterial.TryCreateShared).</summary>
    /// <remarks>Also split while `android_character_cull_back` is on (AndroidCharacterCull), or a character build
    /// (AndroidCharacterShading), which draw a body's copy differently too, and while a prop lighting shortcut or
    /// cut-out pre-pass is on (AndroidVertexLitProps, AndroidFlatCutouts, AndroidCutoutPrepass), which a body's copy
    /// does not take.</remarks>
    public static string KeyPart(Refii.Models.MdlInfo info) =>
        KeyPart(On.Value || AndroidCharacterCull.On || AndroidCharacterShading.On.Any ||
            AndroidVertexLitProps.On || AndroidFlatCutouts.On || AndroidFlatCutouts.SwayingOn ||
            AndroidCutoutPrepass.On, info.SkinnedCount);
}
