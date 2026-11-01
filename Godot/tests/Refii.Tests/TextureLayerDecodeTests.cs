using System;
using GdUnit4;
using Refii.Host;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The texture-layer rows both titles' bridges write, decoded by <see cref="HostBridge.DecodeTextureLayers"/>:
/// Fable 2's kinds 0..2 as they were, Fable 3's kind 3 colour mask folded into the original's composite.
/// </summary>
[TestSuite]
public sealed class TextureLayerDecodeTests
{
    private const int Words = 7;

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    private static uint[] Row(uint original, uint kind, params uint[][] layers)
    {
        var words = new uint[2 + layers.Length * Words];
        words[0] = original;
        words[1] = (uint)layers.Length | (kind << 16);
        for (int i = 0; i < layers.Length; i++)
        {
            Array.Copy(layers[i], 0, words, 2 + i * Words, Words);
        }

        return words;
    }

    private static uint[] Concat(params uint[][] rows)
    {
        int total = 0;
        foreach (uint[] row in rows) total += row.Length;
        var words = new uint[total];
        int at = 0;
        foreach (uint[] row in rows)
        {
            row.CopyTo(words, at);
            at += row.Length;
        }

        return words;
    }

    /// <summary>A Fable 2 replacement row: one opaque layer, kind 0, decoded word for word.</summary>
    [TestCase]
    public void Fable2RowsDecodeAsBefore()
    {
        uint[] words = Concat(
            Row(0x1111u, 0u, new uint[] { 0x2222u, 1u, F(0.75f), 1u, 0x40506070u, F(0.5f), 0u }),
            Row(0x3333u, 1u, new uint[] { 0x4444u, 0u, F(1f), 0u, 0u, 0u, 0x5555u }),
            Row(0u, 2u, new uint[] { 0x6666u, 0u, F(1f), 0u, 0u, 0u, 0u }));

        HostBridge.GuestTextureComposite[] decoded = HostBridge.DecodeTextureLayers(words, (uint)words.Length);

        AssertThat(decoded.Length).IsEqual(3);
        AssertThat(decoded[0].Original).IsEqual(0x1111u);
        AssertThat(decoded[0].Overlay).IsFalse();
        AssertThat(decoded[0].Wildcard).IsFalse();
        AssertThat(decoded[0].Layers.Length).IsEqual(1);
        AssertThat(decoded[0].Layers[0]).IsEqual(
            new HostBridge.GuestTextureLayer(0x2222u, 1, 0.75f, true, 0x40506070u, 0.5f, 0u));
        AssertThat(decoded[1].Overlay).IsTrue();
        AssertThat(decoded[1].Layers[0].Bump).IsEqual(0x5555u);
        AssertThat(decoded[2].Wildcard).IsTrue();
    }

    /// <summary>
    /// A colour mask with no replacement for its original: the original is the base, then each
    /// mask as a masked (mode 4) layer at its strength, the dye colour as its tint.
    /// </summary>
    [TestCase]
    public void ColourMaskAloneGoesOverTheOriginal()
    {
        const uint original = 0xAAAA0001u;
        const uint mask = 0xBBBB0002u;
        uint[] words = Row(original, 3u,
            new uint[] { mask, 0u, F(0.8f), 1u, 0x102030FFu, 0u, 0u },
            new uint[] { mask, 0u, F(0.3f), 1u, 0x405060FFu, 0u, 0u });

        HostBridge.GuestTextureComposite[] decoded = HostBridge.DecodeTextureLayers(words, (uint)words.Length);

        AssertThat(decoded.Length).IsEqual(1);
        AssertThat(decoded[0].Original).IsEqual(original);
        AssertThat(decoded[0].Overlay).IsFalse();
        AssertThat(decoded[0].Layers.Length).IsEqual(3);
        AssertThat(decoded[0].Layers[0]).IsEqual(new HostBridge.GuestTextureLayer(original, 0, 1f, false, 0u, 0f));
        AssertThat(decoded[0].Layers[1]).IsEqual(new HostBridge.GuestTextureLayer(mask, 4, 0.8f, true, 0x102030FFu, 0f));
        AssertThat(decoded[0].Layers[2]).IsEqual(new HostBridge.GuestTextureLayer(mask, 4, 0.3f, true, 0x405060FFu, 0f));
    }

