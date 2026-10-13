using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's mist volumes: the scene's mist objects as the guest holds them, each
/// with its density texture regenerated every frame by the title's own evolve shader, and the
/// five mist registers a draw inside the volume takes.
/// </summary>
/// <remarks>
/// THE TITLE'S MIST (`sub_82A6D018`, `sub_82A6D440`, `sub_82A6E698`, `sub_82A6EAA8`): a volume
/// is an axis-aligned box (V+80..V+96) with a colour, a density and a height texture; its
/// 64x64 density texture is a pure function of the frame time and the volume's noise terms —
/// the evolve pass clears its target and samples only the Perlin permutation and gradient
/// tables, never the previous frame — so it is drawn here the same way: one small viewport per
/// volume running the translated PSHADER_MIST_EVOLVE_DENSITY over a quad, its constants in a
/// row of the constant file (c31 = (tiling.xy, speed * time, 0), c46 = noise offset).
///
/// A draw is inside a volume when its bounds meet the box and its floor is below the mist's
/// top (`sub_82A6DD30` with the default full-height texture); it then takes the MIST1 shader
/// pair and, in its rows, c72 = colour, c73 = V+352, c74 = V+368, c75 = V+320, c76 =
/// (height, floor, -log2(e) * density * ((strength - 1) * weight + 1), 1 / V+316).
///
/// Authored height textures (level mists) are not decoded yet: every volume samples the
/// default full-height texture the Lua path uses.
/// </remarks>
[Tool]
public partial class Engine_ClassicMist : Node
{
    /// <summary>One mist volume, in the guest's axes.</summary>
    public sealed class Volume
    {
        public Vector3 Min, Max, Colour, Extents, Origin;
        public float Density, NoiseOffset, Speed, TilingX, TilingY, Inv316, Weight;
        public Vector4 Params320, Remap352, Remap368;
        public int Row;
        public SubViewport? Viewport;
        public MeshInstance3D? Quad;
        public ShaderMaterial? Material;
        public Texture2D Density64 => Viewport!.GetTexture();
    }

    private const int DensitySize = 64;
    private const int MaxVolumes = 16;
    private const string EvolvePixel = "PSHADER_MIST_EVOLVE_DENSITY";
    private const string EvolveVertex = "VSHADER_PASSTHROUGH_POS_TEX1";

    private static readonly float[] _raw = new float[MaxVolumes * Refii.Host.HostBridge.ClassicMistStride];
    private static readonly float[] _four = new float[4];
    private static readonly List<Volume> _volumes = new();
    private static ImageTexture? _permutation, _gradients, _flatHeight;
    private static long _frames;

    /// <summary>The live instance, for the material path.</summary>
    public static Engine_ClassicMist? Instance { get; private set; }

    /// <summary>The volumes read this frame.</summary>
    public static IReadOnlyList<Volume> Volumes => _volumes;

    /// <summary>The 16x16 full-height texture the Lua path binds when no height map is given.</summary>
    public static Texture2D FlatHeight
    {
        get
        {
            if (_flatHeight is null)
            {
                var image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
                image.Fill(Colors.White);
                _flatHeight = ImageTexture.CreateFromImage(image);
            }
            return _flatHeight;
        }
    }

