using System;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Entities;

/// <summary>
/// The targeting highlight where the post chain cannot draw it: the bleed in two 2D passes and the
/// composite as a screen quad in the main view.
/// </summary>
/// <remarks>
/// WHY. The outline (red on an enemy, the category colour on anyone else) reaches the screen only
/// through the post chain's highlight passes (Engine_PostFxEffect, `fable_postfx_highlight*.glsl`).
/// On the Mobile renderer without MSAA, which every phone preset uses, the chain never runs: the
/// colour layer is A2B10G10R10 or RGBA16F without the storage bit, and there is nothing to stage
/// through (Engine_PostFxRoute). The highlight layer was therefore gated off and the guest's stated
/// highlight was never drawn on a phone: `highlightLayerPresent` false with live words in
/// `highlightWordsSeen` on the debug channel.
///
/// HOW. The silhouette layer is unchanged and still drawn by Engine_EntityHighlightLayer. Two
/// half-size 2D viewports bleed it, the first also looking each slot's colour up (the prep), with
/// the same kernel, gain and 8-bit steps as the compute passes. A quad over the view, drawn in the
/// main camera's transparent pass, composites it (mobile_highlight_composite.gdshader): there the
/// scene depth is Godot's own copy, so each halo is occluded as the guest's is, and the glow is
/// tone-mapped with the scene. The quad is hidden whenever nothing is lit, so its depth read never
/// splits Mobile's render pass otherwise (Engine_FxDepthReads). With android_highlight_depth_test
/// the quad gives way to one quad per lit object at the depth its halo is tested at
/// (mobile_highlight_composite_tested.gdshader), tested by the depth buffer itself: no depth
/// texture, so not even a lit target splits the pass (AndroidTargetingHighlight.DepthTestKey).
///
/// WHAT DIFFERS. The silhouette viewport has no scene depth either (the FX depth capture is a
/// post-opaque effect, and Mobile runs none), so a silhouette is not clipped where the scene hides
/// it: the halo of a partly hidden target also bleeds from its hidden part, wherever the composite's
/// depth test lets it through.
/// </remarks>
public sealed partial class Engine_MobileHighlight : Node
{
    private const string BleedShaderPath = "res://src/android/mobile_highlight_bleed.gdshader";
    private const string CompositeShaderPath = "res://src/android/mobile_highlight_composite.gdshader";
    private const string TestedShaderPath = "res://src/android/mobile_highlight_composite_tested.gdshader";

    /// <summary>Half-res texels the guest pads each object's rect by (`sub_82A69BB0`).</summary>
    private const float PadTexels = 16f;

    private static readonly ConditionalWeakTable<Engine_EntityHighlightLayer, Engine_MobileHighlight> ByLayer = new();

    /// <summary>
    /// Whether this draws the highlight: the post chain does not composite it this frame, and the
    /// phone draws it at all (<see cref="Refii.AndroidRuntime.AndroidTargetingHighlight"/>).
    /// The caller gates on Engine_Platform.IsAndroid.
    /// </summary>
    public static bool Composites => Refii.AndroidRuntime.AndroidTargetingHighlight.Composites(
        Refii.AndroidRuntime.AndroidTargetingHighlight.Enabled,
        Refii.Lighting.Engine_PostFxParams.Enabled && Refii.Lighting.Engine_PostFxRoute.LayersReachScreen);

    private SubViewport _bleedH = null!;
    private SubViewport _bleedV = null!;
    private ShaderMaterial _prep = null!;
    private ShaderMaterial _vertical = null!;
    private ShaderMaterial _composite = null!;
    private MeshInstance3D _quad = null!;
    private Vector2I _size;

    /// <summary>
    /// android_highlight_depth_test's quads, one per lit object, built on first use
    /// (<see cref="Refii.AndroidRuntime.AndroidTargetingHighlight.DepthTest"/>).
    /// </summary>
    private MeshInstance3D[]? _tested;
    private ShaderMaterial[]? _testedMaterials;

    private readonly Vector4[] _colours = new Vector4[Engine_EntityHighlightLayer.Slots];
    private readonly Vector4[] _rects = new Vector4[Engine_EntityHighlightLayer.Slots];
    private readonly float[] _tests = new float[Engine_EntityHighlightLayer.Slots];

    /// <summary>
    /// Main thread, after <paramref name="layer"/> has gathered its targets. Draws this frame's
    /// highlight when <paramref name="active"/>, and turns every pass off when not.
    /// </summary>
    public static void Follow(Engine_EntityHighlightLayer layer, SubViewport silhouettes,
        Camera3D camera, Engine_EntityHighlightLayer.Target[] targets, bool active, float scale, bool secondView)
    {
        if (!Refii.Core.Engine_Platform.IsAndroid)
        {
            return;
        }

        if (!ByLayer.TryGetValue(layer, out Engine_MobileHighlight? highlight) || !GodotObject.IsInstanceValid(highlight))
        {
            if (!active)
            {
                return;
            }

            highlight = new Engine_MobileHighlight { Name = "MobileHighlight" };
            highlight.Build(silhouettes);
            layer.AddChild(highlight);
            ByLayer.AddOrUpdate(layer, highlight);
        }

        highlight.Draw(silhouettes, camera, targets, active && Composites, scale, secondView);
    }

