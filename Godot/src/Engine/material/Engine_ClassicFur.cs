using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's shell fur (the dog and the wolves): the title's fur slices, its shells
/// on the furred primitive (VS 0104 / PS 0105), and the two per-frame prelit textures they read.
/// </summary>
/// <remarks>
/// THE TITLE'S RULE:
/// - A skinned primitive carrying a fur block (Engine_MdlMesh.FurMeta) is drawn again N - 1
///   times as shells 1..N-1 (`Render_LodMeshDraw_INFERRED` 0x821F16B0), N =
///   clamp(int((L / d) / (0.5 cam+0xBC) x 142.857 + 1), 16, 30) (`sub_8218F1A8`), d = max(|eye -
///   centre| - radius, near), cam+0xBC = 2 tan(fovy / 2), L = the fur material's length (file
///   value 8, mat+0x40). Per shell: VS c8.x = L (1 + 0.1 d), c46.x = layer / N, c47.x = the file's
///   value 9 (mat+0x84), c113.x = 1 / (c8.x x 200); PS c31.x = 0.3 B (B = `sub_82A85E68`, see
///   Engine_ClassicBillboards); s13 = slice floor(clamp(layer / N, 0, 0.9999) x 20).
/// - The slices (`sub_82A4C400`, verified): 20 x 128^2 A8L8 from the CRT rand (s = s x 0x343FD +
///   0x269EC3, (s >> 16) & 0x7FFF) seeded 235794 (mat+0xB0, ctor `sub_82B28E20`). Per texel, row
///   by row: three rands, grey v = r3 / 65536 + 0.5 (the first two feed a zero hue and saturation,
///   `sub_82B87028`), L = trunc(255 v) in every slice; then h = min(trunc((0.2 + 1.6 pow(r / 32768,
///   3)) x 20), 20), zeroed when the next r / 32768 >= 1.0; slice j - 1 (j = 1..h) takes alpha
///   trunc((1 - falloff x t^2) x 255 + 0.5) with t = 2j / h - 1, and t^2 read as 0 for t <= 0
///   (the fsel at 0x82A4C714). falloff = the file's value 16 (mat+0xB4).
/// - The shells' vertices are the primitive's skinned ones (stream 0) with the fur block's
///   per-vertex record as stream 1: COLOR (the D3DCOLOR, .w the vertex's length) and TEXCOORD2
///   (the four floats: the fur texture's uv at the root and its drift per shell).
///
/// HERE the shells are a mesh of their own bound to the same skeleton (the furred surface's
/// triangles, with the record in COLOR and CUSTOM0), drawn as a NextPass chain, each pass on its
/// own constant row.
///
/// A PRELIT PAIR PER VIEW. The prelit passes are lit from the eye (the FURMATERIAL specular, the
/// skin's) with the sun cascades fitted to it, and culled by the camera they follow, so split
/// screen's second view renders its own pair from its camera, its copy of the instance's rows and
/// its cascade set, on a layer of its own (<see cref="PrelitSecondLayer"/>); the shells sample it
/// in that view (<see cref="Engine_ClassicShaders.ViewSampler"/>). Built when the screen splits,
/// freed when it rejoins.
///
/// THE SHELLS FADE WITH THE BODY, A REMASTER CHOICE (the title's shell pass never reads the
/// opacity: Engine_EntityAppearance, "THE FUR SHELLS FADE WITH THE BODY"). The composition writes
/// no ALPHA and cuts on its own output alpha (PS 0105's alpha test, reference 16), so the body's
/// Transparency on the shell node is a separate factor; while the body keeps its depth the chain
/// also draws each shell's depth first (the same stages writing nothing else, render priority
/// -128), so only the nearest surviving texel blends. At rest neither exists.
/// </remarks>
public partial class Engine_ClassicFur : Node
{
    /// <summary>The layer the prelit draws are on: only the prelit targets' cameras see it.</summary>
    public const uint PrelitLayer = 1u << 27;

    /// <summary>Layer 23: the second view's prelit draws, seen only by its prelit targets' cameras.</summary>
    public const uint PrelitSecondLayer = 1u << 22;

    private const string PrelitVertex = "res://src/Engine/material/classic_fur_prelit_vertex.gdshaderinc";
    private const string DilateVertex = "VSHADER_BORDER_DILATE";
    private const string DilatePixel = "PSHADER_BORDER_DILATE";
    /// <summary>The prelit textures' side: the title's largest (`sub_822B6718` clamps to [64, 256]).</summary>
    private const int PrelitSize = 256;
    private const string ShellVertex = "VSHADER_SHELL_FUR_SIMPLE_PRESKINNED";
    private const string ShellPixel = "PSHADER_SHELL_FUR_SIMPLE";
    private const string ShellMistPixel = "PSHADER_SHELL_FUR_SIMPLE_MIST";
    internal const int MaxShells = 30;
    private const int Slices = 20;
    private const int SliceSize = 128;

    private static readonly StringName ShellRow = "xr_fur_row";
    private static readonly StringName PrelitFurName = "ps_g_PrelitFurTexture";
    private static readonly StringName PrelitSkinName = "ps_g_PrelitSkinTexture";
    private static readonly StringName PrelitFurSecondName = Engine_ClassicShaders.SecondViewSampler("ps_g_PrelitFurTexture");
    private static readonly StringName PrelitSkinSecondName = Engine_ClassicShaders.SecondViewSampler("ps_g_PrelitSkinTexture");
    private static readonly StringName Cascade0Name = "ps_g_DirectionalLightShadowMapSampler0";
    private static readonly StringName Cascade1Name = "ps_g_DirectionalLightShadowMapSampler1";

    /// <summary>The shells' compositions: each pass reads its own row; the prelit pair is the view's.</summary>
    private static readonly string ShellDefines =
        "uniform int xr_fur_row = 0;\n#define XR_ROW_UNIFORM xr_fur_row\n" +
        Engine_ClassicShaders.ViewSampler("ps_g_PrelitFurTexture") + "\n" +
        Engine_ClassicShaders.ViewSampler("ps_g_PrelitSkinTexture");

