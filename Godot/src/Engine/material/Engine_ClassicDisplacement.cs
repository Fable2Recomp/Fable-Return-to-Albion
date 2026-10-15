using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's displacement target: what the title's "Displacement Renderer" draws
/// (`sub_821C7FC8`), for the post composite's DISPLACEMENT variants.
/// </summary>
/// <remarks>
/// THE TITLE'S TARGET: half the scene's size, RGBA8, cleared to 0, depth-tested against the
/// half-res scene depth with no depth write, every draw ONE / ONE (`sub_822A04D8`). The
/// displacement particle groups draw there with FACE_ME_DISPLACEMENT / PARTICLE_FACEME_DISPLACEMENT
/// and nothing else; the composite takes a DISPLACEMENT variant whenever anything was drawn
/// (`byte_83496E23`, set from the draw count in `sub_821C7FC8`).
///
/// Here the target is a half-size viewport sharing the world, its camera the main camera's,
/// seeing only <see cref="Layer"/>. A second view of the world has no scene depth of its own, so
/// the displacement materials test against the main view's depth as the post chain publishes it
/// (<see cref="SceneDepthName"/>), the previous frame's: the viewport renders before the main view.
/// </remarks>
public partial class Engine_ClassicDisplacement : Node
{
    /// <summary>The layer displacement draws are on; only this view sees it.</summary>
    public const uint Layer = 1u << 26;

    /// <summary>The main view's depth as the post chain copies it, for the displacement materials.</summary>
    public const string SceneDepthName = "fable_scene_depth";

    private SubViewport? _viewport;
    private Camera3D? _camera;

    /// <summary>The live instance.</summary>
    public static Engine_ClassicDisplacement? Instance { get; private set; }

    /// <summary>Displacement draws this frame (the title's byte_83496E23); set by the particle pump.</summary>
    public static int Drawn { get; set; }

