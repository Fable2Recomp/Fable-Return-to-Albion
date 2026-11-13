using System;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_lean_tree_streams`: a tree surface handed to Godot with CUSTOM1 as four bytes and
/// without CUSTOM2/CUSTOM3. Off by default while measured.
/// </summary>
/// <remarks>
/// WHY. Outside the classic renderer a tree part carries three RGBA-float wind streams
/// (Engine_MdlTreeStreams): 48 bytes a vertex, ~76 with the rest. Only CUSTOM1 (the COLOR1 wind
/// weights, D3DCOLOR / 255) is read, by fable_tree_sway in the vertex stage, which a tile GPU
/// runs once per bin. The sway reads CUSTOM1 as a normalised vec4, so RGBA8_UNORM hands it
/// the same numbers from 4 bytes; the vertex drops to ~32 B.
/// <para>READERS CHECKED. CUSTOM2/3: no non-classic shader reads them for a tree (the tree
/// shader sets FABLE_TREE_WIND, which zeroes v_transfer and never calls fable_transfer; the
/// phong shader reads CUSTOM1/2 as transfer but draws no tree), nor does C# (FableFx's CUSTOM1
/// read is its own mesh). The arrays the builder RECORDS (Engine_MdlSurfaceArrays) stay floats,
/// so every reader of <c>Of</c> and every rebuild through <c>FormatOf</c> sees what it always
/// did; only the copy handed to AddSurfaceFromArrays changes. After <c>Forget</c> a readback
/// returns CUSTOM1 as a PackedByteArray, which <c>FormatOf</c> does not flag, and Godot's
/// default custom format IS RGBA8_UNORM, so a rebuild from it is lean again.</para>
/// </remarks>
public static class AndroidLeanTreeStreams
{
    public const string Key = "android_lean_tree_streams";

    private static bool? _enabled;
    private static bool _hooked;

    /// <summary>Whether the key is on, read once and again on each live change.</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } known) return known;
            bool on = false;
            try
            {
                on = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, false);
            }
            catch (Exception)
            {
                on = false;
            }

            if (!_hooked)
            {
                _hooked = true;
                Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
                {
                    if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase)) _enabled = null;
                };
            }

            _enabled = on;
            return on;
        }
    }

    /// <summary>The decision: only on a phone, only with the key, never under the classic renderer (its shaders read all three).</summary>
    public static bool Wanted(bool android, bool keyOn, bool classicActive) => android && keyOn && !classicActive;

    /// <summary>The decision from the live state.</summary>
    public static bool Wanted() =>
        Refii.Core.Engine_Platform.IsAndroid && Wanted(true, Enabled, Refii.Models.Engine_ClassicMaterial.Active);

    /// <summary>
    /// Floats in 0..1 as RGBA8_UNORM bytes, rounded to nearest: exact for D3DCOLOR / 255, and
    /// clamped otherwise.
    /// </summary>
    public static byte[] ToRgba8(ReadOnlySpan<float> values)
    {
        var bytes = new byte[values.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            float v = values[i];
            bytes[i] = v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)MathF.Round(v * 255f);
        }

        return bytes;
    }

    /// <summary>
    /// A tree surface's arrays by shape: CUSTOM1..3 floats and no CUSTOM0 (a surface carrying
    /// transfer has CUSTOM0, and its CUSTOM1/2 are read as transfer).
    /// </summary>
    public static bool IsTreeShape(Godot.Collections.Array arrays) =>
        arrays.Count > (int)Mesh.ArrayType.Custom3 &&
        arrays[(int)Mesh.ArrayType.Custom0].VariantType == Variant.Type.Nil &&
        arrays[(int)Mesh.ArrayType.Custom1].VariantType == Variant.Type.PackedFloat32Array &&
        arrays[(int)Mesh.ArrayType.Custom2].VariantType == Variant.Type.PackedFloat32Array &&
        arrays[(int)Mesh.ArrayType.Custom3].VariantType == Variant.Type.PackedFloat32Array &&
        arrays[(int)Mesh.ArrayType.Custom1].AsFloat32Array().Length ==
            arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length * 4;

    /// <summary>
    /// The arrays to hand Godot: <paramref name="arrays"/> itself unless this is a tree surface
    /// and <see cref="Wanted()"/>, then a shallow copy with CUSTOM1 as bytes and CUSTOM2/3 gone.
    /// The caller records the original.
    /// </summary>
    public static Godot.Collections.Array ForGodot(Godot.Collections.Array arrays)
    {
        if (!Wanted() || !IsTreeShape(arrays)) return arrays;
        Godot.Collections.Array lean = arrays.Duplicate(false);
        lean[(int)Mesh.ArrayType.Custom1] = ToRgba8(arrays[(int)Mesh.ArrayType.Custom1].AsFloat32Array());
        lean[(int)Mesh.ArrayType.Custom2] = default(Variant);
        lean[(int)Mesh.ArrayType.Custom3] = default(Variant);
        return lean;
    }
}
