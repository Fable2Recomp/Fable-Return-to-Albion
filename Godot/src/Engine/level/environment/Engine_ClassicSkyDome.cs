using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Level;

/// <summary>
/// The classic renderer's sky dome: the title's own SKYCOLOUR pair on the unit cube it draws
/// around the eye, with the theme's overlay textures.
/// </summary>
/// <remarks>
/// THE TITLE'S DRAW (`sub_8228F038` -> `Render_MeshDrawWrapper_INFERRED_3` 0x8229B620): a cube
/// of eight {0,1}^3 corners and 36 indices (`dword_83317E00`, `word_83317DA0`), depth write off,
/// VSHADER_SKYCOLOUR placing each corner at (corner * 2 - 1) * 2850 + eye, PSHADER_SKYCOLOUR
/// scattering along the view direction from the in-scattering table and the atmosphere block,
/// plus two blended SKY_OVERLAY textures (s13, s14, blend c47) and the zenith optical depths
/// g_TzenithR = 150, g_TzenithM = 100 (`sub_82263570`, exp(-0) * 150 / 100). The eye and the
/// atmosphere come from the frame feed; this node owns the cube, the overlays and the depths.
///
/// Drawn first among the blended materials with depth test on and no depth write, so it
/// fills exactly the background the opaque scene left, as the title's ZWrite-off dome does.
/// The celestial elements, clouds and stars keep the host's ported draws.
/// </remarks>
[Tool]
public partial class Engine_ClassicSkyDome : MeshInstance3D
{
    private const string PixelEntry = "PSHADER_SKYCOLOUR";
    private const string VertexEntry = "VSHADER_SKYCOLOUR";

    /// <summary>
    /// A mirror camera's far plane is 50 m (Engine_WaterReflection) and the cube's corners sit
    /// 2850 out, so in any view but the main one the dome is drawn on the far plane (reverse Z:
    /// depth 0). The main view keeps the title's geometry, which hides what lies past the cube.
    /// </summary>
    private const string MirrorDepth =
        "#define XR_POSITION_OUT(p) (((CAMERA_VISIBLE_LAYERS & 1u) != 0u) ? (p) : vec4((p).xy, (p).w * 1e-6, (p).w))";

    private readonly Dictionary<uint, Texture2D?> _overlays = new();
    private ShaderMaterial? _material;
    private uint _overlayHash;
    private int _row;
    private bool _inMist;

    /// <summary>The live instance.</summary>
    public static Engine_ClassicSkyDome? Instance { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        Name = "ClassicSkyDome";
        // The vertex shader rotates its input into the file's axes and composes the object
        // matrix with XR_FILE_TO_MODEL, which takes a guest point to Godot as (x, z, -y); the
        // world is (-x, z, y). The node's own transform is that half-turn about Y, so the eye
        // and the corners the shader places in the guest's world land where the world is.
        TopLevel = true;
        Transform = new Transform3D(Basis.FromScale(new Vector3(-1f, 1f, -1f)), Vector3.Zero);
        Mesh = Cube();
        ExtraCullMargin = 16384f;
        // The title's reflection tile draws the sky (Engine_ReflectionLayer): without the bit the
        // mirrors showed the environment's own background, exposed by the water a second time.
        Layers = Engine_ReflectionLayer.ReflectedLayers;
        CastShadow = ShadowCastingSetting.Off;
        if (!Engine_ClassicShaders.HasEntry(PixelEntry) || !Engine_ClassicShaders.HasEntry(VertexEntry))
        {
            GD.Print("[classic] no SKYCOLOUR pair in the bank; the sky dome stays modern");
            return;
        }
        Shader? shader = Engine_ClassicShaders.Compose(
            VertexEntry, PixelEntry, "unshaded, blend_mix, depth_draw_never, cull_disabled", 0, MirrorDepth);
        if (shader is null)
        {
            return;
        }
        _material = new ShaderMaterial { Shader = shader, RenderPriority = -127 };
        _material.SetShaderParameter("ps_g_InScattering", Engine_ClassicAtmosphere.InScatteringTexture);
        _material.SetShaderParameter("ps_g_TzenithR", new Vector4(150f, 150f, 150f, 150f));
        _material.SetShaderParameter("ps_g_TzenithM", new Vector4(100f, 100f, 100f, 100f));
        _material.SetShaderParameter("ps_g_SkyOverlayTextureBlend", new Vector4(0f, 0f, 0f, 0f));
        _material.SetShaderParameter("xr_booleans", 1u << (16 + 4));
        _material.SetShaderParameter("ps_g_SkyOverlayTexture0", NoOverlay());
        _material.SetShaderParameter("ps_g_SkyOverlayTexture1", NoOverlay());
        MaterialOverride = _material;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        if (_row != 0)
        {
            Engine_ClassicConstants.ReleaseRow(_row);
            _row = 0;
        }
    }

