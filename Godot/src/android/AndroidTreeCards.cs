using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `tree_billboards`: distant trees drawn as flat pictures of their models, as the original game drew them.
/// Each tree's impostor model (its `_LOD1`, else its full model) ends where its card has taken over (the
/// stated F, times `tree_billboard_start_scale`), and from 0.75 of that to its cull every placement is one
/// camera-facing card showing a picture of that model (tree_cards.gdshader). Android only; read once,
/// because the stamps are written when a level's props are built.
/// Up to its start a tree draws what it draws without cards: past start scale 1 a placed tree whose
/// definition names no LOD model borrows its `_LOD1` as it does without cards
/// (Engine_TreeReach.PlacedTreeBorrows), so its full model still ends at the hand-over.
/// </summary>
/// <remarks>
/// <para>THE PICTURES. Every tree model with a stamped node (a kind) has a 128-pixel cell in one atlas,
/// 8 cells across (<see cref="AndroidTreeCardAtlas"/>). A capture viewport of its own world draws kinds'
/// models on their own lite materials, with the instance lighting of their first placement, under a copy
/// of the level's sun (no shadow) and ambient, through an orthographic camera turned to the view's
/// horizontal heading. The atlas effect copies each drawn cell into the kind's cell and makes its mips.
/// The stage stands 5 km above the level and is half a metre a cell, so the ground lookups find nothing
/// and the camera, a metre from the cells, sees them unfogged.</para>
/// <para>A CAPTURE is a pass over kinds, `tree_billboard_kinds_per_frame` of them a frame (0: all in one
/// frame). Passes run when an area's tree models have stopped arriving for <see cref="SettleFrames"/>
/// frames (the ones without a picture), when the light has moved (<see cref="AndroidTreeCardMaths.LightReason"/>),
/// and, only with `tree_billboard_turn_degrees` above 0, when the view has turned that far from the last full
/// pass's heading. The turn is 0 by default and in every preset (revision 25): a recapture on turning swapped
/// every card's picture while the camera panned. So a picture keeps the heading the view had when its pass
/// began (the area's first pass, or the last light pass), and a tree seen from the other side shows its
/// sunlit half on the wrong side. The cards show once the area's first pass is complete; a model that
/// arrives later shows its cards once it has its picture.</para>
/// <para>THE CARDS. One MultiMesh for every kind: per placement the world centre of the model's bound
/// sphere, its radius and (cell, start, cull, has a picture). One kind's placements are re-read a frame;
/// the buffer is uploaded only when something changed. `tree_far_reach` still ends the cards when nearer
/// than the stated cull.</para>
/// <para>SPLIT SCREEN. The capture follows one view, so while the screen is split the stamped models are
/// given their full reach back and no cards are drawn: the picture without `tree_billboards`.</para>
/// <para>`tree_billboards_dump_atlas` (refii.toml) writes the atlas to user://tree_cards_atlas.png after
/// an area's first pass.</para>
/// <para>DEBUG SWITCHES (refii.toml, off, in no preset; read once), for a launch A/B of where the cards'
/// cost goes: <see cref="HideCardsKey"/> never shows the cards, <see cref="OpaqueCutKey"/> cuts them
/// without a blending pipeline, <see cref="DrawLastKey"/> sorts them as the farthest opaque draw and
/// <see cref="FirstCaptureOnlyKey"/> stops the light and turn recaptures. The capture viewport is
/// drawn only on a capture frame (update mode Disabled between them) and the atlas effect sits on the
/// capture camera's compositor, so neither touches the main view's render.</para>
/// </remarks>
public partial class AndroidTreeCards : Node
{
    /// <summary>The refii.toml key.</summary>
    public const string Key = "tree_billboards";

    /// <summary>Degrees the view turns before the atlas is captured again; 0 keeps the heading of the last capture.</summary>
    public const string TurnKey = "tree_billboard_turn_degrees";

    /// <summary><see cref="TurnKey"/>'s default.</summary>
    public const float TurnDefault = 0f;

    /// <summary>Tree models captured a frame; 0 captures all of them in one frame.</summary>
    public const string PerFrameKey = "tree_billboard_kinds_per_frame";

    /// <summary><see cref="PerFrameKey"/>'s default.</summary>
    public const int PerFrameDefault = 4;

    /// <summary>Where cards take over, as a multiple of each tree's stated F.</summary>
    public const string StartKey = "tree_billboard_start_scale";

    /// <summary>The debug key that writes the atlas to a PNG once an area.</summary>
    public const string DumpKey = "tree_billboards_dump_atlas";

    /// <summary>Debug: the cards are never shown; captures, uploads and the models' hand-over run as usual.</summary>
    public const string HideCardsKey = "tree_billboards_debug_hide_cards";

    /// <summary>Debug: the cards cut by a plain discard with ALPHA unwritten, so their pipeline does not blend.</summary>
    public const string OpaqueCutKey = "tree_billboards_debug_opaque_cut";

    /// <summary>Debug: the cards sort as the farthest opaque draw instead of at their region's middle.</summary>
    public const string DrawLastKey = "tree_billboards_debug_draw_last";

    /// <summary>Debug: after an area's first pass only new models are captured; no light or turn recaptures.</summary>
    public const string FirstCaptureOnlyKey = "tree_billboards_debug_first_capture_only";

    /// <summary>The define <see cref="OpaqueCutKey"/> composes into the card shader.</summary>
    public const string OpaqueCutDefine = "TREE_CARDS_OPAQUE_CUT";

    /// <summary>
    /// The cards' sorting offset under <see cref="DrawLastKey"/>: their sort depth (distance minus offset)
    /// lands in the last front-to-back band and depth layer (godot-patches/0012), whatever the camera.
    /// </summary>
    public const float DrawLastOffset = -100000f;

    /// <summary>The card shader.</summary>
    public const string ShaderPath = "res://src/android/tree_cards.gdshader";

    /// <summary>Frames an area's tree models must stop arriving for before they are captured.</summary>
    public const int SettleFrames = 30;

    /// <summary>The fewest frames from the end of a pass to a pass for the light.</summary>
    public const int MinCaptureFrames = 30;