    /// <summary>
    /// The guest issues the replacement and the colour mask under the same original; the mask
    /// layers go over the replacement whichever row the table wrote first.
    /// </summary>
    [TestCase]
    public void ColourMaskFoldsOntoTheReplacementInEitherOrder()
    {
        const uint original = 0xAAAA0001u;
        const uint replacement = 0xCCCC0003u;
        const uint mask = 0xBBBB0002u;
        uint[] replace = Row(original, 0u, new uint[] { replacement, 0u, F(1f), 0u, 0u, 0u, 0u });
        uint[] dye = Row(original, 3u,
            new uint[] { mask, 0u, F(1f), 1u, 0x102030FFu, 0u, 0u },
            new uint[] { mask, 0u, F(0f), 1u, 0x405060FFu, 0u, 0u });
        uint[] other = Row(0xDDDD0004u, 0u, new uint[] { 0xEEEE0005u, 0u, F(1f), 0u, 0u, 0u, 0u });

        foreach (uint[] words in new[] { Concat(replace, dye, other), Concat(other, dye, replace) })
        {
            HostBridge.GuestTextureComposite[] decoded = HostBridge.DecodeTextureLayers(words, (uint)words.Length);

            AssertThat(decoded.Length).IsEqual(2);
            HostBridge.GuestTextureComposite dyed = Array.Find(decoded, c => c.Original == original);
            AssertThat(dyed.Layers.Length).IsEqual(3);
            AssertThat(dyed.Layers[0].Texture).IsEqual(replacement);
            AssertThat(dyed.Layers[0].Mode).IsEqual(0);
            AssertThat(dyed.Layers[1].Texture).IsEqual(mask);
            AssertThat(dyed.Layers[1].Mode).IsEqual(4);
            AssertThat(dyed.Layers[1].HasTint).IsTrue();
            AssertThat(dyed.Layers[2].Tint).IsEqual(0x405060FFu);
            HostBridge.GuestTextureComposite plain = Array.Find(decoded, c => c.Original == 0xDDDD0004u);
            AssertThat(plain.Layers.Length).IsEqual(1);
        }
    }

    /// <summary>A colour mask never folds onto an overlay or a wildcard swap of the same original.</summary>
    [TestCase]
    public void ColourMaskSkipsOverlaysAndWildcards()
    {
        const uint original = 0xAAAA0001u;
        uint[] words = Concat(
            Row(original, 1u, new uint[] { 0x1u, 0u, F(1f), 0u, 0u, 0u, 0x2u }),
            Row(original, 3u, new uint[] { 0xBu, 0u, F(1f), 1u, 0xFFu, 0u, 0u }, new uint[] { 0xBu, 0u, F(1f), 1u, 0xFFu, 0u, 0u }));

        HostBridge.GuestTextureComposite[] decoded = HostBridge.DecodeTextureLayers(words, (uint)words.Length);

        AssertThat(decoded.Length).IsEqual(2);
        AssertThat(decoded[0].Overlay).IsTrue();
        AssertThat(decoded[0].Layers.Length).IsEqual(1);
        AssertThat(decoded[1].Overlay).IsFalse();
        AssertThat(decoded[1].Layers.Length).IsEqual(3);
        AssertThat(decoded[1].Layers[0].Texture).IsEqual(original);
    }

    /// <summary>
    /// One index for both titles: the guest's texture id is FNV-1 of the lower-cased, backslashed
    /// path (Fable 3 `sub_8312F3D8`), the same hash a format-4 bank keys its records by, so a
    /// Fable 3 mount entry resolves the live id its HUD reported (C51DF0BA, a type-72 icon).
    /// </summary>
    [TestCase]
    public void Fable3BankEntryResolvesTheGuestsTextureId()
    {
        const string entry = "art/gui_memmount/hudassests/version4/upperelements/followercollection/guildseal_gold.tex";
        const string spelled = "art\\GUI_MemMount\\HUDAssests\\Version4\\UpperElements\\FollowerCollection\\guildseal_gold.tex";

        AssertThat(Engine_TexPathIndex.PathHash(spelled)).IsEqual(0xC51DF0BAu);
        AssertThat(Engine_TexPathIndex.PathHash(entry)).IsEqual(0xC51DF0BAu);
        AssertThat(Engine_TexPathIndex.PathHash(entry)).IsEqual(Refii.Core.Engine_GameHash.ResourcePath(entry));

        Engine_TexPathIndex.SetPathSource(() => new[] { entry, "art/characters/heros/morphs/diffuse/male/legs/blend_morality3_primal0.tex" });
        try
        {
            // Paths registered by RegisterPath are static and outlive a source change, so the
            // count is not asserted — what the source contributes is.
            AssertThat(Engine_TexPathIndex.Resolve(0xC51DF0BAu)).IsEqual(entry);
            AssertThat(Engine_TexPathIndex.Resolve(Engine_TexPathIndex.PathHash(
                "art/characters/heros/morphs/diffuse/male/legs/blend_morality3_primal0.tex")))
                .IsEqual("art/characters/heros/morphs/diffuse/male/legs/blend_morality3_primal0.tex");
            AssertThat(Engine_TexPathIndex.Resolve(Engine_TexPathIndex.NullHash)).IsNull();
        }
        finally
        {
            Engine_TexPathIndex.SetPathSource(null);
        }
    }
}
