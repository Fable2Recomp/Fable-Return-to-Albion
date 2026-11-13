using System;
using Refii.Core;
using Refii.Models;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_light_table_packed`: one texel fetch per light, not two, in the phone's lite shaders.
/// <c>fable_attenuation</c> runs for every light reaching a pixel, 3-7 times per pixel on a phone, and
/// read rows 0 (position, Godot's range) and 1 (the stated range) of <c>fable_light_table</c> as two
/// dependent fetches.
/// </summary>
/// <remarks>
/// <para>ROW 2 CARRIES IT, NOT ROW 0. Row 0's w is read as a float range by every shader that does not
/// go through <see cref="Engine_ShaderVariant.Compose"/>: fur shells, the guest particles and trails,
/// the classic Phong shader, and the full shaders a lite process still draws (tree and wind surfaces
/// with an environment cube). Rows 0 and 1 therefore stay exactly as they are. Row 2 is the baked lamp
/// shadow's (layer, 0, 0, 0), and with packing on the table writes no atlas rect, so every unpacked
/// reader's shadow lookup returns before it reads the layer. Packed, row 2 is
/// (x, y, z, half2(range, stated)): same texture, same upload.</para>
/// <para>ON ONLY WHERE THE SHADERS KNOW IT. Packing needs the lite surface shaders with the lamp-shadow
/// lookup compiled out (<see cref="Engine_ShaderVariant.NoStaticShadowsOn"/>), and the lite builds
/// get <see cref="Define"/> from this same <see cref="On"/>, so the table and the shaders always agree.
/// Read once, as the variant is: changing the key takes a relaunch, which is how it is A/B'd.</para>
/// <para>THE WORD. Low 16 bits: Godot's range as a half; high 16 bits: the stated range as a half
/// (the range when none is stated). Both are clamped to finite, normal halves, so the float the word
/// reads as is never a NaN, an infinity or a denormal a GPU could change on the way through.</para>
/// </remarks>
public static class AndroidLightTablePacking
{
    /// <summary>The refii.toml setting. On unless set false.</summary>
    public const string Key = "android_light_table_packed";

    /// <summary>The define that has <c>fable_attenuation</c> read the packed row.</summary>
    public const string Define = "FABLE_LITE_ONE_ROW_LIGHT";

    /// <summary>The table row the packed record is in.</summary>
    public const int Row = 2;

    /// <summary>Largest finite half.</summary>
    private const float HalfMax = 65504f;

    /// <summary>Smallest normal half: keeps the high half, and so the word's float, out of the denormals.</summary>
    private const float HalfMinNormal = 6.1035156e-5f;

    private static readonly Lazy<bool> Chosen = new(Choose, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process writes the packed row and its lite shaders read it.</summary>
    public static bool On => Chosen.Value;

    /// <summary>Whether the configuration leaves the key on.</summary>
    /// <param name="values">The runtime configuration's values.</param>
    public static bool Wanted(System.Collections.Generic.IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, true);

    /// <summary>The policy: lite shaders, with the lamp-shadow lookup compiled out, and the key on. Pure.</summary>
    public static bool Decide(bool liteShaders, bool noStaticShadow, bool wanted) => liteShaders && noStaticShadow && wanted;

    private static bool Choose()
    {
        if (!Engine_Platform.IsAndroid)
        {
            return false;
        }

        try
        {
            return Decide(
                Engine_ShaderVariant.Lite,
                Engine_ShaderVariant.NoStaticShadowsOn,
                Wanted(Engine_RefiiConfig.Read(out _)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ushort HalfBits(float value, float floor) =>
        BitConverter.HalfToUInt16Bits((Half)Math.Clamp(value, floor, HalfMax));

    /// <summary>Godot's range and the stated range as one word; a stated range of 0 is the range. Pure.</summary>
    public static uint Pack(float range, float stated)
    {
        if (!(range > 0f))
        {
            return 0u;
        }

        float s = stated > 0f ? stated : range;
        return HalfBits(range, HalfMinNormal) | ((uint)HalfBits(s, HalfMinNormal) << 16);
    }

    /// <summary>The (range, stated) a word holds, as the shader's <c>unpackHalf2x16</c> reads it. Pure.</summary>
    public static (float Range, float Stated) Unpack(uint word) => (
        (float)BitConverter.UInt16BitsToHalf((ushort)(word & 0xFFFFu)),
        (float)BitConverter.UInt16BitsToHalf((ushort)(word >> 16)));

    /// <summary>
    /// Writes the packed record (x, y, z, word) at <paramref name="index"/> of <paramref name="data"/>.
    /// True when it changed; the word is compared as its bits.
    /// </summary>
    public static bool Write(float[] data, int index, float x, float y, float z, float range, float stated)
    {
        uint word = Pack(range, stated);
        if (data[index] == x && data[index + 1] == y && data[index + 2] == z &&
            BitConverter.SingleToUInt32Bits(data[index + 3]) == word)
        {
            return false;
        }

        data[index] = x;
        data[index + 1] = y;
        data[index + 2] = z;
        data[index + 3] = BitConverter.UInt32BitsToSingle(word);
        return true;
    }
}