    /// <summary>The fewest frames from the end of a pass to a pass for a turn.</summary>
    public const int TurnGapFrames = 10;

    /// <summary>Frames from an area's first tree model after which the loading screen stops waiting for the first capture.</summary>
    private const int LoadWaitFrames = 900;

    /// <summary>Frames a capture's atlas job may take before the cards give up on it.</summary>
    private const int JobTimeoutFrames = 300;

    /// <summary>The sun and the ambient are compared with the last full pass's every this many frames.</summary>
    private const int LightCheckFrames = 15;

    /// <summary>A cell's side at the capture stage, in metres.</summary>
    private const float CellMetres = 0.5f;

    /// <summary>The capture camera stands this far in front of the cells.</summary>
    private const float CaptureDepth = 1f;

    /// <summary>Floats a card takes in the MultiMesh buffer: the 3x4 transform and the custom data.</summary>
    private const int Stride = 16;

    /// <summary>Where the capture's models stand, far above any level.</summary>
    private static readonly Vector3 Stage = new(0f, 5000f, 0f);

    /// <summary>The four debug switches (all off by default, in no preset).</summary>
    public readonly record struct DebugSwitches(bool HideCards, bool OpaqueCut, bool DrawLast, bool FirstCaptureOnly)
    {
        public bool Any => HideCards || OpaqueCut || DrawLast || FirstCaptureOnly;
    }

    private readonly record struct Choice(bool On, bool Dump, float Turn, int PerFrame, float StartScale, DebugSwitches Debug);

    private static readonly Lazy<Choice> Chosen = new(() =>
    {
        try
        {
            if (!Engine_Platform.IsAndroid)
            {
                return default;
            }

            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            return Read(values);
        }
        catch (Exception)
        {
            return default;
        }
    });

    /// <summary>The settings as <paramref name="values"/> give them, held to their ranges. Pure.</summary>
    private static Choice Read(IReadOnlyDictionary<string, string> values) => new(
        Engine_RefiiConfig.Bool(values, Key, false),
        Engine_RefiiConfig.Bool(values, DumpKey, false),
        AndroidTreeCardMaths.TurnLimit(Engine_RefiiConfig.Float(values, TurnKey, TurnDefault)),
        AndroidTreeCardMaths.KindsPerFrame(Engine_RefiiConfig.Int(values, PerFrameKey, PerFrameDefault)),
        AndroidTreeCardMaths.StartScale(Engine_RefiiConfig.Float(values, StartKey, 1f)),
        Debug(values));

    /// <summary>The debug switches as <paramref name="values"/> give them. Pure.</summary>
    public static DebugSwitches Debug(IReadOnlyDictionary<string, string> values) => new(
        Engine_RefiiConfig.Bool(values, HideCardsKey, false),
        Engine_RefiiConfig.Bool(values, OpaqueCutKey, false),
        Engine_RefiiConfig.Bool(values, DrawLastKey, false),
        Engine_RefiiConfig.Bool(values, FirstCaptureOnlyKey, false));

    /// <summary>(turn degrees, kinds a frame, start scale) as <paramref name="values"/> give them. Pure, for tests.</summary>
    public static (float Turn, int PerFrame, float StartScale) Levers(IReadOnlyDictionary<string, string> values)
    {
        Choice choice = Read(values);
        return (choice.Turn, choice.PerFrame, choice.StartScale);
    }

    /// <summary>This process's choice: false off Android. Any thread.</summary>
    public static bool On => Chosen.Value.On;

    /// <summary>`tree_billboard_start_scale` as this process reads it, held to [1, 8] (0 off Android). Any thread.</summary>
    public static float StartScaleChosen => Chosen.Value.StartScale;

    /// <summary>A stamped node of a kind, with its own {F, cull} stamp and where its card takes over.</summary>
    private readonly record struct Source(GeometryInstance3D Node, ulong Id, float F, float StatedCull, float Start);

    /// <summary>One placement's card: world centre in xyz and radius in w; its node's start and stated cull.</summary>
    private readonly record struct Card(Vector4 Sphere, float Start, float StatedCull);

    private sealed class Kind
    {
        public required string Model;
        public required Mesh Mesh;
        /// <summary>Bound sphere in the file's axes {x, y, z, r}.</summary>
        public Vector4 Sphere;
        /// <summary>The nearest of its nodes' starts, where a model ends and its card has taken over
        /// (<see cref="AndroidTreeCardMaths.Start"/>); each card carries its own node's.</summary>
        public float Start;
        public int Cell;
        public readonly List<Source> Sources = new();
        public readonly HashSet<ulong> SourceIds = new();
        public bool Dirty = true;
        /// <summary>The placements as last read.</summary>
        public List<Card> Records = new();
        public int Signature;
        public GeometryInstance3D? Capture;
        /// <summary>Its cell holds a picture.</summary>
        public bool Pictured;
        /// <summary>No source could be copied into the capture; tried again when a source arrives.</summary>
        public bool Failed;
    }

    /// <summary>One capture: the kinds, in batches, and the light and heading when it began.</summary>
    private sealed class Pass
    {
        public required string Why;
        public required List<Kind> Kinds;
        public bool Full;
        public int Next;
        public int Frames;
        public int Placed;
        public double MainMs;
    }

    private static readonly Dictionary<string, Kind> Kinds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Kind> Order = new();
    private static ulong _kindAddedFrame;
    private static ulong _areaFirstFrame;
    private static bool _split;
    private static AndroidTreeCards? _instance;

    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private DirectionalLight3D _light = null!;
    private AndroidTreeCardAtlas _atlas = null!;
    private MultiMesh _multi = null!;
    private MultiMeshInstance3D _cards = null!;
    private MultiMeshInstance3D _warm = null!;
    private ShaderMaterial? _material;
    private List<Card> _scratch = new();
    private int _cursor;
    private int _count;
    private float _uploadedReach = float.NaN;
    private bool _flagsChanged;

    private DirectionalLight3D? _sun;
    private ulong _sunSearchFrame;

    // The last full pass: its heading, light and start time; when the last pass ended.
    private float _capturedYaw;
    private Vector3 _capturedSun, _capturedSunColour, _capturedAmbient;
    private double _capturedSeconds;
    private ulong _passEndFrame;
    private bool _lightStale;