    /// <summary>Follows the live sky theme and the camera's mist volume. Main thread, once a frame.</summary>
    public void Pump(Camera3D camera)
    {
        if (_material is null)
        {
            return;
        }
        SkyTheme? theme = Engine_SkyEnvironmentNode.Current?.Theme;
        uint hash = theme is { HasSkyOverlayTexture: true } ? theme.SkyOverlayTextureHash : 0u;
        if (hash != _overlayHash)
        {
            _overlayHash = hash;
            Texture2D overlay = (hash == 0u ? null : Overlay(hash)) ?? NoOverlay();
            _material.SetShaderParameter("ps_g_SkyOverlayTexture0", overlay);
            _material.SetShaderParameter("ps_g_SkyOverlayTexture1", overlay);
            _currentOverlay = overlay;
            Engine_ClassicWater.ApplyVistaOverlay(overlay);
        }

        // Inside a mist volume the dome takes the volume's registers, as the title binds them.
        Engine_ClassicMist.Volume? mist = Engine_ClassicMist.VolumeFor(new Aabb(camera.GlobalPosition, Vector3.Zero));
        bool inMist = mist is not null;
        if (inMist)
        {
            if (_row == 0)
            {
                _row = Engine_ClassicConstants.AcquireRow();
            }
            if (_row != 0)
            {
                Engine_ClassicMist.WriteRegisters(mist!, _row);
            }
        }
        if (inMist != _inMist)
        {
            _inMist = inMist;
            SetInstanceShaderParameter("xr_light_row", inMist ? _row : 0);
            _material.SetShaderParameter("xr_booleans", (1u << (16 + 4)) | (inMist ? 1u << (16 + 1) : 0u));
            if (inMist && mist is not null)
            {
                _material.SetShaderParameter("ps_g_MistDensitySampler", mist.Density64);
                _material.SetShaderParameter("ps_g_MistHeightSampler", Engine_ClassicMist.FlatHeight);
            }
        }
    }

    private static ImageTexture? _noOverlay;
    private Texture2D? _currentOverlay;

    /// <summary>The overlay the dome binds now (s13/s14), which the vista ocean's pixel shader shares.</summary>
    public static Texture2D CurrentOverlay => Instance?._currentOverlay ?? NoOverlay();

    /// <summary>
    /// What the title binds to an overlay slot with no texture: its 16x16 default filled with
    /// (0, 0, 0, 0) (`sub_821C2E98` -> `sub_822A1998`). Its alpha is the overlay's weight, so the
    /// dome shows the scattering. Unbound, Godot's black default is opaque and the sky goes black.
    /// </summary>
    private static ImageTexture NoOverlay()
    {
        if (_noOverlay is null)
        {
            Image image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
            image.Fill(new Color(0f, 0f, 0f, 0f));
            _noOverlay = ImageTexture.CreateFromImage(image);
        }
        return _noOverlay;
    }

    private Texture2D? Overlay(uint hash)
    {
        if (!_overlays.TryGetValue(hash, out Texture2D? texture))
        {
            texture = Engine_EnvironmentTextures.Load(hash, Engine_BnkMeshLoader.ReadBytes, out string path);
            if (texture is null)
            {
                GD.Print($"[classic] sky overlay 0x{hash:X8} " + (path.Length == 0 ? "unresolved" : $"'{path}' did not decode"));
            }
            _overlays[hash] = texture;
        }
        return texture;
    }

    /// <summary>The title's cube: {0,1}^3 corners, six faces, as its vertex shader expands them.</summary>
    private static ArrayMesh Cube()
    {
        // The {0,1}^3 corners in the FILE's axes, which is what `xr_file_pos` hands the shader:
        // file (x, y, z) is model (x, z, -y).
        var positions = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            positions[i] = new Vector3(i & 1, (i >> 2) & 1, -((i >> 1) & 1));
        }
        // Two triangles a face; winding is irrelevant to a cull-disabled dome.
        int[] indices =
        {
            0, 2, 3, 0, 3, 1,
            4, 5, 7, 4, 7, 6,
            0, 1, 5, 0, 5, 4,
            2, 6, 7, 2, 7, 3,
            0, 4, 6, 0, 6, 2,
            1, 3, 7, 1, 7, 5,
        };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-8192f, -8192f, -8192f), new Vector3(16384f, 16384f, 16384f));
        return mesh;
    }
}
