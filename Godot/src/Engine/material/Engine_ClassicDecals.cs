using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's decals: the guest's stated decals (RefiiHostGetDecals, taken from its
/// render command `sub_82A40610`) drawn as the title's 17 x 17 projected grids on VS 0149 /
/// PS 0150.
/// </summary>
/// <remarks>
/// THE TITLE'S DECAL (agent spec, refii-classic-remaining-families-spec): a ring of 50, the oldest
/// replaced, none expiring; each a grid over Right = n(dir x Z), Up = n(Right x dir), point (c, r)
/// at pos + Right ((c - 8) / 8) sx - Up ((r - 8) / 8) sy, uv (c / 16, r / 16), pushed onto the
/// surface along dir by the depth its projector camera saw (VSHADER_DECAL_MEMEXPORT, two refine
/// steps, valid when the last is within 0.4), normals from the triangle fan, indices b, b+1,
/// b+17, b+17, b+1, b+18. Drawn in the "Decal Layer" after the opaque world and the water: ONE /
/// INVSRCALPHA, depth test, no depth write, the vertex stage's 0.002 bias, culled past 64 m,
/// c31 = (ambient, a = d &lt; 44.8 ? 1 : 1 - (d - 44.8) / 19.2).
///
/// Here the projection runs on the GPU each frame against the main view's own depth (the post
/// chain's decal pass, <see cref="PointsName"/>), along the decal's axis, and the translated
/// vertex stage takes its POSITION, NORMAL and TEXCOORD0 from those points by vertex index. A
/// decal with an owner follows the owner's matrix (twin+336), from where it was laid.
/// </remarks>
public partial class Engine_ClassicDecals : Node
{
    /// <summary>The projected points: row 2s positions (NaN when off the surface), row 2s+1 normals.</summary>
    public const string PointsName = "fable_decal_points";

    /// <summary>Player two's view's projected points on split screen (the stage picks by <see cref="Engine_ClassicViews.SecondViewLayer"/>).</summary>
    public const string PointsSecondName = "fable_decal_points_second";

    /// <summary>The points global view <paramref name="view"/>'s post chain publishes.</summary>
    public static string PointsFor(int view) => view == 0 ? PointsName : PointsSecondName;

    public const int Slots = Refii.Fx.Engine_DecalFeed.Slots;
    public const int Points = 289;
    private const string VertexEntry = "VSHADER_DECAL";
    private const string PixelEntry = "PSHADER_DECAL";

    /// <summary>One decal's projection, guest axes, for the post chain's pass.</summary>
    public struct Projection
    {
        public bool Active;
        public Vector3 Position;
        public Vector3 Direction;
        public Vector2 Half;
    }

    /// <summary>What this renderer draws for one slot of the feed's ring.</summary>
    private sealed class DrawnSlot
    {
        public uint Serial;
        public MeshInstance3D? Node;
        public ShaderMaterial? Material;
    }

    private static readonly object Gate = new();
    private static readonly Projection[] _published = new Projection[Slots];
    private readonly Refii.Fx.Engine_DecalFeed _feed = new();
    private readonly DrawnSlot?[] _drawn = new DrawnSlot?[Slots];
    private readonly Dictionary<uint, Texture2D?> _textures = new();
    private ArrayMesh? _grid;
    private Shader? _shader;
    private bool _shaderTried;

    /// <summary>The live instance.</summary>
    public static Engine_ClassicDecals? Instance { get; private set; }

    /// <summary>Decals drawn at the last pump.</summary>
    public int Drawn { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        Name = "ClassicDecals";
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>Copies the decals' projections; any thread.</summary>
    public static void Snapshot(Projection[] into)
    {
        lock (Gate)
        {
            Array.Copy(_published, into, Slots);
        }
    }

    /// <summary>
    /// Follows the guest's decals and their owners. Main thread, once a frame. The 64 m cut and
    /// the distance fade are the node's, so on split screen they go by the nearer player's eye;
    /// the view-projection is each view's own (the material's for the first, the host register
    /// for the second, <see cref="Engine_ClassicViews"/>).
    /// </summary>
    public void Pump(Camera3D camera)
    {
        _feed.Pump();
        ReadOnlySpan<Refii.Fx.Engine_DecalFeed.Decal?> ring = _feed.Ring;
        Camera3D view = Engine_ClassicViews.Camera(0) ?? camera;
        bool split = Engine_ClassicViews.Count > 1;
        Vector3 eye = ToGuest(camera.GlobalPosition);
        int drawn = 0;
        lock (Gate)
        {
            for (int s = 0; s < Slots; s++)
            {
                Refii.Fx.Engine_DecalFeed.Decal? decal = ring[s];
                _published[s].Active = false;
                if (_drawn[s] is { } old && (decal is null || old.Serial != decal.Serial))
                {
                    old.Node?.QueueFree();
                    _drawn[s] = null;
                }
                if (decal is null)
                {
                    continue;
                }
                DrawnSlot slot = _drawn[s] ??= new DrawnSlot { Serial = decal.Serial };
                if (!EnsureNode(decal, slot, s))
                {
                    continue;
                }
                float distance = split
                    ? Engine_ClassicViews.NearestEyeDistance(ToGodot(decal.Position))
                    : eye.DistanceTo(decal.Position);
                // `Render_ParticlePoolDraw_INFERRED`: a decal past 64 m is not drawn.
                bool shown = distance * distance <= 4096f;
                slot.Node!.Visible = shown;
                if (!shown)
                {
                    continue;
                }
                _published[s] = new Projection
                {
                    Active = true,
                    Position = decal.Position,
                    Direction = decal.Direction,
                    Half = decal.Half,
                };
                Constants(decal, slot, view, distance);
                ++drawn;
            }
        }
        Drawn = drawn;
    }

    private bool EnsureNode(Refii.Fx.Engine_DecalFeed.Decal decal, DrawnSlot slot, int index)
    {
        if (slot.Node is not null)
        {
            return true;
        }
        if (!EnsureShader())
        {
            return false;
        }
        if (!_textures.TryGetValue(decal.Texture, out Texture2D? texture))
        {
            texture = Refii.Level.Engine_EnvironmentTextures.Load(decal.Texture, Engine_BnkMeshLoader.ReadBytes, out _);
            _textures[decal.Texture] = texture;
        }
        if (texture is null)
        {
            return false;
        }
        var material = new ShaderMaterial { Shader = _shader, RenderPriority = 5 };
        material.SetShaderParameter("ps_g_DiffuseSampler", texture);
        material.SetShaderParameter("ps_g_ScreenShadowMap", Engine_ClassicScreenShadow.Mask);
        // g_PrimitiveWorldMatrix: the points are projected in the guest's world, so identity.
        material.SetShaderParameter("vs_g_PrimitiveWorldMatrix", new[]
        {
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f),
        });
        // g_DecalAlpha = min(life x 0.5, 1): the life is FLT_MAX, so 1.
        material.SetShaderParameter("vs_g_DecalAlpha", new Vector4(1f, 0f, 0f, 0f));
        Vector3 at = ToGodot(decal.Position);
        var node = new MeshInstance3D
        {
            Name = $"Decal{index}",
            Mesh = _grid,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = Engine_ClassicParticles.Layer,
            ExtraCullMargin = 16384f,
            TopLevel = true,
        };
        AddChild(node);
        node.GlobalPosition = at;
        node.SetInstanceShaderParameter("xr_decal_slot", index * 2);
        Engine_ClassicLitInstances.Attach(node);
        slot.Node = node;
        slot.Material = material;
        return true;
    }

    private bool EnsureShader()
    {
        if (_shader is not null)
        {
            return true;
        }
        if (_shaderTried)
        {
            return false;
        }
        _shaderTried = true;
        EnsureRegistered();
        // The vertex stage's inputs from the projected points by vertex index: POSITION (NaN off
        // the surface, which kills the triangle as the title's NaN write does), NORMAL, and
        // TEXCOORD0 = (c / 16, r / 16). The stage reads them in the file's axes (xr_file_pos /
        // xr_file_dir take model (x, y, z) to (x, -z, y)), so the guest point p is model (x, z, -y).
        string defines =
            "global uniform sampler2D " + PointsName + ";\n" +
            "global uniform sampler2D " + PointsSecondName + ";\n" +
            "instance uniform int xr_decal_slot = 0;\n" +
            "#define XR_DECAL_AT(row) (XR_SECOND_VIEW ? texelFetch(" + PointsSecondName + ", ivec2(VERTEX_ID, xr_decal_slot + (row)), 0) : " +
            "texelFetch(" + PointsName + ", ivec2(VERTEX_ID, xr_decal_slot + (row)), 0))\n" +
            "#define VERTEX vec3(XR_DECAL_AT(0).x, XR_DECAL_AT(0).z, -XR_DECAL_AT(0).y)\n" +
            "#define NORMAL vec3(XR_DECAL_AT(1).x, XR_DECAL_AT(1).z, -XR_DECAL_AT(1).y)\n" +
            "#define UV vec2(float(VERTEX_ID % 17) / 16.0, float(VERTEX_ID / 17) / 16.0)\n" +
            "#define XR_POSITION_OUT(p) (p)\n" +
            ViewDefines;
        _shader = Engine_ClassicShaders.Compose(VertexEntry, PixelEntry,
            "unshaded, blend_premul_alpha, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled", 0, defines);
        // Before the decal stages' own constants were named they read frame registers.
        if (_shader is null || !_shader.Code.Contains("vs_g_MatViewProj", StringComparison.Ordinal))
        {
            GD.Print("[classic] the decal shaders predate their named constants; decals stay off until the shaders are compiled again");
            _shader = null;
            return false;
        }

        var indices = new int[16 * 16 * 6];
        int k = 0;
        for (int r = 0; r < 16; r++)
        {
            for (int c = 0; c < 16; c++)
            {
                int b = r * 17 + c;
                indices[k++] = b;
                indices[k++] = b + 1;
                indices[k++] = b + 17;
                indices[k++] = b + 17;
                indices[k++] = b + 1;
                indices[k++] = b + 18;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[Points];
        arrays[(int)Mesh.ArrayType.Index] = indices;
        _grid = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(_grid, Mesh.PrimitiveType.Triangles, arrays);
        arrays.Dispose();
        return true;
    }

    /// <summary>The per-frame constants: the camera's view-projection and the ambient with the distance fade.</summary>
    private static void Constants(Refii.Fx.Engine_DecalFeed.Decal decal, DrawnSlot slot, Camera3D camera, float distance)
    {
        ShaderMaterial m = slot.Material!;
        // g_MatViewProj rows: guest world -> Godot clip (the stage writes oPos straight out).
        Godot.Projection clip = Engine_ClassicShaders.GuestToClip(camera);
        m.SetShaderParameter("vs_g_MatViewProj", new[]
        {
            new Vector4(clip.X.X, clip.Y.X, clip.Z.X, clip.W.X),
            new Vector4(clip.X.Y, clip.Y.Y, clip.Z.Y, clip.W.Y),
            new Vector4(clip.X.Z, clip.Y.Z, clip.Z.Z, clip.W.Z),
            new Vector4(clip.X.W, clip.Y.W, clip.Z.W, clip.W.W),
        });
        Vector3 ambient = Engine_ClassicFeed.GlobalAmbient;
        float fade = distance < 44.8f ? 1f : 1f - (distance - 44.8f) / 19.2f;
        m.SetShaderParameter("ps_g_GlobalAmbientAndDistanceAlpha", new Vector4(ambient.X, ambient.Y, ambient.Z, fade));
        m.SetShaderParameter("xr_use_guest_position", true);
        slot.Node!.GlobalPosition = ToGodot(decal.Position);
    }

    private static bool _registered;

    /// <summary>The decal stage's g_MatViewProj belongs to the view (Engine_ClassicShaders reads the directive).</summary>
    private static string ViewDefines =>
        Engine_ClassicShaders.ViewParam("vs_g_MatViewProj", false, Engine_ClassicRegisters.Vs.HostDecalViewProjection);

    /// <summary>Registers the points global (a placeholder until the chain publishes). Main thread.</summary>
    public static void EnsureRegistered()
    {
        // Once per process: the server's global list is editor-only and reads empty in the game.
        if (_registered)
        {
            return;
        }
        _registered = true;
        var image = Image.CreateEmpty(Points, Slots * 2, false, Image.Format.Rgbaf);
        image.Fill(new Color(float.NaN, float.NaN, float.NaN, 0f));
        RenderingServer.GlobalShaderParameterAdd(PointsName,
            RenderingServer.GlobalShaderParameterType.Sampler2D, ImageTexture.CreateFromImage(image));
        RenderingServer.GlobalShaderParameterAdd(PointsSecondName,
            RenderingServer.GlobalShaderParameterType.Sampler2D, ImageTexture.CreateFromImage(image));
    }

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
}