    // The pass under way and its batch on the render thread.
    private Pass? _pass;
    private long _jobId;
    private long _submitted;
    private ulong _submittedFrame;
    private readonly List<Kind> _batch = new();
    private readonly List<GeometryInstance3D> _shown = new();

    private bool _firstDone;

    /// <summary>With turn recaptures off, the heading every pass in this area captures from (its first pass's).</summary>
    private Vector3? _areaHeading;
    private bool _warnedNoAtlas;
    private bool _dumped;

    private static StringName? _atlasName, _cellsName, _lightmapColourName, _ambientScaleName, _bakedAoName,
        _probeRName, _probeGName, _probeBName;

    /// <summary>
    /// A tree node whose model is its placement's impostor, once its mesh stands (Engine_PropStreamer);
    /// a node without the {F, cull} stamp is ignored. Its model's end is moved to where its card takes
    /// over, from its own stamp: two blocks of one model may state different distances. Main thread.
    /// </summary>
    public static void Register(GeometryInstance3D node, Mesh mesh)
    {
        if (!On || !node.HasMeta(Refii.Models.Engine_ClassicBillboards.Meta) ||
            !node.HasMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta) ||
            node.GetMeta(Refii.Models.Engine_ClassicBillboards.Meta).AsFloat32Array() is not { Length: >= 2 } stated ||
            !(stated[0] > 0f))
        {
            return;
        }