    private void Build(SubViewport silhouettes)
    {
        var bleed = GD.Load<Shader>(BleedShaderPath);
        _prep = new ShaderMaterial { Shader = bleed };
        _prep.SetShaderParameter("src_tex", silhouettes.GetTexture());
        _prep.SetShaderParameter("dir", new Vector2(1f, 0f));
        _prep.SetShaderParameter("prep", true);
        _bleedH = Pass("HighlightBleedH", _prep);

        _vertical = new ShaderMaterial { Shader = bleed };
        _vertical.SetShaderParameter("src_tex", _bleedH.GetTexture());
        _vertical.SetShaderParameter("dir", new Vector2(0f, 1f));
        _bleedV = Pass("HighlightBleedV", _vertical);

        // Last of the transparent list: the guest's composite runs once the scene is drawn.
        _composite = new ShaderMaterial
        {
            Shader = GD.Load<Shader>(CompositeShaderPath),
            RenderPriority = (int)Material.RenderPriorityMax,
        };
        _composite.SetShaderParameter("layer_tex", silhouettes.GetTexture());
        _composite.SetShaderParameter("glow_tex", _bleedV.GetTexture());
        _quad = new MeshInstance3D
        {
            Name = "HighlightComposite",
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            MaterialOverride = _composite,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            ExtraCullMargin = 16384f,
            Visible = false,
        };

        // Added in this order, after the silhouette viewport: Godot draws sibling viewports in the
        // order they became active, so each pass reads this frame's input.
        AddChild(_bleedH);
        AddChild(_bleedV);
        AddChild(_quad);
    }

    /// <summary>A half-size 2D pass: one rect covering it, drawing <paramref name="material"/>.
    /// 8-bit, as the guest's target is; the shader writes every texel, so no blend.</summary>
    private static SubViewport Pass(string name, ShaderMaterial material)
    {
        var viewport = new SubViewport
        {
            Name = name,
            Disable3D = true,
            TransparentBg = true,
            UseHdr2D = false,
            RenderTargetClearMode = SubViewport.ClearMode.Never,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Msaa2D = Viewport.Msaa.Disabled,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
        };
        viewport.AddChild(new ColorRect { Name = "Pass", Material = material, MouseFilter = Control.MouseFilterEnum.Ignore });
        return viewport;
    }

    private void Draw(SubViewport silhouettes, Camera3D camera,
        Engine_EntityHighlightLayer.Target[] targets, bool active, float scale, bool secondView)
    {
        int count = active ? Fill(silhouettes.Size, camera, targets) : 0;
        SubViewport.UpdateMode mode = count > 0 ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        _bleedH.RenderTargetUpdateMode = mode;
        _bleedV.RenderTargetUpdateMode = mode;
        bool tested = count > 0 && Refii.AndroidRuntime.AndroidTargetingHighlight.DepthTest;
        _quad.Visible = count > 0 && !tested;
        int shownTested = tested ? count : 0;
        if (_tested is not null || tested)
        {
            ShowTested(shownTested);
        }

        if (count == 0)
        {
            return;
        }

        if (silhouettes.Size != _size)
        {
            _size = silhouettes.Size;
            _bleedH.Size = _size;
            _bleedV.Size = _size;
            ((ColorRect)_bleedH.GetChild(0)).Size = _size;
            ((ColorRect)_bleedV.GetChild(0)).Size = _size;
        }

        // Only this view's camera draws the quad: the view's own layer (Engine_ClassicViews), which
        // the split cameras are kept on and the window's camera is given here. Nothing else is on
        // view 0's layer without a split, and no mirror following the guest sees either.
        uint layer = secondView ? Refii.Models.Engine_ClassicViews.SecondViewOnlyLayer : Refii.Models.Engine_ClassicViews.FirstViewOnlyLayer;
        if (_quad.Layers != layer)
        {
            _quad.Layers = layer;
        }

        if ((camera.CullMask & layer) == 0)
        {
            camera.CullMask |= layer;
        }

        _prep.SetShaderParameter("colours", _colours);
        _prep.SetShaderParameter("scale", scale);
        _vertical.SetShaderParameter("scale", scale);
        if (tested && _tested is not null && _testedMaterials is not null)
        {
            for (int k = 0; k < count; ++k)
            {
                if (_tested[k].Layers != layer)
                {
                    _tested[k].Layers = layer;
                }

                _testedMaterials[k].SetShaderParameter("rect", _rects[k]);
                _testedMaterials[k].SetShaderParameter("test",
                    Refii.AndroidRuntime.AndroidTargetingHighlight.QuadDistance(_tests[k], camera.Near, camera.Far));
            }

            return;
        }

        _composite.SetShaderParameter("count", count);
        _composite.SetShaderParameter("rects", _rects);
        _composite.SetShaderParameter("tests", _tests);
    }