    public override void _Ready()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>Reads the volumes, keeps a density viewport per volume and writes their rows. Main thread.</summary>
    public void Pump(float globalStrength, float seconds)
    {
        ++_frames;
        int stride = Refii.Host.HostBridge.ClassicMistStride;
        uint count;
        if (Replay is { } replay)
        {
            count = (uint)Math.Min(replay.Length / stride, _raw.Length / stride);
            Array.Copy(replay, _raw, count * stride);
        }
        else
        {
            count = Refii.Host.HostBridge.GetClassicMists(_raw);
        }
        _rawCount = (int)count;

        while (_volumes.Count > (int)count)
        {
            Drop(_volumes[^1]);
            _volumes.RemoveAt(_volumes.Count - 1);
        }
        for (int i = 0; i < count; i++)
        {
            if (i >= _volumes.Count)
            {
                _volumes.Add(new Volume());
            }
            Volume v = _volumes[i];
            int at = i * stride;
            v.Min = new Vector3(_raw[at], _raw[at + 1], _raw[at + 2]);
            v.Max = new Vector3(_raw[at + 3], _raw[at + 4], _raw[at + 5]);
            v.Colour = new Vector3(_raw[at + 6], _raw[at + 7], _raw[at + 8]);
            v.Density = _raw[at + 10];
            v.NoiseOffset = _raw[at + 11];
            v.Speed = _raw[at + 12];
            v.TilingX = _raw[at + 13];
            v.TilingY = _raw[at + 14];
            v.Inv316 = _raw[at + 15] != 0f ? 1f / _raw[at + 15] : 0f;
            v.Params320 = new Vector4(_raw[at + 16], _raw[at + 17], _raw[at + 18], _raw[at + 19]);
            v.Weight = _raw[at + 20];
            v.Remap352 = new Vector4(_raw[at + 21], _raw[at + 22], _raw[at + 23], _raw[at + 24]);
            v.Remap368 = new Vector4(_raw[at + 25], _raw[at + 26], _raw[at + 27], _raw[at + 28]);
            v.Extents = new Vector3(_raw[at + 29], _raw[at + 30], _raw[at + 31]);
            v.Origin = new Vector3(_raw[at + 32], _raw[at + 33], _raw[at + 34]);

            if (v.Viewport is null && !Build(v))
            {
                continue;
            }
            // The evolve pass's constants, in the volume's row: c31 and c46 of that shader.
            Set(v.TilingX, v.TilingY, v.Speed * seconds, 0f);
            Engine_ClassicConstants.SetRowBlock(true, v.Row, 31, _four);
            Set(v.NoiseOffset, 0f, 0f, 0f);
            Engine_ClassicConstants.SetRowBlock(true, v.Row, 46, _four);
        }
        _globalStrength = globalStrength;
    }

    private static float _globalStrength = 1f;

    /// <summary>Writes a volume's five mist registers (c72..c76) into a draw's row.</summary>
    public static void WriteRegisters(Volume v, int row)
    {
        Set(v.Colour.X, v.Colour.Y, v.Colour.Z, 1f);
        Engine_ClassicConstants.SetRowBlock(true, row, Engine_ClassicRegisters.Ps.MistColour, _four);
        Set(v.Remap352.X, v.Remap352.Y, v.Remap352.Z, v.Remap352.W);
        Engine_ClassicConstants.SetRowBlock(true, row, Engine_ClassicRegisters.Ps.MistDensityTextureScaleAndOffset, _four);
        Set(v.Remap368.X, v.Remap368.Y, v.Remap368.Z, v.Remap368.W);
        Engine_ClassicConstants.SetRowBlock(true, row, Engine_ClassicRegisters.Ps.MistHeightTextureScaleAndOffset, _four);
        Set(v.Params320.X, v.Params320.Y, v.Params320.Z, v.Params320.W);
        Engine_ClassicConstants.SetRowBlock(true, row, Engine_ClassicRegisters.Ps.MistAnimationConstants, _four);
        float factor = (_globalStrength - 1f) * v.Weight + 1f;
        Set(v.Extents.Z, v.Origin.Z, -1.442695f * v.Density * factor, v.Inv316);
        Engine_ClassicConstants.SetRowBlock(true, row, Engine_ClassicRegisters.Ps.MistHeightScaleAndOffsetDensityFallOff, _four);
    }