        string model = node.GetMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta).AsString();
        float f = stated[0], statedCull = stated[1];
        float start = AndroidTreeCardMaths.Start(f, statedCull, Chosen.Value.StartScale);
        if (!Kinds.TryGetValue(model, out Kind? kind))
        {
            kind = new Kind
            {
                Model = model, Mesh = mesh, Sphere = SphereOf(mesh), Cell = Order.Count, Start = start,
            };
            Kinds[model] = kind;
            Order.Add(kind);
            _kindAddedFrame = Engine.GetProcessFrames();
            if (Order.Count == 1)
            {
                _areaFirstFrame = _kindAddedFrame;
            }
        }

        Restamp(node, mesh, f, statedCull);
        ulong id = node.GetInstanceId();
        if (kind.SourceIds.Add(id))
        {
            kind.Sources.Add(new Source(node, id, f, statedCull, start));
            kind.Start = MathF.Min(kind.Start, start);
            kind.Dirty = true;
            kind.Failed = false;
        }
    }

    /// <summary>
    /// The node's model ending where its card takes over, or at its stated cull while no cards are drawn
    /// (split screen): its band and range rewritten and applied again. Nothing when they already end there.
    /// </summary>
    private static void Restamp(GeometryInstance3D node, Mesh mesh, float f, float statedCull)
    {
        if (!node.HasMeta(Refii.Level.Engine_PropDrawRange.TreeBandMeta) || !node.HasMeta(Refii.Level.Engine_PropDrawRange.Meta) ||
            node.GetMeta(Refii.Level.Engine_PropDrawRange.TreeBandMeta).AsFloat32Array() is not { Length: >= 6 } band ||
            node.GetMeta(Refii.Level.Engine_PropDrawRange.Meta).AsFloat32Array() is not { Length: >= 3 } stated)
        {
            return;
        }

        (float[] moved, float[] movedStated) =
            AndroidTreeCardMaths.Restamped(band, stated, f, statedCull, Chosen.Value.StartScale, !_split);
        if (ReferenceEquals(moved, band))
        {
            return;
        }

        node.SetMeta(Refii.Level.Engine_PropDrawRange.TreeBandMeta, moved);
        node.SetMeta(Refii.Level.Engine_PropDrawRange.Meta, movedStated);
        Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh);
    }

    /// <summary>For a region change, where the props go away (Engine_LevelCaches.ReleaseAndroidRegion). Main thread.</summary>
    public static void Forget()
    {
        if (_instance is { } self && IsInstanceValid(self))
        {
            self.ReleaseRegion();
        }

        Kinds.Clear();
        Order.Clear();
    }

    /// <summary>The frame's work, from a node that processes every frame (AndroidGameHost). Main thread.</summary>
    public static void Tick(Node owner)
    {
        if (!On)
        {
            return;
        }

        bool split = Refii.Game.GameSplitView.Active;
        if (split != _split)
        {
            SplitChanged(split);
        }

        if (_instance is null || !IsInstanceValid(_instance))
        {
            if (Order.Count == 0)
            {
                return;
            }

            _instance = new AndroidTreeCards { Name = "AndroidTreeCards" };
            owner.AddChild(_instance);
        }

        if (_instance.IsInsideTree() && owner.GetViewport()?.GetCamera3D() is { } camera)
        {
            _instance.Step(camera);
        }
    }

    /// <summary>
    /// The card shader's draw for the pipeline warm-up (Engine_PipelineWarmup): a hidden one-card batch of
    /// the cards' own format and material, or null before the first tree model arrives.
    /// </summary>
    public static MultiMeshInstance3D? WarmCards =>
        On && _instance is { } self && IsInstanceValid(self) && self.IsInsideTree() && self._material is not null ? self._warm : null;

    /// <summary>
    /// Whether an area's first capture is still to come, so the loading screen can wait for it
    /// (Engine_PipelineWarmup): its pipelines then compile under the loading screen.
    /// </summary>
    public static bool FirstCaptureWaiting =>
        On && !_split && _instance is { } self && IsInstanceValid(self) && self._material is not null &&
        Order.Count > 0 && self._count > 0 && !self._firstDone && !self._warnedNoAtlas &&
        Engine.GetProcessFrames() - _areaFirstFrame < LoadWaitFrames;

    private static void SplitChanged(bool split)
    {
        _split = split;
        foreach (Kind kind in Order)
        {
            foreach (Source source in kind.Sources)
            {
                if (IsInstanceValid(source.Node) && MeshOf(source.Node) is { } mesh)
                {
                    Restamp(source.Node, mesh, source.F, source.StatedCull);
                }
            }
        }

        if (_instance is { } self && IsInstanceValid(self))
        {
            self.AbortPass();
            self._lightStale = !split;
        }

        GD.Print(split
            ? "[tree-cards] split screen: tree models to their full reach, no cards"
            : "[tree-cards] one view again: cards from where they take over");
    }

    private static Mesh? MeshOf(GeometryInstance3D node) => node switch
    {
        MultiMeshInstance3D { Multimesh.Mesh: { } mesh } => mesh,
        MeshInstance3D { Mesh: { } mesh } => mesh,
        _ => null,
    };

    /// <summary>The model's bound sphere in the file's axes; the mesh box's when the mesh carries none.</summary>
    private static Vector4 SphereOf(Mesh mesh)
    {
        if (mesh.HasMeta(Refii.Models.Engine_MdlMesh.BoundSphereMeta) &&
            mesh.GetMeta(Refii.Models.Engine_MdlMesh.BoundSphereMeta).AsFloat32Array() is { Length: 4 } s && s[3] > 0f)
        {
            return new Vector4(s[0], s[1], s[2], s[3]);
        }

        Aabb box = mesh.GetAabb();
        Vector3 c = box.GetCenter();
        // Godot model (x, y, z) is the file's (x, -z, y).
        return new Vector4(c.X, -c.Z, c.Y, box.Size.Length() * 0.5f);
    }

    public override void _Ready()
    {
        _atlas = new AndroidTreeCardAtlas();
        _atlas.Resized = rows =>
        {
            if (IsInstanceValid(this))
            {
                _material?.SetShaderParameter(_cellsName ??= "atlas_cells", new Vector2(AndroidTreeCardMaths.Columns, rows));
            }
        };
        _viewport = new SubViewport
        {
            Name = "TreeCardCapture",
            OwnWorld3D = true,
            TransparentBg = true,
            // RGBA16F: the atlas effect needs it, and the atlas keeps linear, unexposed colour.
            UseHdr2D = true,
            Msaa3D = Viewport.Msaa.Disabled,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            UseTaa = false,
            UseDebanding = false,
            UseOcclusionCulling = false,
            PositionalShadowAtlasSize = 0,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Size = new Vector2I(AndroidTreeCardMaths.CellPixels, AndroidTreeCardMaths.CellPixels),
            // The pictures are the models' full detail, not the level Godot would pick for 128 pixels.
            MeshLodThreshold = 0f,
        };
        _camera = new Camera3D
        {
            Name = "TreeCardCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 0.05f,
            Far = (CaptureDepth * 2f) + CellMetres,
            Size = CellMetres,
            Current = true,
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                TonemapMode = Godot.Environment.ToneMapper.Linear,
                AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
            },
            // The atlas is written inside the capture's own render, which comes before the main view's.
            Compositor = new Compositor
            {
                CompositorEffects = new Godot.Collections.Array<CompositorEffect> { _atlas },
            },
            Transform = new Transform3D(Basis.Identity, Stage),
        };
        _light = new DirectionalLight3D { Name = "TreeCardSun", ShadowEnabled = false, Visible = false };
        _viewport.AddChild(_camera);
        _viewport.AddChild(_light);
        AddChild(_viewport);

        var quad = new QuadMesh { Size = new Vector2(2f, 2f) };
        _multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = false,
            UseCustomData = true,
            Mesh = quad,
        };
        _material = CardMaterial();
        _cards = new MultiMeshInstance3D
        {
            Name = "TreeCards",
            Multimesh = _multi,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            TopLevel = true,
            Visible = false,
        };
        if (Chosen.Value.Debug.DrawLast)
        {
            // Sorted by the middle of the region's box, the one batch can land in a near band and draw
            // before the buildings and ground that hide most of it (render_forward_mobile.cpp, _fill_render_list).
            _cards.SortingOffset = DrawLastOffset;
        }

        AddChild(_cards);
        if (Chosen.Value.Debug is { Any: true } debug)
        {
            GD.Print($"[tree-cards] debug: hide cards {debug.HideCards}, opaque cut {debug.OpaqueCut}, " +
                $"draw last {debug.DrawLast}, first capture only {debug.FirstCaptureOnly}");
        }

        // One card of the same format and material, never shown: the warm-up draws a copy of it.
        var warm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = false,
            UseCustomData = true,
            Mesh = quad,
        };
        warm.InstanceCount = 1;
        warm.SetInstanceTransform(0, Transform3D.Identity);
        warm.SetInstanceCustomData(0, new Color(0f, 0f, 0f, 0f));
        _warm = new MultiMeshInstance3D
        {
            Name = "TreeCardWarm",
            Multimesh = warm,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            TopLevel = true,
            Visible = false,
        };
        AddChild(_warm);
    }

    public override void _ExitTree()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    /// <summary>The card material: tree_cards.gdshader with FABLE_LITE and this process's fog defines.</summary>
    private ShaderMaterial? CardMaterial()
    {
        if (GD.Load<Shader>(ShaderPath) is not { } file)
        {
            GD.PushError("[tree-cards] " + ShaderPath + " did not load; no cards are drawn");
            return null;
        }

        string code = file.Code;
        foreach (string define in CardDefines(AndroidLeanShading.FogOn, Chosen.Value.Debug.OpaqueCut))
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, define);
        }

        var material = new ShaderMaterial { Shader = new Shader { Code = code } };
        // The cards are a cut-out draw: they take the cut-out class's place in the opaque order, as the
        // tree models' discard shaders do (android_discard_draw_priority; 0 and in no preset, so no change).
        if (AndroidOpaqueOrder.CutoutPriority is { } priority)
        {
            material.RenderPriority = priority;
        }

        material.SetShaderParameter(_atlasName ??= "tree_atlas", _atlas.Texture);
        material.SetShaderParameter(_cellsName ??= "atlas_cells", new Vector2(AndroidTreeCardMaths.Columns, 1f));
        return material;
    }

    /// <summary>The defines the card shader takes for <paramref name="fog"/>: the lite fog's, as the lite props take them. Pure.</summary>
    public static string[] FogDefines(AndroidLeanShading.FogPath fog) => fog switch
    {
        AndroidLeanShading.FogPath.Table => new[] { "FABLE_LITE", AndroidLeanShading.FogLeanDefine, AndroidLeanShading.FogTableDefine },
        AndroidLeanShading.FogPath.Lean => new[] { "FABLE_LITE", AndroidLeanShading.FogLeanDefine },
        _ => new[] { "FABLE_LITE" },
    };

    /// <summary>Every define the card shader takes: <see cref="FogDefines"/>, then <see cref="OpaqueCutDefine"/> when asked. Pure.</summary>
    public static string[] CardDefines(AndroidLeanShading.FogPath fog, bool opaqueCut) =>
        opaqueCut ? [.. FogDefines(fog), OpaqueCutDefine] : FogDefines(fog);

    private void Step(Camera3D camera)
    {
        if (_material is null)
        {
            return;
        }

        ulong frame = Engine.GetProcessFrames();
        if (ReadOne() || !_uploadedReach.Equals(Refii.Level.Engine_TreeReach.TreeFarReach))
        {
            _flagsChanged = true;
        }

        if (!_split)
        {
            if (_pass is not null)
            {
                Advance(camera, frame);
            }
            else
            {
                StartIfDue(camera, frame);
            }
        }

        if (_flagsChanged)
        {
            _flagsChanged = false;
            Upload();
        }

        bool show = !_split && _firstDone && _count > 0 && !Chosen.Value.Debug.HideCards;
        if (_cards.Visible != show)
        {
            _cards.Visible = show;
        }
    }

    /// <summary>Starts a pass when one is due: models without a picture, a turn, or a light change.</summary>
    private void StartIfDue(Camera3D camera, ulong frame)
    {
        if (_warnedNoAtlas)
        {
            return;
        }

        if (frame - _kindAddedFrame >= SettleFrames && _count > 0)
        {
            List<Kind>? waiting = null;
            foreach (Kind kind in Order)
            {
                if (!kind.Pictured && !kind.Failed)
                {
                    (waiting ??= new List<Kind>()).Add(kind);
                }
            }

            if (waiting is not null)
            {
                // An area's first pass takes every model, so all its pictures share one light and heading.
                Begin(camera, "models", _firstDone ? waiting : new List<Kind>(Order), !_firstDone);
                return;
            }
        }

        if (!_firstDone || Chosen.Value.Debug.FirstCaptureOnly)
        {
            return;
        }

        if (_lightStale)
        {
            Begin(camera, "one view again", new List<Kind>(Order), true);
            return;
        }

        float yaw = AndroidTreeCardMaths.YawDegrees(Heading(camera));
        if (AndroidTreeCardMaths.Turned(_capturedYaw, yaw, Chosen.Value.Turn) && frame - _passEndFrame >= TurnGapFrames)
        {
            Begin(camera, $"turn {AndroidTreeCardMaths.YawBetween(_capturedYaw, yaw):0} deg", new List<Kind>(Order), true);
            return;
        }

        if (frame - _passEndFrame >= MinCaptureFrames && frame % LightCheckFrames == 0)
        {
            (Vector3 sun, Vector3 colour) = SunNow();
            double seconds = Now() - _capturedSeconds;
            if (AndroidTreeCardMaths.LightReason(_capturedSun, sun, _capturedSunColour, colour, _capturedAmbient,
                    AmbientNow(camera), seconds) is { } reason)
            {
                Begin(camera, "light: " + reason, new List<Kind>(Order), true);
            }
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>A pass over <paramref name="kinds"/>; a full one sets the heading and light the next passes compare with.</summary>
    private void Begin(Camera3D camera, string why, List<Kind> kinds, bool full)
    {
        _pass = new Pass { Why = why, Kinds = kinds, Full = full };
        if (full)
        {
            (Vector3 sun, Vector3 colour) = SunNow();
            _capturedYaw = AndroidTreeCardMaths.YawDegrees(Heading(camera));
            _capturedSun = sun;
            _capturedSunColour = colour;
            _capturedAmbient = AmbientNow(camera);
            _capturedSeconds = Now();
            _lightStale = false;
        }

        Submit(camera, Engine.GetProcessFrames());
    }

    /// <summary>The pass's next step: once the last batch's cells are in the atlas, the next batch or the end.</summary>
    private void Advance(Camera3D camera, ulong frame)
    {
        Pass pass = _pass!;
        if (_submitted != 0)
        {
            if (_atlas.Done < _submitted)
            {
                if (frame > _submittedFrame + JobTimeoutFrames)
                {
                    _warnedNoAtlas = true;
                    GD.PushWarning("[tree-cards] the capture's atlas did not arrive (was the capture's colour RGBA16F?); no cards are drawn");
                    AbortPass();
                }

                return;
            }

            foreach (Kind kind in _batch)
            {
                if (!kind.Pictured)
                {
                    kind.Pictured = true;
                    _flagsChanged = true;
                }
            }

            _batch.Clear();
            _submitted = 0;
        }

        if (pass.Next < pass.Kinds.Count)
        {
            Submit(camera, frame);
            return;
        }

        _pass = null;
        _passEndFrame = frame;
        int kinds = Order.Count;
        int rows = AndroidTreeCardMaths.Rows(kinds);
        GD.Print($"[tree-cards] capture ({pass.Why}): {pass.Placed} of {pass.Kinds.Count} tree models in {pass.Frames} frame(s), " +
            $"atlas {AndroidTreeCardMaths.Columns * AndroidTreeCardMaths.CellPixels}x{rows * AndroidTreeCardMaths.CellPixels} " +
            $"({AndroidTreeCardMaths.AtlasBytes(kinds) / (1024.0 * 1024.0):0.0} MB with mips), {pass.MainMs:0.00} ms on the main thread");
        if (!_firstDone && pass.Full)
        {
            _firstDone = true;
            GD.Print("[tree-cards] " + RegionLine());
            if (!_dumped && Chosen.Value.Dump)
            {
                _dumped = true;
                _atlas.Dump(Save);
            }
        }
    }

    /// <summary>Stands the pass's next batch in the capture, copies the light in and asks for one render.</summary>
    private void Submit(Camera3D camera, ulong frame)
    {
        long from = Stopwatch.GetTimestamp();
        Pass pass = _pass!;
        int perFrame = Chosen.Value.PerFrame;
        (int columns, int rows) = perFrame > 0
            ? AndroidTreeCardMaths.BatchGrid(perFrame, perFrame)
            : AndroidTreeCardMaths.BatchGrid(0, Order.Count);
        int room = columns * rows;
        var size = new Vector2I(columns * AndroidTreeCardMaths.CellPixels, rows * AndroidTreeCardMaths.CellPixels);
        if (_viewport.Size != size)
        {
            _viewport.Size = size;
        }

        (Vector3 heading, _areaHeading) = AndroidTreeCardMaths.CaptureHeading(Chosen.Value.Turn, _areaHeading, Heading(camera));
        Basis facing = Basis.LookingAt(heading, Vector3.Up);
        _camera.Transform = new Transform3D(facing, Stage);
        _camera.Size = rows * CellMetres;

        foreach (GeometryInstance3D shown in _shown)
        {
            if (IsInstanceValid(shown))
            {
                shown.Visible = false;
            }
        }

        _shown.Clear();
        _batch.Clear();
        var cells = new List<int>(room);
        while (pass.Next < pass.Kinds.Count && cells.Count < room)
        {
            Kind kind = pass.Kinds[pass.Next++];
            if (Place(kind, cells.Count, columns, rows, facing))
            {
                cells.Add(kind.Cell);
                _batch.Add(kind);
                _shown.Add(kind.Capture!);
            }
            else
            {
                kind.Failed = true;
            }
        }

        pass.Frames++;
        pass.Placed += cells.Count;
        if (cells.Count > 0)
        {
            CopyLight(camera);
            _submitted = ++_jobId;
            _submittedFrame = frame;
            _atlas.Submit(_submitted, AndroidTreeCardMaths.Rows(Order.Count), columns, cells.ToArray());
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }

        pass.MainMs += (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Drops the pass under way; a job already on the render thread finishes into cells no one reads yet.</summary>
    private void AbortPass()
    {
        _pass = null;
        _submitted = 0;
        _batch.Clear();
    }

    /// <summary>Re-reads the kinds whose sources changed and one more in turn; true when any kind's placements changed.</summary>
    private bool ReadOne()
    {
        if (Order.Count == 0)
        {
            return false;
        }

        bool changed = false;
        int pick = _cursor++ % Order.Count;
        for (int i = 0; i < Order.Count; i++)
        {
            Kind kind = Order[i];
            if (kind.Dirty || i == pick)
            {
                kind.Dirty = false;
                changed |= Read(kind);
            }
        }

        return changed;
    }

    /// <summary>The kind's standing placements; true when they differ from the last read.</summary>
    private bool Read(Kind kind)
    {
        List<Card> records = _scratch;
        records.Clear();
        for (int s = kind.Sources.Count - 1; s >= 0; s--)
        {
            Source from = kind.Sources[s];
            GeometryInstance3D source = from.Node;
            if (!IsInstanceValid(source) || !source.IsInsideTree())
            {
                kind.Sources.RemoveAt(s);
                kind.SourceIds.Remove(from.Id);
                continue;
            }

            if (!source.IsVisibleInTree())
            {
                continue;
            }

            Transform3D node = source.GlobalTransform;
            if (source is MultiMeshInstance3D { Multimesh: { } multi })
            {
                if (Refii.Level.Engine_PropShadow.StatedTransforms(multi) is not { } stated)
                {
                    continue;
                }

                foreach (Transform3D placement in stated)
                {
                    Add(node * placement, from);
                }
            }
            else
            {
                Add(node, from);
            }
        }

        void Add(Transform3D t, Source from)
        {
            if (AndroidTreeCardMaths.Record(t, kind.Sphere, out Vector3 centre, out float radius))
            {
                records.Add(new Card(new Vector4(centre.X, centre.Y, centre.Z, radius), from.Start, from.StatedCull));
            }
        }

        var signature = new HashCode();
        foreach (Card record in records)
        {
            signature.Add(record);
        }

        int hash = signature.ToHashCode();
        if (records.Count == kind.Records.Count && hash == kind.Signature)
        {
            return false;
        }

        kind.Signature = hash;
        _scratch = kind.Records;
        kind.Records = records;
        return true;
    }

    /// <summary>Writes every kind's cards into the one MultiMesh.</summary>
    private void Upload()
    {
        float reach = Refii.Level.Engine_TreeReach.TreeFarReach;
        _uploadedReach = reach;
        int n = 0;
        foreach (Kind kind in Order)
        {
            n += kind.Records.Count;
        }

        _count = n;
        if (n == 0)
        {
            if (_multi.InstanceCount != 0)
            {
                _multi.InstanceCount = 0;
            }

            return;
        }

        var buffer = new float[n * Stride];
        Aabb bounds = default;
        float widest = 0f;
        int at = 0;
        foreach (Kind kind in Order)
        {
            foreach (Card record in kind.Records)
            {
                var centre = new Vector3(record.Sphere.X, record.Sphere.Y, record.Sphere.Z);
                float r = record.Sphere.W;
                Refii.Level.Engine_PropShadow.WriteSlot(buffer, at, new Transform3D(Basis.FromScale(new Vector3(r, r, r)), centre));
                buffer[at + 12] = kind.Cell;
                buffer[at + 13] = record.Start;
                buffer[at + 14] = AndroidTreeCardMaths.CardEnd(record.Start, record.StatedCull, reach);
                buffer[at + 15] = kind.Pictured ? 1f : 0f;
                bounds = at == 0 ? new Aabb(centre, Vector3.Zero) : bounds.Expand(centre);
                widest = MathF.Max(widest, r);
                at += Stride;
            }
        }

        if (_multi.InstanceCount != n)
        {
            _multi.InstanceCount = n;
        }

        RenderingServer.MultimeshSetBuffer(_multi.GetRid(), (ReadOnlySpan<float>)buffer);
        _multi.CustomAabb = bounds.Grow(widest);
    }

    /// <summary>The view's horizontal heading.</summary>
    private static Vector3 Heading(Camera3D camera)
    {
        Vector3 forward = -camera.GlobalBasis.Z;
        var flat = new Vector3(forward.X, 0f, forward.Z);
        return flat.LengthSquared() > 1e-6f ? flat.Normalized() : new Vector3(0f, 0f, -1f);
    }

    /// <summary>The level's sun: a child named Sun of one of a tree node's ancestors, looked for now and then.</summary>
    private DirectionalLight3D? Sun()
    {
        if (_sun is not null && IsInstanceValid(_sun) && _sun.IsInsideTree())
        {
            return _sun;
        }

        _sun = null;
        ulong frame = Engine.GetProcessFrames();
        if (frame < _sunSearchFrame)
        {
            return null;
        }

        _sunSearchFrame = frame + 60;
        foreach (Kind kind in Order)
        {
            foreach (Source from in kind.Sources)
            {
                GeometryInstance3D source = from.Node;
                if (!IsInstanceValid(source) || !source.IsInsideTree())
                {
                    continue;
                }

                for (Node? at = source.GetParent(); at is not null; at = at.GetParent())
                {
                    if (at.GetNodeOrNull<DirectionalLight3D>(Refii.Lighting.Engine_LevelLighting.SunNodeName) is { } sun)
                    {
                        return _sun = sun;
                    }
                }

                return null;
            }
        }

        return null;
    }

    /// <summary>The sun's travel direction and colour times energy; zero for none or a hidden one.</summary>
    private (Vector3 Direction, Vector3 Colour) SunNow()
    {
        if (Sun() is not { } sun || !sun.IsVisibleInTree())
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        Color c = sun.LightColor;
        return (-sun.GlobalBasis.Z, new Vector3(c.R, c.G, c.B) * sun.LightEnergy);
    }

    private static Godot.Environment? MainEnvironment(Camera3D camera) => camera.Environment ?? camera.GetWorld3D()?.Environment;

    private static Vector3 AmbientNow(Camera3D camera)
    {
        if (MainEnvironment(camera) is not { } env || env.AmbientLightSource == Godot.Environment.AmbientSource.Disabled)
        {
            return Vector3.Zero;
        }

        Color c = env.AmbientLightColor;
        return new Vector3(c.R, c.G, c.B) * env.AmbientLightEnergy;
    }

    /// <summary>The area's line: cards, models and the distances the drawn cards start and end at.</summary>
    private string RegionLine()
    {
        if (Order.Count == 0)
        {
            return "region: no tree models";
        }

        var placements = new List<(float Start, float StatedCull)>(_count);
        foreach (Kind kind in Order)
        {
            foreach (Card record in kind.Records)
            {
                placements.Add((record.Start, record.StatedCull));
            }
        }

        return AndroidTreeCardMaths.RegionSpread(placements, Refii.Level.Engine_TreeReach.TreeFarReach)
                .Describe(Order.Count, Chosen.Value.StartScale) + ", " +
            $"{(Chosen.Value.PerFrame > 0 ? $"{Chosen.Value.PerFrame} models a frame" : "all models in one frame")}, " +
            $"turn {(Chosen.Value.Turn > 0f ? $"{Chosen.Value.Turn:0} deg" : "off")}";
    }

    /// <summary>Writes the atlas's mip 0 to user://tree_cards_atlas.png (debug only).</summary>
    private static void Save(Image? image)
    {
        const string path = "user://tree_cards_atlas.png";
        if (image is null)
        {
            GD.Print("[tree-cards] the atlas could not be read back");
            return;
        }

        if (image.IsCompressed() || image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        // The atlas is linear; the PNG is for viewing.
        image.LinearToSrgb();
        Error error = image.SavePng(path);
        GD.Print($"[tree-cards] atlas {image.GetWidth()}x{image.GetHeight()} written to {ProjectSettings.GlobalizePath(path)} ({error})");
    }

    /// <summary>
    /// Stands the kind's model in slot <paramref name="slot"/> of the capture's grid, facing the capture
    /// camera, and shows it; false when it has no source to copy.
    /// </summary>
    private bool Place(Kind kind, int slot, int columns, int rows, Basis facing)
    {
        if (kind.Capture is null || !IsInstanceValid(kind.Capture))
        {
            kind.Capture = CaptureModel(kind);
            if (kind.Capture is null)
            {
                return false;
            }
        }

        Vector2 offset = AndroidTreeCardMaths.CellCentre(slot, columns, rows) * CellMetres;
        Vector3 centre = Stage - (facing.Z * CaptureDepth) + (facing.X * offset.X) + (facing.Y * offset.Y);
        // The model's file +Y (Godot model -Z) along the heading, scaled so its bound sphere fills the cell.
        float scale = CellMetres * 0.5f / MathF.Max(kind.Sphere.W, 1e-3f);
        Basis turn = facing.Scaled(new Vector3(scale, scale, scale));
        var sphereCentre = new Vector3(kind.Sphere.X, kind.Sphere.Z, -kind.Sphere.Y);
        kind.Capture.Transform = new Transform3D(turn, centre - (turn * sphereCentre));
        kind.Capture.Visible = true;
        return true;
    }

    /// <summary>
    /// The kind's model in the capture world: a copy of its first standing source's node, with its mesh
    /// (a private vertex bake included), its materials and its instance lighting. A batch's copy is a
    /// one-instance batch carrying the first placement's instance colour, which holds a batch's bake.
    /// Where the real trees wear their far build at the hand-over (`prop_far_shading_metres`), so does
    /// the copy.
    /// </summary>
    private GeometryInstance3D? CaptureModel(Kind kind)
    {
        bool far = Refii.Level.Engine_PropFarShading.Active &&
            AndroidTreeCardMaths.CaptureFar(Refii.Level.Engine_PropFarShading.Metres, kind.Start);
        foreach (Source from in kind.Sources)
        {
            GeometryInstance3D source = from.Node;
            if (!IsInstanceValid(source) || !source.IsInsideTree())
            {
                continue;
            }

            bool farHere = far && Refii.Level.Engine_PropFarShading.IsRegistered(source);
            GeometryInstance3D model;
            if (source is MultiMeshInstance3D { Multimesh: { } batch })
            {
                Mesh mesh = batch.Mesh ?? kind.Mesh;
                var multi = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = batch.UseColors,
                    Mesh = mesh,
                };
                multi.InstanceCount = 1;
                multi.SetInstanceTransform(0, Transform3D.Identity);
                if (batch.UseColors && batch.InstanceCount > 0)
                {
                    multi.SetInstanceColor(0, Engine_ServerSyncs.InstanceColor(batch, FirstStanding(batch)));
                }

                model = new MultiMeshInstance3D { Multimesh = multi };
                model.MaterialOverride = farHere ? FarOf(source.MaterialOverride, mesh.SurfaceGetMaterial(0)) : source.MaterialOverride;
            }
            else if (source is MeshInstance3D single)
            {
                Mesh mesh = single.Mesh ?? kind.Mesh;
                var copy = new MeshInstance3D { Mesh = mesh };
                int surfaces = Math.Min(mesh.GetSurfaceCount(), single.GetSurfaceOverrideMaterialCount());
                for (int s = 0; s < surfaces; s++)
                {
                    Material? own = single.GetSurfaceOverrideMaterial(s);
                    if (farHere)
                    {
                        own = FarOf(own, mesh.SurfaceGetMaterial(s));
                    }

                    if (own is not null)
                    {
                        copy.SetSurfaceOverrideMaterial(s, own);
                    }
                }

                model = copy;
                model.MaterialOverride = source.MaterialOverride;
            }
            else
            {
                continue;
            }

            model.Name = "Capture";
            model.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            model.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
            model.Layers = source.Layers;
            model.Visible = false;
            _viewport.AddChild(model);
            CopyInstanceLighting(source, model);
            return model;
        }

        return null;
    }

    /// <summary>
    /// A surface's far build: the far copy of <paramref name="shared"/> when the node's own material is
    /// none or already a far copy; a material someone else set is kept (as Engine_PropFarShading keeps it).
    /// </summary>
    private static Material? FarOf(Material? own, Material? shared) =>
        own is null || Refii.Models.Engine_ShaderSpecialisation.IsFarCopy(own)
            ? Refii.Models.Engine_ShaderSpecialisation.FarCopy(shared) ?? own
            : own;

    /// <summary>The batch's first placement not collapsed to a point.</summary>
    private static int FirstStanding(MultiMesh batch)
    {
        if (Refii.Level.Engine_PropShadow.StatedTransforms(batch) is { } stated)
        {
            for (int i = 0; i < stated.Length; i++)
            {
                if (stated[i].Basis.Column0.LengthSquared() > 1e-8f)
                {
                    return i;
                }
            }
        }

        return 0;
    }

    /// <summary>The source's bake mode, probe, ambient scale and baked AO, from its metadata (no server reads).</summary>
    private static void CopyInstanceLighting(GeometryInstance3D source, GeometryInstance3D model)
    {
        Refii.Lighting.Engine_LevelBakedLighting.ReadInstanceLighting(source, true, out bool hasProbe,
            out Vector4 probeR, out Vector4 probeG, out Vector4 probeB, out Vector4 lightmapColour, out float ambientScale);
        model.SetInstanceShaderParameter(_lightmapColourName ??= "lightmap_colour", lightmapColour);
        model.SetInstanceShaderParameter(_ambientScaleName ??= "fable_ambient_scale", ambientScale);
        if (hasProbe)
        {
            model.SetInstanceShaderParameter(_probeRName ??= "probe_r", probeR);
            model.SetInstanceShaderParameter(_probeGName ??= "probe_g", probeG);
            model.SetInstanceShaderParameter(_probeBName ??= "probe_b", probeB);
        }

        StringName aoMeta = Refii.Lighting.Engine_LevelBakedLighting.InstanceBakedAoMetaName;
        if (source.HasMeta(aoMeta) && source.GetMeta(aoMeta) is { VariantType: Variant.Type.Float } ao)
        {
            model.SetInstanceShaderParameter(_bakedAoName ??= "instance_baked_ao", (float)ao.AsDouble());
        }
    }

    /// <summary>The capture world's sun and ambient, copied from the level's: the sun without its shadow.</summary>
    private void CopyLight(Camera3D camera)
    {
        DirectionalLight3D? sun = Sun();
        bool lit = sun is not null && sun.IsVisibleInTree();
        _light.Visible = lit;
        if (lit)
        {
            _light.Transform = new Transform3D(sun!.GlobalBasis.Orthonormalized(), Vector3.Zero);
            _light.LightColor = sun.LightColor;
            _light.LightEnergy = sun.LightEnergy;
            _light.LightIndirectEnergy = sun.LightIndirectEnergy;
            _light.LightSpecular = sun.LightSpecular;
            _light.LightCullMask = sun.LightCullMask;
            _light.LightIntensityLux = sun.LightIntensityLux;
            _light.LightTemperature = sun.LightTemperature;
            _light.LightAngularDistance = sun.LightAngularDistance;
            _light.LightNegative = sun.LightNegative;
        }

        Godot.Environment capture = _camera.Environment;
        if (MainEnvironment(camera) is not { } env || env.AmbientLightSource == Godot.Environment.AmbientSource.Disabled)
        {
            capture.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
            return;
        }

        // The capture world has no sky: a sky or background ambient is taken as its stated colour.
        capture.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        capture.AmbientLightColor = env.AmbientLightColor;
        capture.AmbientLightEnergy = env.AmbientLightEnergy;
        capture.AmbientLightSkyContribution = 0f;
    }

    /// <summary>Drops the area's capture models and hides the cards until the next area's first pass.</summary>
    private void ReleaseRegion()
    {
        foreach (Kind kind in Order)
        {
            if (kind.Capture is not null && IsInstanceValid(kind.Capture))
            {
                kind.Capture.QueueFree();
            }

            kind.Capture = null;
        }

        if (_multi.InstanceCount != 0)
        {
            _multi.InstanceCount = 0;
        }

        AbortPass();
        _shown.Clear();
        _cards.Visible = false;
        _count = 0;
        _cursor = 0;
        _firstDone = false;
        _areaHeading = null;
        _lightStale = false;
        _dumped = false;
        _sun = null;
        _sunSearchFrame = 0;
    }
}