    /// <summary>
    /// Shows the first <paramref name="count"/> depth-tested quads and hides the rest, building them
    /// on first use: the same textures as the composite, last of the transparent list as it is.
    /// </summary>
    private void ShowTested(int count)
    {
        if (_tested is null || _testedMaterials is null)
        {
            var shader = GD.Load<Shader>(TestedShaderPath);
            var mesh = new QuadMesh { Size = new Vector2(2f, 2f) };
            _tested = new MeshInstance3D[Engine_EntityHighlightLayer.Slots];
            _testedMaterials = new ShaderMaterial[Engine_EntityHighlightLayer.Slots];
            for (int k = 0; k < _tested.Length; ++k)
            {
                var material = new ShaderMaterial { Shader = shader, RenderPriority = (int)Material.RenderPriorityMax };
                material.SetShaderParameter("layer_tex", _composite.GetShaderParameter("layer_tex"));
                material.SetShaderParameter("glow_tex", _composite.GetShaderParameter("glow_tex"));
                _testedMaterials[k] = material;
                _tested[k] = new MeshInstance3D
                {
                    Name = "HighlightTested" + k,
                    Mesh = mesh,
                    MaterialOverride = material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                    ExtraCullMargin = 16384f,
                    Layers = _quad.Layers,
                    Visible = false,
                };
                AddChild(_tested[k]);
            }
        }

        for (int k = 0; k < _tested.Length; ++k)
        {
            bool show = k < count;
            if (_tested[k].Visible != show)
            {
                _tested[k].Visible = show;
            }
        }
    }

    /// <summary>
    /// Each lit slot's colour, its padded screen rect and the depth its halo is tested at, packed
    /// for the composite: Engine_PostFxEffect.HighlightTargets for this frame's camera. The
    /// colours stay indexed by slot (the prep looks them up); the rects are packed. Returns how
    /// many rects are on screen.
    /// </summary>
    private int Fill(Vector2I layerSize, Camera3D camera, Engine_EntityHighlightLayer.Target[] targets)
    {
        Array.Clear(_colours);
        Projection projection = camera.GetCameraProjection();
        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        if (px <= 1e-8f || py <= 1e-8f)
        {
            return 0;
        }

        Transform3D eye = camera.GlobalTransform;
        Vector3 origin = eye.Origin;
        Vector3 forward = -eye.Basis.Z;
        Vector3 right = eye.Basis.X;
        Vector3 up = eye.Basis.Y;
        float hw = Math.Max(layerSize.X, 1);
        float hh = Math.Max(layerSize.Y, 1);
        int count = 0;

        for (int k = 0; k < targets.Length; ++k)
        {
            Engine_EntityHighlightLayer.Target t = targets[k];
            if (!t.Present)
            {
                continue;
            }

            _colours[k] = t.Colour;
            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f, nearest = float.MaxValue;
            bool inside = false;
            for (int c = 0; c < 8; ++c)
            {
                Vector3 offset = new Vector3(
                    (c & 1) == 0 ? t.Min.X : t.Max.X,
                    (c & 2) == 0 ? t.Min.Y : t.Max.Y,
                    (c & 4) == 0 ? t.Min.Z : t.Max.Z) - origin;
                float facing = offset.Dot(forward);
                if (facing <= 0.05f)
                {
                    inside = true;
                    break;
                }

                float x = ((offset.Dot(right) * px / facing) + 1f) * 0.5f;
                float y = (1f - (offset.Dot(up) * py / facing)) * 0.5f;
                x0 = MathF.Min(x0, x);
                x1 = MathF.Max(x1, x);
                y0 = MathF.Min(y0, y);
                y1 = MathF.Max(y1, y);
                nearest = MathF.Min(nearest, facing);
            }

            if (inside)
            {
                (x0, y0, x1, y1, nearest) = (0f, 0f, 1f, 1f, 0f);
            }
            else
            {
                x0 = MathF.Max(x0, 0f);
                y0 = MathF.Max(y0, 0f);
                x1 = MathF.Min(x1, 1f);
                y1 = MathF.Min(y1, 1f);
                if (x0 >= x1 || y0 >= y1)
                {
                    continue;
                }

                x0 = MathF.Floor(x0 * hw) / hw - PadTexels / hw;
                y0 = MathF.Floor(y0 * hh) / hh - PadTexels / hh;
                x1 = MathF.Ceiling(x1 * hw) / hw + PadTexels / hw;
                y1 = MathF.Ceiling(y1 * hh) / hh + PadTexels / hh;
            }

            // A small target (a character) is tested at its centre, a large one at its nearest
            // corner: fable_postfx_highlightcomposite.glsl's DEPTH note.
            Vector3 centre = (t.Min + t.Max) * 0.5f;
            bool small = (t.Max - t.Min).Length() * 0.5f <= 3f;
            float centreDepth = inside ? 0f : MathF.Max((centre - origin).Dot(forward), 0f);
            _rects[count] = new Vector4(x0, y0, x1, y1);
            _tests[count] = small ? centreDepth : nearest;
            ++count;
        }

        for (int k = count; k < _rects.Length; ++k)
        {
            _rects[k] = default;
            _tests[k] = 0f;
        }

        return count;
    }
}
