using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The repeated family's wind (`sub_82224578`, the WIND1 vertex shaders): per draw VS c25
/// g_SwaySpeed and c26 g_WindFactors; the frame carries c21..c22 g_WindDirections.
/// </summary>
/// <remarks>
/// A mesh sways when its header's movement byte (.mdl +77) is 1; that byte is also the
/// permutation's NormalParams_Wind (`sub_8222B600` 0x8222B6C4 -> `sub_82B35990`).
///   c25 = splat(the mesh's speed, .mdl +84)
///   c26 = 100 * (s, s, 2s, 0.2s) / (|400 * windDir|, |T1|, |T2|, |T3|) + 1e-10 each, T the
///         static rows at 0x834A1240 (T1 = (-35, 153), T2 = (1, 23), T3 = (60, 0)), and
///         s = scene wind speed x the mesh's amplitude (.mdl +80) — taken ONCE, on the first
///         wind draw of the process (the function-local static at 0x834C3650, guard bit
///         0x834C3660), and never again. Only the first lane follows the live direction.
/// </remarks>
public static class Engine_ClassicWind
{
    private static readonly StringName SwaySpeed = "vs_g_SwaySpeed";
    private static readonly StringName WindFactors = "vs_g_WindFactors";
    private static readonly List<WeakReference<ShaderMaterial>> _materials = new();
    private static float? _amplitude;
    private static Vector4? _scaled;
    private static Vector4 _factors;

    private static readonly float Row1 = new Vector2(-35f, 153f).Length();
    private static readonly float Row2 = new Vector2(1f, 23f).Length();
    private const float Row3 = 60f;

    /// <summary>A WIND1 material: its own speed now, the shared factors as the feed has them.</summary>
    public static void Register(ShaderMaterial material, float amplitude, float speed)
    {
        material.SetShaderParameter(SwaySpeed, new Vector4(speed, speed, speed, speed));
        lock (_materials)
        {
            _amplitude ??= amplitude;
            _materials.Add(new WeakReference<ShaderMaterial>(material));
            if (_scaled is not null)
            {
                material.SetShaderParameter(WindFactors, _factors);
            }
        }
    }

    /// <summary>The frame's wind (scene+3384 direction, +3396 speed). Main thread, once a frame.</summary>
    public static void Pump(Vector3 direction, float speed)
    {
        lock (_materials)
        {
            if (_materials.Count == 0)
            {
                return;
            }
            if (_scaled is null)
            {
                float s = speed * (_amplitude ?? 0f);
                _scaled = new Vector4(s, s, 2f * s, 0.2f * s);
            }
            Vector4 k = _scaled.Value;
            var factors = new Vector4(
                100f * k.X / ((direction * 400f).Length() + 1e-10f),
                100f * k.Y / (Row1 + 1e-10f),
                100f * k.Z / (Row2 + 1e-10f),
                100f * k.W / (Row3 + 1e-10f));
            if (factors == _factors)
            {
                return;
            }
            _factors = factors;
            for (int i = _materials.Count - 1; i >= 0; i--)
            {
                if (!_materials[i].TryGetTarget(out ShaderMaterial? material) || !GodotObject.IsInstanceValid(material))
                {
                    _materials.RemoveAt(i);
                    continue;
                }
                Engine_ClassicMaterial.SetOnPasses(material, WindFactors, factors);
            }
        }
    }
}