    /// <summary>
    /// One prelit texture: the furred surface drawn in uv space, one pass per light group of the
    /// instance (the first replacing, the rest adding), then BORDER_DILATE'd.
    /// </summary>
    private sealed class Prelit
    {
        public SubViewport Target = null!;
        public Camera3D Camera = null!;
        public MeshInstance3D Node = null!;
        public SubViewport Dilated = null!;
        public MeshInstance3D Source = null!;
        public ShaderMaterial Skin = null!;
        public bool Fur;
        /// <summary>The view whose camera, rows and cascades it is lit from.</summary>
        public int View;
        /// <summary>The first cascade map its passes bind (<see cref="BindCascades"/>).</summary>
        public Texture2D? Cascades;
        public readonly int[] Rows = new int[3];
        public string Shape = string.Empty;
        public readonly Dictionary<string, ShaderMaterial> Cache = new();
    }

    private sealed class Furred
    {
        public Prelit? FurPass, SkinPass;
        /// <summary>The second view's pair while the screen is split.</summary>
        public Prelit? SecondFurPass, SecondSkinPass;
        public MeshInstance3D Source = null!;
        public MeshInstance3D Shell = null!;
        public readonly ShaderMaterial[] Passes = new ShaderMaterial[MaxShells - 1];
        public readonly int[] Rows = new int[MaxShells - 1];
        public float Length;
        public float Occlusion;
        /// <summary>The mist volume the shells drew in last (PS 128), or null (PS 127).</summary>
        public Engine_ClassicMist.Volume? Mist;
        public bool MistKnown;
        public int Linked = -1;
        /// <summary>The shell node's visibility: the source's colour pass is built (Engine_EntityAppearance.IsSubmitted).</summary>
        public bool Shown = true;
        /// <summary>The body's fade on the shell node (Engine_EntityAppearance.FadeOf).</summary>
        public float Transparency;
        /// <summary>The body keeps its depth while faded: the chain draws the shells' depth first.</summary>
        public bool Depth;
        public bool LinkedDepth;
        /// <summary>The depth-only passes, one per shell on its row; made at the first faded frame.</summary>
        public ShaderMaterial[]? DepthPasses;
    }

    private readonly Dictionary<ulong, Furred> _furred = new();
    private readonly List<ulong> _gone = new();
    private static readonly Dictionary<float, ImageTexture[]> SlicesByFalloff = new();
    private static readonly Dictionary<ulong, ArrayMesh?> ShellMeshes = new();
    private Shader? _shader, _mistShader, _depthShader;
    private bool _shaderTried, _depthTried;

    private static readonly float[] FramePs = new float[Engine_ClassicConstants.Floats];
    private static readonly float[] FrameVs = new float[Engine_ClassicConstants.Floats];

    /// <summary>The live instance.</summary>
    public static Engine_ClassicFur? Instance { get; private set; }

    /// <summary>Furred instances, and the shells drawn on the last frame.</summary>
    public (int Instances, int Shells) Counts
    {
        get
        {
            int shells = 0;
            foreach (Furred f in _furred.Values)
            {
                shells += f.Shown ? Math.Max(f.Linked, 0) : 0;
            }
            return (_furred.Count, shells);
        }
    }

    public override void _Ready()
    {
        Instance = this;
        Name = "ClassicFur";
    }