    /// <summary>
    /// The volume a draw is inside, by the title's test with the default height texture: bounds
    /// meet the box and the draw's floor is below the mist's top. Bounds in Godot's axes.
    /// </summary>
    public static Volume? VolumeFor(in Aabb bounds)
    {
        Vector3 min = bounds.Position, max = bounds.End;
        foreach (Volume v in _volumes)
        {
            if (v.Viewport is null)
            {
                continue;
            }
            // Guest (x, y, z) is Godot (-x, z, y): the guest's X range mirrors.
            if (min.X > -v.Min.X || max.X < -v.Max.X || min.Z > v.Max.Y || max.Z < v.Min.Y ||
                min.Y > v.Max.Z || max.Y < v.Min.Z)
            {
                continue;
            }
            if (min.Y < v.Origin.Z + v.Extents.Z)
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>Recorded mists (36 floats each) read instead of the bridge.</summary>
    public static float[]? Replay;

    private static int _rawCount;

    /// <summary>The raw mists as last read, for the debug channel's recorder.</summary>
    public static float[] RawMists()
    {
        var copy = new float[_rawCount * Refii.Host.HostBridge.ClassicMistStride];
        Array.Copy(_raw, copy, copy.Length);
        return copy;
    }

    /// <summary>Volumes read and frames pumped, for the log.</summary>
    public static (int Volumes, long Frames) Counts => (_volumes.Count, _frames);

    private bool Build(Volume v)
    {
        if (!Engine_ClassicShaders.HasEntry(EvolvePixel) || !Engine_ClassicShaders.HasEntry(EvolveVertex))
        {
            return false;
        }
        Shader? shader = Engine_ClassicShaders.Compose(EvolveVertex, EvolvePixel, "unshaded, cull_disabled");
        if (shader is null)
        {
            return false;
        }
        v.Row = Engine_ClassicConstants.AcquireRow();
        if (v.Row == 0)
        {
            return false;
        }
        EnsureTables();
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("ps_g_PermTexture", _permutation);
        material.SetShaderParameter("ps_g_GradTexture", _gradients);

        var viewport = new SubViewport
        {
            Name = "ClassicMist" + _volumes.IndexOf(v),
            Size = new Vector2I(DensitySize, DensitySize),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            TransparentBg = false,
            GuiDisableInput = true,
            HandleInputLocally = false,
            Msaa3D = Viewport.Msaa.Disabled,
        };
        var camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 2f,
            Near = 0.1f,
            Far = 10f,
            Current = true,
            Position = new Vector3(0f, 0f, 1f),
        };
        var quad = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            MaterialOverride = material,
        };
        quad.SetInstanceShaderParameter("xr_light_row", v.Row);
        viewport.AddChild(camera);
        viewport.AddChild(quad);
        AddChild(viewport);
        v.Viewport = viewport;
        v.Quad = quad;
        v.Material = material;
        return true;
    }

    private void Drop(Volume v)
    {
        if (v.Row != 0)
        {
            Engine_ClassicConstants.ReleaseRow(v.Row);
            v.Row = 0;
        }
        v.Viewport?.QueueFree();
        v.Viewport = null;
    }

    /// <summary>
    /// Ken Perlin's permutation as a 256x1 table and the sixteen improved-noise gradients as a
    /// 16x1 table, as `sub_82A6DF60` builds them (L8 and 4 x S8); here as the sampled values.
    /// </summary>
    private static void EnsureTables()
    {
        if (_permutation is not null)
        {
            return;
        }
        var perm = Image.CreateEmpty(256, 1, false, Image.Format.Rgba8);
        for (int i = 0; i < 256; i++)
        {
            float p = Permutation[i] / 255f;
            perm.SetPixel(i, 0, new Color(p, p, p, 1f));
        }
        _permutation = ImageTexture.CreateFromImage(perm);

        var grad = Image.CreateEmpty(16, 1, false, Image.Format.Rgbaf);
        for (int k = 0; k < 16; k++)
        {
            Vector3 g = Gradients[k];
            Vector3 next = Gradients[(k + 1) % 16];
            grad.SetPixel(k, 0, new Color(g.X, g.Y, g.Z, next.X));
        }
        _gradients = ImageTexture.CreateFromImage(grad);
    }

    private static readonly int[] Permutation =
    {
        151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142,
        8, 99, 37, 240, 21, 10, 23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117,
        35, 11, 32, 57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175, 74, 165, 71,
        134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122, 60, 211, 133, 230, 220, 105, 92, 41,
        55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208, 89,
        18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186, 3, 64, 52, 217, 226,
        250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17, 182,
        189, 28, 42, 223, 183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43,
        172, 9, 129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104, 218, 246, 97,
        228, 251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107,
        49, 192, 214, 31, 181, 199, 106, 157, 184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138,
        236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128, 195, 78, 66, 215, 61, 156, 180,
    };

    private static readonly Vector3[] Gradients =
    {
        new(1, 1, 0), new(-1, 1, 0), new(1, -1, 0), new(-1, -1, 0),
        new(1, 0, 1), new(-1, 0, 1), new(1, 0, -1), new(-1, 0, -1),
        new(0, 1, 1), new(0, -1, 1), new(0, 1, -1), new(0, -1, -1),
        new(1, 1, 0), new(0, -1, 1), new(-1, 1, 0), new(0, -1, -1),
    };

    private static void Set(float x, float y, float z, float w)
    {
        _four[0] = x; _four[1] = y; _four[2] = z; _four[3] = w;
    }
}
