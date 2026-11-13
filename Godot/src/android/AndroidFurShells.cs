using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_fur_max_shells`: at most this many layers N for shell fur (the dog, wolves), so N - 1
/// shell passes instead of the title's 15 to 29. 0 keeps the title's rule.
/// </summary>
/// <remarks>
/// <para>WHY. The title's N is clamp((L / d) / (2 tan(fovy / 2)) x 142.857 + 1, 16, 30)
/// (Engine_ModernFur, Engine_ClassicFur). For a dog's fur length that only rises above 16 within
/// about half a metre, so a furred body draws its furred surface 15 more times wherever it is: 15
/// full-shader passes (fur_shell.gdshader is not a lite build), each lit by every light, each with a
/// discard, over the whole coat.</para>
/// <para>WHAT CHANGES. Each shell is h = layer / N of the way out (fur_shell.gdshaderinc), so fewer
/// layers keep the coat's length and spread its slices further apart: the fur reads coarser and
/// more stepped where the coat's edge faces the camera. Live; nothing on the desktop.</para>
/// </remarks>
public static class AndroidFurShells
{
    /// <summary>The refii.toml key; 0 (the title's 16 to 30) unless set.</summary>
    public const string Key = "android_fur_max_shells";

    /// <summary>The fewest layers a cap may ask for: one shell pass.</summary>
    public const int MinLayers = 2;

    private static int _cap = -1;
    private static bool _hooked;

    /// <summary>The cap's value from the configuration: 0 off, else at least <see cref="MinLayers"/>. Pure.</summary>
    public static int Wanted(IReadOnlyDictionary<string, string> values)
    {
        int cap = Engine_RefiiConfig.Int(values, Key, 0);
        return cap <= 0 ? 0 : Math.Max(cap, MinLayers);
    }

    /// <summary>The title's layer count <paramref name="layers"/> under <paramref name="cap"/> (0: unchanged). Pure.</summary>
    public static int Cap(int layers, int cap) => cap <= 0 ? layers : Math.Min(layers, Math.Max(cap, MinLayers));

    /// <summary>This process's layer count for a furred instance whose title rule gives <paramref name="layers"/>.</summary>
    public static int Cap(int layers) => Cap(layers, Current);

    /// <summary>The cap in force: 0 off the phone or unset.</summary>
    public static int Current
    {
        get
        {
            if (_cap < 0)
            {
                Reload();
            }

            return _cap;
        }
    }

    private static void Reload()
    {
        // Off the phone nothing reads the configuration: the title's rule stands.
        if (!Engine_Platform.IsAndroid)
        {
            _cap = 0;
            return;
        }

        int cap;
        try
        {
            cap = Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            cap = 0;
        }

        _cap = cap;
        if (!_hooked)
        {
            _hooked = true;
            Engine_RefiiConfig.OnConfigChanged += (name, _) =>
            {
                if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase))
                {
                    _cap = -1;
                }
            };
            GD.Print($"[fur] {Key}={cap}: " + (cap == 0 ? "the title's 16-30 layers" : $"at most {cap} layers ({cap - 1} shell passes)"));
        }
    }
}