    public override void _ExitTree()
    {
        foreach (Furred f in _furred.Values)
        {
            Release(f);
        }
        _furred.Clear();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<MeshInstance3D> Pending = new();

    private static void Release(Furred furred)
    {
        foreach (int row in furred.Rows)
        {
            Engine_ClassicConstants.ReleaseRow(row);
        }
        foreach (Prelit? prelit in new[] { furred.FurPass, furred.SkinPass, furred.SecondFurPass, furred.SecondSkinPass })
        {
            FreePrelit(prelit);
        }
    }

    /// <summary>A prelit target's rows and viewports.</summary>
    private static void FreePrelit(Prelit? prelit)
    {
        if (prelit is null)
        {
            return;
        }
        foreach (int row in prelit.Rows)
        {
            Engine_ClassicConstants.ReleaseRow(row);
        }
        if (GodotObject.IsInstanceValid(prelit.Target))
        {
            prelit.Target.QueueFree();
        }
        if (GodotObject.IsInstanceValid(prelit.Dilated))
        {
            prelit.Dilated.QueueFree();
        }
    }

    /// <summary>
    /// Builds the second view's prelit pair of a furred instance and binds it to the shells' second
    /// view samplers; once the second view's cascades stand (the pair is lit with them).
    /// </summary>
    private void SplitPrelit(Furred furred)
    {
        if (furred.FurPass is null || furred.SecondFurPass is not null || furred.SecondSkinPass is not null ||
            Engine_ClassicShadows.Instance?.CascadeTextureFor(1, 0) is null ||
            furred.Shell.Mesh is not ArrayMesh shellMesh)
        {
            return;
        }
        MeshInstance3D mesh = furred.Source;
        ShaderMaterial skinSource = furred.FurPass.Skin;
        Node? skeleton = mesh.GetNodeOrNull(mesh.Skeleton);
        furred.SecondFurPass = MakePrelit(mesh, shellMesh, skeleton, skinSource, fur: true, "FurPrelitSecondView", 1);
        furred.SecondSkinPass = MakePrelit(mesh, shellMesh, skeleton, skinSource, fur: false, "SkinPrelitSecondView", 1);
        foreach (ShaderMaterial pass in furred.Passes)
        {
            if (furred.SecondFurPass is not null)
            {
                pass.SetShaderParameter(PrelitFurSecondName, furred.SecondFurPass.Dilated.GetTexture());
            }
            if (furred.SecondSkinPass is not null)
            {
                pass.SetShaderParameter(PrelitSkinSecondName, furred.SecondSkinPass.Dilated.GetTexture());
            }
        }
    }

    /// <summary>Frees the second view's prelit pair when the screen rejoins; its samplers take the first's again.</summary>
    private static void JoinPrelit(Furred furred)
    {
        if (furred.SecondFurPass is null && furred.SecondSkinPass is null)
        {
            return;
        }
        BindPrelit(furred);
        foreach (Prelit? prelit in new[] { furred.SecondFurPass, furred.SecondSkinPass })
        {
            FreePrelit(prelit);
            if (prelit is not null && GodotObject.IsInstanceValid(prelit.Node))
            {
                prelit.Node.QueueFree();
            }
        }
        furred.SecondFurPass = furred.SecondSkinPass = null;
    }

    /// <summary>Renders a prelit pair while its instance is drawn.</summary>
    private static void Update(Prelit? prelit, bool visible)
    {
        if (prelit is null)
        {
            return;
        }
        SubViewport.UpdateMode mode = visible ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (prelit.Target.RenderTargetUpdateMode != mode)
        {
            prelit.Target.RenderTargetUpdateMode = mode;
            prelit.Dilated.RenderTargetUpdateMode = mode;
        }
    }

    /// <summary>A lit instance; one whose mesh carries fur gets its shells at the next pump. Any thread.</summary>
    public static void Attach(GeometryInstance3D node)
    {
        if (node is MeshInstance3D mesh && mesh.Mesh is ArrayMesh source && source.HasMeta(Engine_MdlMesh.FurMeta))
        {
            Pending.Enqueue(mesh);
        }
    }

    private void AttachNow(MeshInstance3D mesh)
    {
        if (!GodotObject.IsInstanceValid(mesh) || mesh.Mesh is not ArrayMesh source ||
            _furred.ContainsKey(mesh.GetInstanceId()) || !EnsureShader())
        {
            return;
        }
        Engine_ClassicFur self = this;
        GeometryInstance3D node = mesh;
        if (Shells(source) is not { } shellMesh ||
            source.GetMeta(Engine_MdlMesh.FurMeta).AsGodotArray() is not { Count: > 0 } blocks ||
            blocks[0].AsGodotDictionary()["fur"].AsFloat32Array() is not { Length: >= 17 } fur)
        {
            return;
        }

        var furred = new Furred { Source = mesh, Length = fur[8], Occlusion = fur[9] };
        ImageTexture[] slices = SlicesFor(fur[16]);
        for (int i = 0; i < furred.Passes.Length; i++)
        {
            var pass = new ShaderMaterial { Shader = self._shader };
            furred.Rows[i] = Engine_ClassicConstants.AcquireRow();
            pass.SetShaderParameter(ShellRow, furred.Rows[i]);
            pass.SetShaderParameter("ps_g_Extinction", Engine_ClassicAtmosphere.ExtinctionTexture);
            pass.SetShaderParameter("ps_g_InScattering", Engine_ClassicAtmosphere.InScatteringTexture);
            // Alpha test, reference 16 (the shells' cut).
            pass.SetShaderParameter("xr_spec_constants", 2u);
            pass.SetShaderParameter("xr_alpha_threshold", 16f / 255f);
            var furSlices = new Godot.Collections.Array<ImageTexture>(slices);
            pass.SetMeta("fur_slices", furSlices);
            GC.KeepAlive(furSlices);
            furred.Passes[i] = pass;
        }
        furred.Shell = new MeshInstance3D
        {
            Name = "FurShells",
            Mesh = shellMesh,
            Skin = mesh.Skin,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = mesh.Layers,
            MaterialOverride = furred.Passes[0],
        };
        mesh.AddChild(furred.Shell);
        Node? skeleton = mesh.GetNodeOrNull(mesh.Skeleton);
        if (skeleton is not null)
        {
            furred.Shell.Skeleton = furred.Shell.GetPathTo(skeleton);
        }

        // The prelit textures (`sub_82268C70`): the fur material's pass and the skin's.
        ShaderMaterial? skinSource = SkinOf(mesh, shellMesh);
        if (skinSource is not null)
        {
            furred.FurPass = MakePrelit(mesh, shellMesh, skeleton, skinSource, fur: true, "FurPrelit", 0);
            furred.SkinPass = MakePrelit(mesh, shellMesh, skeleton, skinSource, fur: false, "SkinPrelit", 0);
        }
        BindPrelit(furred);
        self._furred[node.GetInstanceId()] = furred;
    }

    /// <summary>
    /// The shells' prelit samplers: the first view's pair, which the second view's samplers also
    /// take until (and after) its own pair stands.
    /// </summary>
    private static void BindPrelit(Furred furred)
    {
        foreach (ShaderMaterial pass in furred.Passes)
        {
            if (furred.FurPass is not null)
            {
                Texture2D fur = furred.FurPass.Dilated.GetTexture();
                pass.SetShaderParameter(PrelitFurName, fur);
                pass.SetShaderParameter(PrelitFurSecondName, fur);
            }
            if (furred.SkinPass is not null)
            {
                Texture2D skin = furred.SkinPass.Dilated.GetTexture();
                pass.SetShaderParameter(PrelitSkinName, skin);
                pass.SetShaderParameter(PrelitSkinSecondName, skin);
            }
        }
    }

    /// <summary>The skin material: the one the furred primitive's source surface draws with.</summary>
    private static ShaderMaterial? SkinOf(MeshInstance3D mesh, ArrayMesh shellMesh)
    {
        if (!shellMesh.HasMeta(SourceSurfacesMeta) || shellMesh.GetMeta(SourceSurfacesMeta).AsInt32Array() is not { Length: > 0 } surfaces)
        {
            return null;
        }
        int s = surfaces[0];
        Material? material = mesh.GetSurfaceOverrideMaterial(s) ?? mesh.Mesh.SurfaceGetMaterial(s);
        return material is ShaderMaterial shader && shader.HasMeta(Engine_ClassicMaterial.ClassicMeta) ? shader : null;
    }

    private Shader? _dilateShader;
    private bool _prelitTried;
    private readonly Dictionary<(int Blt, int Pass), Shader?> _prelitShaders = new();

    /// <summary>
    /// The fur pass's material for one light group: the FURMATERIAL pixel stage of the group's
    /// BLT (`*(mat+0xD8)`; BLT1 ships misted only) on the stand-in vertex stage, with the skin's
    /// layer-0 diffuse and its uv scale and offset (s0 and c8 from the skin material's own texture
    /// slot, `sub_821A5220` -> `sub_8221BB18`) and the sun's cascades. A later group adds
    /// (SrcBlend = DestBlend = ONE when the pass index is 1, `sub_821A5220`).
    /// </summary>
    private ShaderMaterial? FurPrelitMaterial(ShaderMaterial skin, int blt, int pass, int view)
    {
        if (!_prelitShaders.TryGetValue((blt, pass), out Shader? shader))
        {
            string pixel = $"PSHADER_FURMATERIAL_BLT{blt}_GLT1_MIST{(blt == 1 ? 1 : 0)}";
            shader = Engine_ClassicShaders.Compose(PrelitVertex, pixel,
                pass == 0 ? Engine_ClassicMaterial.UvSpaceRenderMode : Engine_ClassicMaterial.UvSpaceAddRenderMode,
                pass, pass == 0 ? string.Empty : Engine_ClassicMaterial.UvSpaceAddDefines);
            _prelitShaders[(blt, pass)] = shader;
        }
        if (shader is null)
        {
            return null;
        }
        var m = new ShaderMaterial { Shader = shader };
        m.SetShaderParameter("ps_g_DiffuseTextureSampler", skin.GetShaderParameter("ps_g_DiffuseTextureSampler"));
        Variant uv = skin.GetShaderParameter("ps_g_DiffuseTextureUVMatrix");
        m.SetShaderParameter("ps_g_DiffuseTextureUVMatrix", uv.VariantType == Variant.Type.Nil ? new Vector4(1f, 1f, 0f, 0f) : uv);
        if (Engine_ClassicShadows.Instance is { } shadows)
        {
            Texture2D? c0 = shadows.CascadeTextureFor(view, 0);
            Texture2D? c1 = shadows.CascadeTextureFor(view, 1);
            m.SetShaderParameter(Cascade0Name, c0 is not null ? Variant.From(c0) : default);
            m.SetShaderParameter(Cascade1Name, c1 is not null ? Variant.From(c1) : default);
        }
        // The group's spot map: the prelit rows are copies of the group's own row, which carries
        // its spot transform and layer (Engine_ClassicSpotShadows).
        m.SetShaderParameter(Engine_ClassicSpotShadows.SamplerName, Engine_ClassicSpotShadows.Maps);
        m.SetShaderParameter("xr_booleans", Engine_ClassicSpotShadows.DoSpotShadows);
        return m;
    }

    private bool EnsurePrelitShaders()
    {
        if (_dilateShader is not null)
        {
            return true;
        }
        if (_prelitTried)
        {
            return false;
        }
        _prelitTried = true;
        // BORDER_DILATE's stage writes its position as given; the quad carries clip coordinates.
        _dilateShader = Engine_ClassicShaders.Compose(DilateVertex, DilatePixel, Engine_ClassicMaterial.UvSpaceRenderMode, 0, string.Empty);
        return _dilateShader is not null;
    }

    /// <summary>One pass of a prelit texture for light group <paramref name="pass"/> (or the frame's row).</summary>
    private ShaderMaterial? PassMaterial(Prelit prelit, int pass, int count, bool single)
    {
        int blt = Engine_ClassicMaterial.BltFor(count, single);
        string key = $"{pass}:{count}:{single}";
        if (prelit.Cache.TryGetValue(key, out ShaderMaterial? held))
        {
            return held;
        }
        ShaderMaterial? made;
        if (prelit.Fur)
        {
            made = FurPrelitMaterial(prelit.Skin, blt, pass, prelit.View);
        }
        else
        {
            // The skin's own light-group variant, drawn in uv space.
            ShaderMaterial variant = count == 0 && pass == 0
                ? prelit.Skin
                : Engine_ClassicMaterial.ForLightGroup(prelit.Skin, count, single, pass) ?? prelit.Skin;
            made = Engine_ClassicMaterial.UvSpaceCopy(variant, pass);
        }
        if (made is not null)
        {
            prelit.Cache[key] = made;
        }
        return made;
    }

    /// <summary>
    /// A prelit target sharing the world (its camera the view's, seeing only the view's prelit
    /// layer, with no environment or post chain of its own), its draw bound to the source's
    /// skeleton, and the dilate target that reads it.
    /// </summary>
    private Prelit? MakePrelit(MeshInstance3D source, ArrayMesh shellMesh, Node? skeleton, ShaderMaterial skin, bool fur, string name, int view)
    {
        if (!EnsurePrelitShaders() || source.GetViewport()?.World3D is not { } world)
        {
            return null;
        }
        uint layer = view == 0 ? PrelitLayer : PrelitSecondLayer;
        // The first view's passes are made on the bound pair (the skin's own and FurPrelitMaterial's).
        var prelit = new Prelit
        {
            Source = source, Skin = skin, Fur = fur, View = view,
            Cascades = view == 0 ? Engine_ClassicShadows.Instance?.CascadeTexture(0) : null,
        };
        prelit.Target = new SubViewport
        {
            Name = name,
            World3D = world,
            Size = new Vector2I(PrelitSize, PrelitSize),
            TransparentBg = true,
            UseHdr2D = true,
            Msaa3D = Viewport.Msaa.Disabled,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        prelit.Camera = new Camera3D
        {
            CullMask = layer,
            Current = true,
            Environment = PlainEnvironment(),
            Compositor = new Compositor(),
        };
        prelit.Target.AddChild(prelit.Camera);
        AddChild(prelit.Target);
        prelit.Node = new MeshInstance3D
        {
            Name = name,
            Mesh = shellMesh,
            Skin = source.Skin,
            Layers = layer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        source.AddChild(prelit.Node);
        if (skeleton is not null)
        {
            prelit.Node.Skeleton = prelit.Node.GetPathTo(skeleton);
        }

        prelit.Dilated = new SubViewport
        {
            Name = name + "Dilated",
            OwnWorld3D = true,
            Size = new Vector2I(PrelitSize, PrelitSize),
            TransparentBg = true,
            UseHdr2D = true,
            Msaa3D = Viewport.Msaa.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var dilate = new ShaderMaterial { Shader = _dilateShader };
        dilate.SetShaderParameter("ps_g_TextureSampler", prelit.Target.GetTexture());
        dilate.SetShaderParameter("xr_use_guest_position", true);
        prelit.Dilated.AddChild(new Camera3D { Current = true, Environment = PlainEnvironment(), Compositor = new Compositor() });
        prelit.Dilated.AddChild(new MeshInstance3D
        {
            Mesh = ClipQuad(),
            MaterialOverride = dilate,
            Position = new Vector3(0f, 0f, -1f),
            ExtraCullMargin = 16f,
        });
        AddChild(prelit.Dilated);
        return prelit;
    }

    private static Godot.Environment PlainEnvironment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Color,
        BackgroundColor = new Color(0f, 0f, 0f, 0f),
        TonemapMode = Godot.Environment.ToneMapper.Linear,
    };

    private static ArrayMesh? _clipQuad;

    /// <summary>
    /// A quad whose positions the dilate stage reads as clip coordinates: the stage takes the
    /// file's axes (model (x, y, z) is the file's (x, -z, y)), so corner (u, v) sits at model
    /// (2u - 1, 0.5, 1 - 2v), which is Godot clip (2u - 1, 2v - 1, 0.5): uv (0, 0) at the top left.
    /// </summary>
    private static ArrayMesh ClipQuad()
    {
        if (_clipQuad is not null)
        {
            return _clipQuad;
        }
        var uvs = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
        var vertices = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            vertices[i] = new Vector3(2f * uvs[i].X - 1f, 0.5f, 1f - 2f * uvs[i].Y);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        _clipQuad = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(_clipQuad, Mesh.PrimitiveType.Triangles, arrays);
        arrays.Dispose();
        return _clipQuad;
    }

    /// <summary>Sets every furred instance's shells for the frame. Main thread, before the constants publish.</summary>
    public void Pump(Camera3D camera)
    {
        while (Pending.TryDequeue(out MeshInstance3D? attach))
        {
            AttachNow(attach);
        }
        _gone.Clear();
        float b = Engine_ClassicBillboards.BillboardBrightness();
        float near = camera.Near;
        float halfTan = MathF.Tan(Mathf.DegToRad(camera.Fov) * 0.5f);
        Camera3D? secondCamera = Engine_ClassicViews.Count > 1 ? Engine_ClassicViews.Camera(1) : null;
        foreach ((ulong id, Furred furred) in _furred)
        {
            if (!GodotObject.IsInstanceValid(furred.Source) || !furred.Source.IsInsideTree())
            {
                Release(furred);
                _gone.Add(id);
                continue;
            }
            if (secondCamera is not null)
            {
                SplitPrelit(furred);
            }
            else
            {
                JoinPrelit(furred);
            }
            // The shells and their prelit pair are not faded with the body: drawn whole while its
            // colour pass is built, else not at all (Engine_EntityAppearance, "THE FUR SHELLS DO
            // NOT FADE"; the prelit list push follows the same gate, `sub_82219AB8` 0x82219FD4, disc
            // 0x8221A104).
            bool visible = furred.Source.IsVisibleInTree() &&
                Refii.Entities.Engine_EntityAppearance.IsSubmitted(furred.Source);
            if (furred.Shown != visible)
            {
                furred.Shown = visible;
                furred.Shell.Visible = visible;
            }
            if (visible)
            {
                (float fade, bool depth) = Refii.Entities.Engine_EntityAppearance.FadeOf(furred.Source);
                if (furred.Transparency != fade)
                {
                    furred.Transparency = fade;
                    furred.Shell.Transparency = fade;
                }
                furred.Depth = depth && fade > 0f;
            }
            Update(furred.FurPass, visible);
            Update(furred.SkinPass, visible);
            Update(furred.SecondFurPass, visible);
            Update(furred.SecondSkinPass, visible);
            if (!visible)
            {
                continue;
            }
            Aabb box = furred.Source.GlobalTransform * furred.Source.GetAabb();
            float radius = box.Size.Length() * 0.5f;
            float d = MathF.Max((camera.GlobalPosition - box.GetCenter()).Length() - radius, near);
            int n = Math.Clamp((int)((furred.Length / d) / halfTan * 142.85713f + 1f), 16, MaxShells);
            // The shells are the node's: on split screen each view's count from the camera that
            // draws it, and the nearer view's (the finer) wins.
            for (int v = 0; v < Engine_ClassicViews.Count && Engine_ClassicViews.Count > 1; v++)
            {
                if (Engine_ClassicViews.Camera(v) is not { } other)
                {
                    continue;
                }
                float dv = MathF.Max((other.GlobalPosition - box.GetCenter()).Length() - radius, other.Near);
                float tv = MathF.Tan(Mathf.DegToRad(other.Fov) * 0.5f);
                n = Math.Max(n, Math.Clamp((int)((furred.Length / dv) / tv * 142.85713f + 1f), 16, MaxShells));
                d = MathF.Min(d, dv);
            }
            n = Refii.AndroidRuntime.AndroidFurShells.Cap(n);
            float length = furred.Length * (1f + 0.1f * d);
            Link(furred, n - 1);
            Mist(furred, Engine_ClassicMist.VolumeFor(box));

            PrelitFrame(furred.FurPass, camera, b);
            PrelitFrame(furred.SkinPass, camera, b);
            if (secondCamera is not null)
            {
                PrelitFrame(furred.SecondFurPass, secondCamera, b);
                PrelitFrame(furred.SecondSkinPass, secondCamera, b);
            }

            for (int v = 0; v < Engine_ClassicViews.Count; v++)
            {
                ShellRows(furred, v, n, length, b);
            }
            if (furred.Mist is not null)
            {
                // The volume's registers over the frame's, in every view's copy (SetRowBlock).
                for (int layer = 1; layer < n; layer++)
                {
                    Engine_ClassicMist.WriteRegisters(furred.Mist, furred.Rows[layer - 1]);
                }
            }
        }
        foreach (ulong id in _gone)
        {
            _furred.Remove(id);
        }
    }

    /// <summary>
    /// The shell rows of one view (each view's copy of a row holds its own frame: the eye the
    /// shells' lighting reads), with the furred instance's own registers.
    /// </summary>
    private static void ShellRows(Furred furred, int view, int n, float length, float b)
    {
        Engine_ClassicConstants.CopyFrameView(view, true, FramePs);
        Engine_ClassicConstants.CopyFrameView(view, false, FrameVs);
        Set(FrameVs, 8, length);
        Set(FrameVs, 47, furred.Occlusion);
        Set(FrameVs, 113, 1f / MathF.Max(length * 200f, 1e-6f));
        Set(FramePs, 31, 0.3f * b);
        for (int layer = 1; layer < n; layer++)
        {
            float t = layer / (float)n;
            Set(FrameVs, 46, t);
            int row = furred.Rows[layer - 1];
            Engine_ClassicConstants.SetRowBlockView(view, true, row, 0, FramePs);
            Engine_ClassicConstants.SetRowBlockView(view, false, row, 0, FrameVs);
        }
    }

    private static readonly int[] GroupRows = new int[3], GroupCounts = new int[3];
    private static readonly bool[] GroupSingle = new bool[3];
    private static readonly StringName[] RowUniforms = { "xr_light_row", "xr_light_row2", "xr_light_row3" };

    /// <summary>
    /// A prelit target's frame: its camera on the main one (the draw is culled by it), and one
    /// pass per light group the instance drew with on its last assignment (the renderable's own
    /// groups, `sub_821B4838` -> `sub_82189ED8`; the frame's row when it has none). Each pass's
    /// row is the group's with the prelit exposure 1 / (0.3 B) in PS c20.w and VS c54.w
    /// (`sub_821B1958` at 0x82268E40), and for the fur the FURMATERIAL constants (`sub_821A5220`,
    /// `sub_821B4838`): c31 g_SpecularColorAndDecay = (0.2, 0.2, 0.2, 9), c46
    /// g_SecondarySpecularColorAndDecay = (0.3, 0.3, 0.35, 7), c47 = (2, 0.1, ambient, 0) with the
    /// ambient factor 1 on the first pass and 0 after (0x821B48B0).
    /// </summary>
    private void PrelitFrame(Prelit? prelit, Camera3D camera, float b)
    {
        if (prelit is null)
        {
            return;
        }
        prelit.Camera.GlobalTransform = camera.GlobalTransform;
        prelit.Camera.Fov = camera.Fov;
        prelit.Camera.Near = camera.Near;
        prelit.Camera.Far = camera.Far;
        prelit.Camera.KeepAspect = camera.KeepAspect;

        int groups = Engine_ClassicLitInstances.GroupsOf(prelit.Source, GroupRows, GroupCounts, GroupSingle);
        int passes = Math.Max(groups, 1);
        string shape = groups == 0 ? "frame" : $"{groups}:{GroupCounts[0]}{GroupSingle[0]}:{GroupCounts[1]}{GroupSingle[1]}:{GroupCounts[2]}{GroupSingle[2]}";
        bool made = shape != prelit.Shape;
        if (made)
        {
            prelit.Shape = shape;
            ShaderMaterial? first = null, previous = null;
            for (int g = 0; g < passes; g++)
            {
                ShaderMaterial? pass = PassMaterial(prelit, g, groups == 0 ? 0 : GroupCounts[g], groups != 0 && GroupSingle[g]);
                if (pass is null)
                {
                    break;
                }
                if (previous is null)
                {
                    first = pass;
                }
                else
                {
                    previous.NextPass = pass;
                }
                pass.NextPass = null;
                previous = pass;
            }
            prelit.Node.MaterialOverride = first;
        }
        BindCascades(prelit, made);

        float exposure = 1f / MathF.Max(0.3f * b, 1e-4f);
        for (int g = 0; g < passes; g++)
        {
            if (prelit.Rows[g] == 0)
            {
                prelit.Rows[g] = Engine_ClassicConstants.AcquireRow();
                prelit.Node.SetInstanceShaderParameter(RowUniforms[g], prelit.Rows[g]);
            }
            // The view's copy of the group's row: its eye and its cascades.
            int from = groups == 0 ? 0 : GroupRows[g];
            Engine_ClassicConstants.CopyRowView(prelit.View, true, from, FramePs);
            Engine_ClassicConstants.CopyRowView(prelit.View, false, from, FrameVs);
            FramePs[Engine_ClassicRegisters.Ps.GlobalAmbientAndBrightness * 4 + 3] = exposure;
            FrameVs[Engine_ClassicRegisters.Vs.GlobalAmbientAndBrightness * 4 + 3] = exposure;
            if (prelit.Fur)
            {
                SetVec(FramePs, 31, 0.2f, 0.2f, 0.2f, 9f);
                SetVec(FramePs, 46, 0.3f, 0.3f, 0.35f, 7f);
                SetVec(FramePs, 47, 2f, 0.1f, g == 0 ? 1f : 0f, 0f);
            }
            Engine_ClassicConstants.SetRowBlock(true, prelit.Rows[g], 0, FramePs);
            Engine_ClassicConstants.SetRowBlock(false, prelit.Rows[g], 0, FrameVs);
        }
    }

    /// <summary>
    /// Keeps a prelit target's passes on its view's cascade maps (Engine_ClassicShadows
    /// CascadeTextureFor): the bound pair holds whichever view copied last, so while split each
    /// view's passes read that view's own maps. Rebinds when the maps move, and when new passes
    /// were made (<paramref name="made"/>) on other maps than the bound pair they start on.
    /// </summary>
    private static void BindCascades(Prelit prelit, bool made)
    {
        if (Engine_ClassicShadows.Instance is not { } shadows)
        {
            return;
        }
        Texture2D? first = shadows.CascadeTextureFor(prelit.View, 0);
        bool moved = !ReferenceEquals(first, prelit.Cascades);
        if (!moved && !(made && !ReferenceEquals(first, shadows.CascadeTexture(0))))
        {
            return;
        }
        prelit.Cascades = first;
        Texture2D? second = shadows.CascadeTextureFor(prelit.View, 1);
        foreach (ShaderMaterial pass in prelit.Cache.Values)
        {
            if (pass.GetShaderParameter(Cascade0Name).VariantType != Variant.Type.Nil)
            {
                pass.SetShaderParameter(Cascade0Name, first is not null ? Variant.From(first) : default);
            }
            if (pass.GetShaderParameter(Cascade1Name).VariantType != Variant.Type.Nil)
            {
                pass.SetShaderParameter(Cascade1Name, second is not null ? Variant.From(second) : default);
            }
        }
    }

    private static void SetVec(float[] into, int register, float x, float y, float z, float w)
    {
        int at = register * 4;
        into[at] = x;
        into[at + 1] = y;
        into[at + 2] = z;
        into[at + 3] = w;
    }

    /// <summary>
    /// The shells' mist (`Render_LodMeshDraw_INFERRED` 0x821F1A08..1A20): PS 127 + (the instance's
    /// box is in a mist volume, `sub_82B1DE38`), the volume's c72..c76 and s11 / s12 bound with b1
    /// (`Render_BindHighSamplersAndPixelBool_INFERRED`).
    /// </summary>
    private void Mist(Furred furred, Engine_ClassicMist.Volume? volume)
    {
        if (furred.MistKnown && ReferenceEquals(volume, furred.Mist))
        {
            return;
        }
        furred.MistKnown = true;
        if (volume is not null && _mistShader is null)
        {
            _mistShader = Engine_ClassicShaders.Compose(ShellVertex, ShellMistPixel,
                "unshaded, depth_draw_opaque, cull_back, shadows_disabled, fog_disabled", 0, ShellDefines);
        }
        if (volume is not null && _mistShader is null)
        {
            volume = null;
        }
        furred.Mist = volume;
        foreach (ShaderMaterial pass in furred.Passes)
        {
            pass.Shader = volume is null ? _shader : _mistShader;
            pass.SetShaderParameter("xr_booleans", volume is null ? 0u : 1u << 17);
            if (volume is not null)
            {
                pass.SetShaderParameter("ps_g_MistDensitySampler", volume.Density64);
                pass.SetShaderParameter("ps_g_MistHeightSampler", Engine_ClassicMist.FlatHeight);
            }
        }
    }

    /// <summary>Chains the first <paramref name="count"/> shell passes; each its slice.</summary>
    private void Link(Furred furred, int count)
    {
        ShaderMaterial[]? depth = furred.Depth ? DepthPassesOf(furred) : null;
        if (furred.Linked == count && furred.LinkedDepth == (depth is not null))
        {
            return;
        }
        furred.Linked = count;
        furred.LinkedDepth = depth is not null;
        int n = count + 1;
        for (int i = 0; i < furred.Passes.Length; i++)
        {
            ShaderMaterial pass = furred.Passes[i];
            pass.NextPass = i + 1 < count ? furred.Passes[i + 1]
                : i + 1 == count && depth is not null ? depth[0] : null;
            if (depth is not null)
            {
                depth[i].NextPass = i + 1 < count ? depth[i + 1] : null;
            }
            if (i < count && pass.GetMeta("fur_slices").AsGodotArray() is { Count: Slices } slices)
            {
                float t = Math.Clamp((i + 1) / (float)n, 0f, 0.9999f);
                Variant slice = slices[(int)(t * Slices)];
                pass.SetShaderParameter("ps_g_FurTexture", slice);
                depth?[i].SetShaderParameter("ps_g_FurTexture", slice);
            }
        }
    }

    /// <summary>
    /// The shells' depth passes: PS 0105 on the shell stage writing no colour and no alpha
    /// (blend_add, depth_draw_always), each on its colour pass's row and cut. The misted shells'
    /// PS 0106 cuts on the same alpha (the slice's .w x COLOR.y + COLOR.w), so one composition
    /// serves both.
    /// </summary>
    private ShaderMaterial[]? DepthPassesOf(Furred furred)
    {
        if (furred.DepthPasses is not null)
        {
            return furred.DepthPasses;
        }
        if (_depthShader is null && !_depthTried)
        {
            _depthTried = true;
            _depthShader = Engine_ClassicShaders.Compose(ShellVertex, ShellPixel,
                "unshaded, blend_add, depth_draw_always, cull_back, shadows_disabled, fog_disabled", 0,
                ShellDefines + "\n#define XR_COLOUR_OUT(c) vec3(0.0)\n#define XR_ALPHA_OUT(a) 0.0");
        }
        if (_depthShader is null)
        {
            return null;
        }
        var passes = new ShaderMaterial[furred.Passes.Length];
        for (int i = 0; i < passes.Length; i++)
        {
            var pass = new ShaderMaterial { Shader = _depthShader, RenderPriority = (int)Material.RenderPriorityMin };
            pass.SetShaderParameter(ShellRow, furred.Rows[i]);
            pass.SetShaderParameter("xr_spec_constants", 2u);
            pass.SetShaderParameter("xr_alpha_threshold", 16f / 255f);
            passes[i] = pass;
        }
        furred.Linked = -1;
        return furred.DepthPasses = passes;
    }

    private static void Set(float[] into, int register, float value)
    {
        int at = register * 4;
        into[at] = value;
        into[at + 1] = value;
        into[at + 2] = value;
        into[at + 3] = value;
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
        // Each shell pass reads its own row: the pass's layer and the frame's constants.
        _shader = Engine_ClassicShaders.Compose(ShellVertex, ShellPixel,
            "unshaded, depth_draw_opaque, cull_back, shadows_disabled, fog_disabled", 0, ShellDefines);
        // Before the translator carried TEXCOORD2 whole the stage read the fur uv's drift as zero.
        if (_shader is null || !_shader.Code.Contains("CUSTOM0", StringComparison.Ordinal))
        {
            GD.Print("[classic] the fur shell shaders predate the fur record's lanes; fur stays off until the shaders are compiled again");
            _shader = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The furred surfaces' triangles as a mesh of their own, skinned as the source, with the fur
    /// record per vertex in COLOR and CUSTOM0. Null when no source vertex maps to a fur block.
    /// </summary>
    internal static ArrayMesh? Shells(ArrayMesh source)
    {
        ulong key = source.GetInstanceId();
        if (ShellMeshes.TryGetValue(key, out ArrayMesh? cached))
        {
            return cached;
        }
        ArrayMesh? built = BuildShells(source);
        ShellMeshes[key] = built;
        return built;
    }

    private static ArrayMesh? BuildShells(ArrayMesh source)
    {
        if (!source.HasMeta(Engine_MdlMesh.SourceVertexMapMeta) ||
            source.GetMeta(Engine_MdlMesh.SourceVertexMapMeta).AsInt32Array() is not { } sourceMap)
        {
            return null;
        }
        Godot.Collections.Array blocks = source.GetMeta(Engine_MdlMesh.FurMeta).AsGodotArray();
        var mesh = new ArrayMesh();
        var used = new List<int>();
        int surfaceStart = 0;
        for (int s = 0; s < source.GetSurfaceCount(); s++)
        {
            int vertexCount = source.SurfaceGetArrayLen(s);
            int start = surfaceStart;
            surfaceStart += vertexCount;
            foreach (Variant blockVariant in blocks)
            {
                Godot.Collections.Dictionary block = blockVariant.AsGodotDictionary();
                int furBase = block["base"].AsInt32(), furCount = block["count"].AsInt32();
                bool any = false;
                for (int v = 0; v < vertexCount && start + v < sourceMap.Length; v++)
                {
                    int src = sourceMap[start + v];
                    if (src >= furBase && src < furBase + furCount)
                    {
                        any = true;
                        break;
                    }
                }
                if (any)
                {
                    AddShellSurface(mesh, source, s, start, sourceMap, block);
                    used.Add(s);
                }
            }
        }
        mesh.SetMeta(SourceSurfacesMeta, used.ToArray());
        return mesh.GetSurfaceCount() != 0 ? mesh : null;
    }

    /// <summary>Shell mesh metadata: the source surfaces its triangles came from.</summary>
    internal static readonly StringName SourceSurfacesMeta = "fur_source_surfaces";

    private static void AddShellSurface(ArrayMesh into, ArrayMesh source, int surface, int start, int[] sourceMap,
        Godot.Collections.Dictionary block)
    {
        int furBase = block["base"].AsInt32(), furCount = block["count"].AsInt32();
        Color[] colours = block["colour"].AsColorArray();
        float[] lanes = block["lanes"].AsFloat32Array();
        Godot.Collections.Array arrays = Engine_MdlSurfaceArrays.Of(source, surface);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        int[] bones = arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil
            ? Array.Empty<int>() : arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
        float[] weights = arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.Nil
            ? Array.Empty<float>() : arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int n = vertices.Length;
        int influences = n > 0 && bones.Length == n * 8 ? 8 : 4;

        var remap = new int[n];
        Array.Fill(remap, -1);
        var outVertices = new List<Vector3>();
        var outNormals = new List<Vector3>();
        var outUvs = new List<Vector2>();
        var outBones = new List<int>();
        var outWeights = new List<float>();
        var outColours = new List<Color>();
        var outLanes = new List<float>();
        var outIndices = new List<int>();

        int Map(int v)
        {
            if (remap[v] >= 0)
            {
                return remap[v];
            }
            int local = sourceMap[start + v] - furBase;
            remap[v] = outVertices.Count;
            outVertices.Add(vertices[v]);
            outNormals.Add(v < normals.Length ? normals[v] : Vector3.Up);
            outUvs.Add(v < uvs.Length ? uvs[v] : Vector2.Zero);
            for (int k = 0; k < influences && bones.Length != 0; k++)
            {
                outBones.Add(bones[v * influences + k]);
                outWeights.Add(weights[v * influences + k]);
            }
            outColours.Add(colours[local]);
            for (int k = 0; k < 4; k++)
            {
                outLanes.Add(lanes[local * 4 + k]);
            }
            return remap[v];
        }

        bool Fur(int v)
        {
            if (start + v >= sourceMap.Length)
            {
                return false;
            }
            int src = sourceMap[start + v];
            return src >= furBase && src < furBase + furCount;
        }

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (!Fur(a) || !Fur(b) || !Fur(c))
            {
                continue;
            }
            outIndices.Add(Map(a));
            outIndices.Add(Map(b));
            outIndices.Add(Map(c));
        }
        if (outIndices.Count == 0)
        {
            return;
        }

        Godot.Collections.Dictionary? lods = Refii.AndroidRuntime.AndroidFurShellLods.For(source, surface, Fur, Map, outIndices.Count);
        var shell = new Godot.Collections.Array();
        shell.Resize((int)Mesh.ArrayType.Max);
        shell[(int)Mesh.ArrayType.Vertex] = outVertices.ToArray();
        shell[(int)Mesh.ArrayType.Normal] = outNormals.ToArray();
        shell[(int)Mesh.ArrayType.TexUV] = outUvs.ToArray();
        shell[(int)Mesh.ArrayType.Color] = outColours.ToArray();
        shell[(int)Mesh.ArrayType.Custom0] = outLanes.ToArray();
        shell[(int)Mesh.ArrayType.Index] = outIndices.ToArray();
        Mesh.ArrayFormat format = Mesh.ArrayFormat.FormatCustom0 |
            (Mesh.ArrayFormat)((int)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        if (outBones.Count != 0)
        {
            shell[(int)Mesh.ArrayType.Bones] = outBones.ToArray();
            shell[(int)Mesh.ArrayType.Weights] = outWeights.ToArray();
            if (influences == 8)
            {
                format |= Mesh.ArrayFormat.FlagUse8BoneWeights;
            }
        }
        Refii.Core.Engine_ArrayMeshes.AddSurface(into, Mesh.PrimitiveType.Triangles, shell, null, lods, format);
        shell.Dispose();
    }

    /// <summary>The fur set for one falloff (`sub_82A4C400`; see the remarks), built once.</summary>
    private static ImageTexture[] SlicesFor(float falloff)
    {
        if (SlicesByFalloff.TryGetValue(falloff, out ImageTexture[]? held))
        {
            return held;
        }
        Image[] images = SliceImages(falloff);
        var slices = new ImageTexture[Slices];
        for (int slice = 0; slice < Slices; slice++)
        {
            slices[slice] = ImageTexture.CreateFromImage(images[slice]);
        }
        SlicesByFalloff[falloff] = slices;
        return slices;
    }

    private static readonly Dictionary<float, Texture2DArray> SliceArraysByFalloff = new();

    /// <summary>The same set as one array, layer = slice, for the modern shells.</summary>
    internal static Texture2DArray SliceArrayFor(float falloff)
    {
        if (SliceArraysByFalloff.TryGetValue(falloff, out Texture2DArray? held))
        {
            return held;
        }
        var array = new Texture2DArray();
        var slices = new Godot.Collections.Array<Image>(SliceImages(falloff));
        array.CreateFromImages(slices);
        GC.KeepAlive(slices);
        SliceArraysByFalloff[falloff] = array;
        return array;
    }

    private static Image[] SliceImages(float falloff)
    {
        const int texels = SliceSize * SliceSize;
        var luminance = new byte[texels];
        var alpha = new byte[Slices * texels];
        uint seed = 235794u;
        int Rand()
        {
            seed = seed * 0x343FDu + 0x269EC3u;
            return (int)((seed >> 16) & 0x7FFF);
        }
        for (int texel = 0; texel < texels; texel++)
        {
            Rand();
            Rand();
            float v = Rand() * (1f / 65536f) + 0.5f;
            luminance[texel] = (byte)(long)(v * 255f);
            float r = Rand() * (1f / 32768f);
            int h = Math.Min((int)((0.2f + 1.6f * (float)Math.Pow(r, 3.0)) * Slices), Slices);
            if (Rand() * (1f / 32768f) >= 1.0f)
            {
                h = 0;
            }
            for (int j = 1; j <= h; j++)
            {
                float t = 2f * j / h - 1f;
                float p = t > 0f ? (float)Math.Pow(t, 2.0) : 0f;
                alpha[(j - 1) * texels + texel] = (byte)(int)((1f - falloff * p) * 255f + 0.5f);
            }
        }
        var images = new Image[Slices];
        var bytes = new byte[texels * 2];
        for (int slice = 0; slice < Slices; slice++)
        {
            for (int texel = 0; texel < texels; texel++)
            {
                bytes[texel * 2] = luminance[texel];
                bytes[texel * 2 + 1] = alpha[slice * texels + texel];
            }
            Image image = Image.CreateFromData(SliceSize, SliceSize, false, Image.Format.La8, bytes);
            image.GenerateMipmaps();
            images[slice] = image;
        }
        return images;
    }
}