    /// <summary>The target's RenderingServer texture, for the composite.</summary>
    public static Rid Texture { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        Name = "ClassicDisplacement";
        EnsureRegistered();
        _viewport = new SubViewport
        {
            Name = "DisplacementTarget",
            TransparentBg = true,
            // Linear values: the composite reads byte-scaled offsets, not a picture.
            UseHdr2D = true,
            Msaa3D = Viewport.Msaa.Disabled,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
        };
        _camera = new Camera3D
        {
            Name = "DisplacementCamera",
            CullMask = Layer,
            Current = true,
            Environment = new Environment
            {
                BackgroundMode = Environment.BGMode.Color,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                TonemapMode = Environment.ToneMapper.Linear,
            },
        };
        _viewport.AddChild(_camera);
        AddChild(_viewport);
        Texture = _viewport.GetTexture().GetRid();
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
            Texture = default;
        }
    }

    /// <summary>Follows the main camera at half its size; renders only while anything displaces. Main thread.</summary>
    public void Pump(Camera3D main, Vector2 viewport)
    {
        if (_viewport is null || _camera is null)
        {
            return;
        }
        Drawn += PumpQuads();
        // The displacement meshes standing (a composite's displacement parts) that are drawn.
        for (int i = Meshes.Count - 1; i >= 0; i--)
        {
            MeshInstance3D node = Meshes[i];
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
            {
                Meshes.RemoveAt(i);
                continue;
            }
            if (node.IsVisibleInTree())
            {
                ++Drawn;
            }
        }
        var size = new Vector2I(Mathf.Max((int)viewport.X / 2, 1), Mathf.Max((int)viewport.Y / 2, 1));
        if (_viewport.Size != size)
        {
            _viewport.Size = size;
        }
        _camera.GlobalTransform = main.GlobalTransform;
        _camera.Projection = main.Projection;
        _camera.Fov = main.Fov;
        _camera.Size = main.Size;
        _camera.Near = main.Near;
        _camera.Far = main.Far;
        _camera.KeepAspect = main.KeepAspect;
        _camera.HOffset = main.HOffset;
        _camera.VOffset = main.VOffset;
        _viewport.RenderTargetUpdateMode = Drawn > 0 ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }

    private static bool _registered;

    private static readonly System.Collections.Generic.List<MeshInstance3D> Meshes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> Records = new();

    /// <summary>A `.save` record whose static mesh states UseDisplacement (the level build). Any thread.</summary>
    public static void NoteRecord(uint record) => Records[record] = 0;

    /// <summary>
    /// A node drawing a record noted by <see cref="NoteRecord"/> becomes a displacement mesh: the
    /// displacement layer alone, its surfaces on <see cref="MeshMaterial"/>. False for any other.
    /// Main thread.
    /// </summary>
    public static bool AdoptRecord(uint record, MeshInstance3D node, Mesh mesh)
    {
        if (record == 0u || !Records.ContainsKey(record))
        {
            return false;
        }
        node.Layers = Layer;
        node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        // Out of the cascades too: the twin's layer is 0x1000 alone, which their gather never walks.
        Engine_ClassicCasters.Mark(node, 0, 0f);
        Engine_ClassicCasters.Register(node);
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            node.SetSurfaceOverrideMaterial(s, MeshMaterial(mesh.SurfaceGetMaterial(s)));
        }
        Track(node);
        return true;
    }
    private static readonly System.Collections.Generic.Dictionary<ulong, ShaderMaterial> MeshMaterials = new();
    private static Shader? _meshShader;
    private static bool _meshTried;

    /// <summary>A displacement mesh node standing for the frame count (the renderer draws while any is).</summary>
    public static void Track(MeshInstance3D node)
    {
        if (!Meshes.Contains(node))
        {
            Meshes.Add(node);
        }
    }

    /// <summary>
    /// A displacement mesh's draw (pass 3, `sub_82220668`: VS table[3] / PS table[4]): VS 172
    /// MESH_DISPLACEMENT_PASS (the skinned and one-bone stages 174 / 175 skin in their own code;
    /// Godot skins before the stage) with PS 173, which copies the surface's own diffuse into the
    /// target, ONE / ONE. Null for a surface with no material.
    /// </summary>
    public static Material? MeshMaterial(Material? surface)
    {
        if (surface is not ShaderMaterial source)
        {
            return null;
        }
        if (MeshMaterials.TryGetValue(source.GetInstanceId(), out ShaderMaterial? held))
        {
            return held;
        }
        if (!_meshTried)
        {
            _meshTried = true;
            _meshShader = Engine_ClassicShaders.Compose("VSHADER_MESH_DISPLACEMENT_PASS", "PSHADER_MESH_DISPLACEMENT_PASS",
                "unshaded, cull_disabled, depth_draw_never, depth_test_disabled, fog_disabled, blend_premul_alpha", 0,
                "#define XR_ALPHA_OUT(a) 0.0");
        }
        if (_meshShader is null)
        {
            return null;
        }
        var material = new ShaderMaterial { Shader = _meshShader };
        material.SetShaderParameter("ps_g_DiffuseTextureSampler", source.GetShaderParameter("ps_g_DiffuseTextureSampler"));
        MeshMaterials[source.GetInstanceId()] = material;
        return material;
    }

    private const string QuadVertex = "DISPLACEMENT_QUAD_VERTEX_SHADER";
    private const string QuadPixel = "DISPLACEMENT_QUAD_PIXEL_SHADER";
    /// <summary>The one texture a displacement quad is made with (`sub_822B62F0`).</summary>
    private const string DrunkTexture = "Art/GUI/HUD/FXT_Fullscreen_Drunk.tex";
    private const int MaxQuads = 4;

    private readonly float[] _quads = new float[1 + MaxQuads * 16];
    private readonly MeshInstance3D?[] _quadNodes = new MeshInstance3D?[MaxQuads];
    private readonly float[] _rowPs = new float[Engine_ClassicConstants.Floats];
    private readonly float[] _rowVs = new float[Engine_ClassicConstants.Floats];
    private ShaderMaterial? _quadMaterial;
    private bool _quadTried;
    private int _quadRow;

    /// <summary>
    /// The displacement quads the guest states (<see cref="Refii.Host.HostBridge.GetDisplacementQuads"/>),
    /// drawn as the title's draw (0x82A81828) does: VS 176 / PS 177 on four pixel-space corners, VS c8
    /// g_ScreenToViewportTransform = (2 / W, -2 / H), the quad's texture through the renderer's
    /// ONE / ONE. The stage writes D3D clip as it stands (y up, z from 0 at the near plane), so the
    /// corners are given with y mirrored and z as Godot's reversed depth. Returns the quads drawn.
    /// </summary>
    private int PumpQuads()
    {
        uint got = Refii.Host.HostBridge.GetDisplacementQuads(_quads);
        int count = got >= 1 ? System.Math.Min((int)_quads[0], MaxQuads) : 0;
        if (count > 0 && !EnsureQuad())
        {
            count = 0;
        }
        for (int i = 0; i < MaxQuads; i++)
        {
            if (i >= count)
            {
                if (_quadNodes[i] is { } idle)
                {
                    idle.Visible = false;
                }
                continue;
            }
            int at = 1 + i * 16;
            float x0 = _quads[at], y0 = _quads[at + 1], x1 = _quads[at + 2], y1 = _quads[at + 3];
            float z = 1f - _quads[at + 4], angle = _quads[at + 5];
            float w = _quads[at + 14], h = _quads[at + 15];
            if (w <= 0f || h <= 0f)
            {
                continue;
            }
            // v0 (x0, y0), v1 (x0, y1), v2 (x1, y1), v3 (x1, y0), turned about the centre by the
            // rotation with the extents corrected for aspect (0x82A81C10..; as shipped 0).
            var corners = new Vector2[] { new(x0, y0), new(x0, y1), new(x1, y1), new(x1, y0) };
            if (angle != 0f)
            {
                var centre = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                for (int k = 0; k < 4; k++)
                {
                    Vector2 d = corners[k] - centre;
                    corners[k] = centre + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
                }
            }
            var vertices = new Vector3[4];
            var uvs = new Vector2[4];
            for (int k = 0; k < 4; k++)
            {
                // The stage reads xr_file_pos(VERTEX) = (x, -z, y): the pixel point (x, H - y) at depth z.
                vertices[k] = new Vector3(corners[k].X, z, -(h - corners[k].Y));
                uvs[k] = new Vector2(_quads[at + 6 + k * 2], _quads[at + 7 + k * 2]);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = new[] { 1, 0, 2, 2, 0, 3 };
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            arrays.Dispose();
            MeshInstance3D node = _quadNodes[i] ??= NewQuad();
            node.Mesh = mesh;
            node.Visible = true;

            Engine_ClassicConstants.CopyFrame(true, _rowPs);
            Engine_ClassicConstants.CopyFrame(false, _rowVs);
            _rowVs[32] = 2f / w;
            _rowVs[33] = -2f / h;
            _rowVs[34] = 0f;
            _rowVs[35] = 0f;
            Engine_ClassicConstants.SetRowBlock(true, _quadRow, 0, _rowPs);
            Engine_ClassicConstants.SetRowBlock(false, _quadRow, 0, _rowVs);
        }
        return count;
    }

    private MeshInstance3D NewQuad()
    {
        var node = new MeshInstance3D
        {
            Name = "DisplacementQuad",
            MaterialOverride = _quadMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = Layer,
            ExtraCullMargin = 16384f,
        };
        AddChild(node);
        node.SetInstanceShaderParameter("xr_light_row", _quadRow);
        return node;
    }

    private bool EnsureQuad()
    {
        if (_quadMaterial is not null)
        {
            return true;
        }
        if (_quadTried)
        {
            return false;
        }
        _quadTried = true;
        Shader? shader = Engine_ClassicShaders.Compose(QuadVertex, QuadPixel,
            "unshaded, cull_disabled, depth_draw_never, depth_test_disabled, fog_disabled, blend_premul_alpha", 0,
            "#define XR_ALPHA_OUT(a) 0.0");
        Texture2D? texture = Refii.Textures.Engine_TexLibrary.Get(DrunkTexture, Refii.Textures.TexSemantic.Unknown,
            Engine_BnkMeshLoader.ReadBytes, out _);
        _quadRow = Engine_ClassicConstants.AcquireRow();
        if (shader is null || texture is null || _quadRow == 0)
        {
            GD.Print($"[classic] the displacement quad did not build (shader {(shader is null ? "missing" : "ok")}, texture {(texture is null ? "missing" : "ok")})");
            return false;
        }
        _quadMaterial = new ShaderMaterial { Shader = shader };
        _quadMaterial.SetShaderParameter("ps_g_TextureSampler", texture);
        _quadMaterial.SetShaderParameter("xr_use_guest_position", true);
        return true;
    }

    /// <summary>Registers the scene-depth global (a placeholder until the chain publishes). Main thread.</summary>
    public static void EnsureRegistered()
    {
        // Once per process: the server's global list is editor-only and reads empty in the game.
        if (_registered)
        {
            return;
        }
        _registered = true;
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rf);
        RenderingServer.GlobalShaderParameterAdd(SceneDepthName,
            RenderingServer.GlobalShaderParameterType.Sampler2D, ImageTexture.CreateFromImage(image));
    }
}
