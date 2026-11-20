using System;
using System.Collections.Generic;
using Godot;
using Refii.Fx;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_displacement_rect`: the heat-haze layer copies and redraws only the part of the
/// screen its sources can reach, not the whole screen. The same picture. Android only; off, the
/// layer is untouched.
/// </summary>
/// <remarks>
/// WHY. Awake, the layer's apply rect (Engine_FxDisplacement, CanvasLayer -100) covers the window
/// and reads the screen texture, so Godot copies the whole target to the back buffer and runs the
/// apply fragment on every pixel: about 2 ms on an Adreno 640 at the Oakfield pub, where the
/// hearth's haze is on screen three headings in four.
/// <para>ON. At the frame end (<see cref="AndroidDisplacementFrameEnd"/>, after every _Process
/// and the take-back test) each awake view projects the world boxes of this frame's and last
/// frame's wakers (<see cref="AndroidDisplacementGate.CollectBoxes"/>) and of the shown static
/// haze meshes (<see cref="NoteStatic"/>) that touch its frustum, with both its live camera and
/// the layer's own copy, to one screen rectangle. The apply rect is that, grown by two
/// displacement texels for the half-size map's filtering. A Rect-mode BackBufferCopy, a child
/// of the apply rect drawn behind it (so it hides with it), copies the apply rect grown by the
/// largest offset the shader can apply (disp_pixel_scale: the map's channels are 0-1 and the
/// edge fade at most 1), so every pixel the shader can sample is copied this frame; the shader
/// clamps its uv to the copied rect (`copy_rect`). A copy rect that would be the whole screen is
/// left to the shader's own full copy. The renderer takes a BackBufferCopy's rect in
/// render-target pixels, untransformed (renderer_canvas_render_rd.cpp:769).</para>
/// <para>FULL SCREEN, AS BEFORE, when a wake has no box (the drunk warp, a mesh particle with
/// no placement, past the gate's capacity), a box in view crosses the near plane, or a camera
/// cannot be read. No box in view at all hides the apply rect for the frame.</para>
/// Hooks: Engine_FxDisplacement.Resize (<see cref="Owns"/>: leaves the placed rect alone),
/// Engine_DisplacementMeshes.Pump (<see cref="NoteStatic"/>), fx_displacement_apply.gdshader
/// (`copy_rect`, default the whole screen). Reported in the gate's `metrics` `displacement.rect`
/// and `[disp-gate] rect` log line. Main thread. Live.
/// </remarks>
public static class AndroidDisplacementRect
{
    public const string Key = "android_displacement_rect";

    /// <summary>The apply shader's uniform: the copied rect in screen uv, (min x, min y, max x, max y).</summary>
    public const string CopyRectParam = "copy_rect";

    /// <summary>The whole screen: the shader's default, and the value with the setting off.</summary>
    public static readonly Vector4 WholeScreen = new(0f, 0f, 1f, 1f);

    /// <summary>Displacement-map texels added round the projected rect: its linear filter and rasterisation.</summary>
    public const float BleedTexels = 2f;

    private static bool? _enabled;
    private static bool _hooked;

    /// <summary>Whether the key is on, read once and again on each live change; always false off a phone.</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } known) return known;
            bool on = false;
            try
            {
                on = Refii.Core.Engine_Platform.IsAndroid &&
                    Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, false);
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

    /// <summary>Test seam: forces the setting; null reads it again.</summary>
    internal static void Force(bool? on) => _enabled = on;

    // ---- Pure ----

    /// <summary>What a view's projected boxes came to.</summary>
    public enum Fit
    {
        /// <summary>No box in view: nothing to apply.</summary>
        Empty,
        /// <summary>A screen rect.</summary>
        Rect,
        /// <summary>Full screen: a box crosses the near plane (or a camera could not be read).</summary>
        Full,
    }

    /// <summary>
    /// Grows [<paramref name="min"/>, <paramref name="max"/>] (screen uv, y down, unclamped) by the
    /// eight corners of <paramref name="box"/> seen through <paramref name="view"/> (world to
    /// camera) and <paramref name="projection"/>. False when a corner is on or behind the near
    /// plane, where the projection folds over. Pure.
    /// </summary>
    public static bool AddBox(Transform3D view, Projection projection, Aabb box, ref Vector2 min, ref Vector2 max)
    {
        Vector3 p = box.Position, s = box.Size;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                p.X + ((i & 1) != 0 ? s.X : 0f),
                p.Y + ((i & 2) != 0 ? s.Y : 0f),
                p.Z + ((i & 4) != 0 ? s.Z : 0f));
            Vector3 v = view * corner;
            Vector4 clip = projection * new Vector4(v.X, v.Y, v.Z, 1f);
            if (!(clip.W > 1e-6f) || clip.Z < -clip.W) return false;
            var uv = new Vector2(0.5f + 0.5f * clip.X / clip.W, 0.5f - 0.5f * clip.Y / clip.W);
            min = min.Min(uv);
            max = max.Max(uv);
        }
        return true;
    }

    /// <summary>
    /// The screen rect (uv, unclamped) of the <paramref name="boxes"/> touching
    /// <paramref name="frustum"/>, through one camera. Empty when none does; Full when one in view
    /// crosses the near plane. Pure.
    /// </summary>
    public static Fit ScreenRect(Transform3D view, Projection projection, Plane[] frustum, ReadOnlySpan<Aabb> boxes, ref Vector2 min, ref Vector2 max)
    {
        Fit fit = Fit.Empty;
        foreach (Aabb box in boxes)
        {
            if (!Engine_DisplacementMeshes.BoxTouchesFrustum(box, Transform3D.Identity, frustum)) continue;
            if (!AddBox(view, projection, box, ref min, ref max)) return Fit.Full;
            fit = Fit.Rect;
        }
        return fit;
    }

    /// <summary>
    /// The uv rect [<paramref name="min"/>, <paramref name="max"/>] in pixels of a
    /// <paramref name="size"/> target, grown by <paramref name="grow"/> pixels each side, rounded
    /// outwards and clamped to the target. Zero-sized when it lies off the target. Pure.
    /// </summary>
    public static Rect2I ToPixels(Vector2 min, Vector2 max, Vector2I size, float grow)
    {
        int x0 = Clamp(MathF.Floor(min.X * size.X - grow), size.X);
        int y0 = Clamp(MathF.Floor(min.Y * size.Y - grow), size.Y);
        int x1 = Clamp(MathF.Ceiling(max.X * size.X + grow), size.X);
        int y1 = Clamp(MathF.Ceiling(max.Y * size.Y + grow), size.Y);
        if (x1 <= x0 || y1 <= y0) return new Rect2I(x0, y0, 0, 0);
        return new Rect2I(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary><paramref name="rect"/> grown by <paramref name="grow"/> pixels each side, clamped to the target. Pure.</summary>
    public static Rect2I Grow(Rect2I rect, Vector2I size, float grow)
    {
        int g = (int)MathF.Ceiling(MathF.Max(0f, grow));
        int x0 = Math.Max(0, rect.Position.X - g), y0 = Math.Max(0, rect.Position.Y - g);
        int x1 = Math.Min(size.X, rect.End.X + g), y1 = Math.Min(size.Y, rect.End.Y + g);
        return new Rect2I(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static int Clamp(float v, int max) =>
        float.IsNaN(v) ? 0 : v <= 0f ? 0 : v >= max ? max : (int)v;

    /// <summary>
    /// The largest offset, in target pixels, the apply shader can sample from a pixel: its
    /// `disp_pixel_scale` on the larger axis (the map's channel differences are at most 1 and the
    /// edge fade at most 1), plus one pixel for the linear fetch. Pure.
    /// </summary>
    public static float MaxOffset(Vector2 dispPixelScale) =>
        MathF.Max(MathF.Abs(dispPixelScale.X), MathF.Abs(dispPixelScale.Y)) + 1f;

    /// <summary>The shader's `copy_rect` for a copied pixel rect of a <paramref name="size"/> target. Pure.</summary>
    public static Vector4 UvRect(Rect2I copy, Vector2I size) => new(
        (float)copy.Position.X / size.X, (float)copy.Position.Y / size.Y,
        (float)copy.End.X / size.X, (float)copy.End.Y / size.Y);

    /// <summary>A rect's share of a <paramref name="size"/> target, 0-1. Pure.</summary>
    public static float Share(Rect2I rect, Vector2I size) =>
        size.X <= 0 || size.Y <= 0 ? 0f : (float)((double)rect.Size.X * rect.Size.Y / ((double)size.X * size.Y));

    // ---- Static meshes ----

    private static List<Aabb> _staticNow = new(), _staticBefore = new();
    private static ulong _staticNowFrame = ulong.MaxValue, _staticBeforeFrame = ulong.MaxValue;

    /// <summary>A shown static haze mesh (Engine_DisplacementMeshes.Pump): its cull box under its placement.</summary>
    public static void NoteStatic(Aabb local, Transform3D placed)
    {
        if (!Enabled) return;
        NoteStaticAt(placed * local, Engine.GetProcessFrames());
        AndroidDisplacementGate.EnsureFrameEnd();
    }

    internal static void NoteStaticAt(Aabb world, ulong frame)
    {
        if (frame != _staticNowFrame)
        {
            (_staticBefore, _staticNow) = (_staticNow, _staticBefore);
            _staticBeforeFrame = _staticNowFrame;
            _staticNow.Clear();
            _staticNowFrame = frame;
        }
        _staticNow.Add(world);
    }

    /// <summary>
    /// Every box that keeps the layer awake at <paramref name="frame"/> into <paramref name="into"/>:
    /// the gate's wakers and the static meshes, this frame and the last. False when a wake had no box.
    /// </summary>
    internal static bool Collect(ulong frame, List<Aabb> into)
    {
        into.Clear();
        bool boxed = AndroidDisplacementGate.CollectBoxes(frame, into);
        if (_staticNowFrame == frame || _staticNowFrame + 1 == frame) into.AddRange(_staticNow);
        if (_staticBeforeFrame + 1 == frame) into.AddRange(_staticBefore);
        return boxed;
    }

    /// <summary>Test seam: forgets the static boxes and the views' state.</summary>
    internal static void Reset()
    {
        _staticNow.Clear();
        _staticBefore.Clear();
        _staticNowFrame = _staticBeforeFrame = ulong.MaxValue;
        _views[0] = new View();
        _views[1] = new View();
    }

    // ---- The layer ----

    private const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    private static readonly System.Reflection.FieldInfo? RectField = typeof(Engine_FxDisplacement).GetField("_rect", Hidden);
    private static readonly System.Reflection.FieldInfo? HostField = typeof(Engine_FxDisplacement).GetField("_host", Hidden);
    private static readonly System.Reflection.FieldInfo? CameraField = typeof(Engine_FxDisplacement).GetField("_camera", Hidden);
    private static readonly System.Reflection.FieldInfo? MapField = typeof(Engine_FxDisplacement).GetField("_viewport", Hidden);
    // Made on first use: a StringName needs the engine, which the pure tests do not start.
    private static StringName? _scaleParam, _copyParam;

    private sealed class View
    {
        public ColorRect? Rect;
        public BackBufferCopy? Copy;
        /// <summary>The rect, copy or uniform differs from upstream's.</summary>
        public bool Applied;
        public Vector4 Uniform = WholeScreen;
        // For the reply and the log.
        public string Mode = "off";
        public string? Why;
        public float ApplyShare = 1f, CopyShare = 1f;
        public double ApplySum, CopySum;
        public int Frames;
    }

    private static readonly View[] _views = { new(), new() };
    private static readonly List<Aabb> Boxes = new();
    private static readonly Plane[] Frustum = new Plane[6];

    /// <summary>
    /// Engine_FxDisplacement.Resize's hook: true while this rect is placed here, so Resize leaves
    /// it alone. With the setting turned off, puts the rect, copy and uniform back as upstream
    /// has them and answers false.
    /// </summary>
    public static bool Owns(ColorRect rect)
    {
        for (int i = 0; i < _views.Length; i++)
        {
            View view = _views[i];
            if (!ReferenceEquals(view.Rect, rect)) continue;
            if (Enabled) return view.Applied;
            if (view.Applied) Restore(view, null);
            return false;
        }
        return false;
    }

    /// <summary>The frame end's placement of each view's apply rect and copy (AndroidDisplacementGate.EndFrame).</summary>
    internal static void Place(ulong frame)
    {
        bool on = Enabled;
        for (int i = 0; i < _views.Length; i++)
        {
            Engine_FxDisplacement? layer = i == 0 ? Engine_FxDisplacement.Current : Engine_FxDisplacement.SecondView;
            View view = _views[i];
            if (layer is null || !GodotObject.IsInstanceValid(layer) || RectField?.GetValue(layer) is not ColorRect rect ||
                !GodotObject.IsInstanceValid(rect))
            {
                if (view.Rect is not null) _views[i] = new View();
                continue;
            }
            if (!ReferenceEquals(view.Rect, rect))
            {
                // A new layer (a level change, a split): its rect starts as upstream's.
                view = _views[i] = new View { Rect = rect };
            }
            Viewport? host = HostField?.GetValue(layer) as Viewport;
            if (!on)
            {
                if (view.Applied) Restore(view, host);
                view.Mode = "off";
                continue;
            }
            if (!rect.Visible)
            {
                view.Mode = "asleep";
                continue;
            }
            PlaceView(frame, layer, view, rect, host);
            view.ApplySum += view.ApplyShare;
            view.CopySum += view.CopyShare;
            view.Frames++;
        }
    }

    private static void PlaceView(ulong frame, Engine_FxDisplacement layer, View view, ColorRect rect, Viewport? host)
    {
        if (host is null || !GodotObject.IsInstanceValid(host) || rect.Material is not ShaderMaterial apply)
        {
            Full(view, host, "host");
            return;
        }
        Vector2I size = host.GetTexture()?.GetSize() is { } texSize ? new Vector2I((int)texSize.X, (int)texSize.Y) : Vector2I.Zero;
        if (size.X <= 0 || size.Y <= 0)
        {
            Full(view, host, "size");
            return;
        }
        if (!Collect(frame, Boxes))
        {
            Full(view, host, "nobox");
            return;
        }

        // Every camera the haze can be drawn with this frame: the view's own, now, and the copy
        // the layer drew its map with.
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        Fit fit = Fit.Empty;
        Camera3D? live = host.GetCamera3D();
        Camera3D? copied = CameraField?.GetValue(layer) as Camera3D;
        for (int c = 0; c < 2; c++)
        {
            Camera3D? camera = c == 0 ? live : copied;
            if (camera is null || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree() ||
                (c == 1 && ReferenceEquals(camera, live)))
            {
                if (c == 0)
                {
                    Full(view, host, "camera");
                    return;
                }
                continue;
            }
            if (!AndroidDisplacementGate.ReadFrustum(camera, Frustum))
            {
                Full(view, host, "camera");
                return;
            }
            Fit one = ScreenRect(camera.GetCameraTransform().AffineInverse(), camera.GetCameraProjection(), Frustum,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Boxes), ref min, ref max);
            if (one == Fit.Full)
            {
                Full(view, host, "near");
                return;
            }
            if (one == Fit.Rect) fit = Fit.Rect;
        }

        Rect2I applyPx = default;
        if (fit == Fit.Rect)
        {
            Vector2I map = MapField?.GetValue(layer) is SubViewport sub && GodotObject.IsInstanceValid(sub) ? sub.Size : size / 2;
            float texel = MathF.Max((float)size.X / Math.Max(1, map.X), (float)size.Y / Math.Max(1, map.Y));
            applyPx = ToPixels(min, max, size, BleedTexels * texel + 1f);
        }
        if (applyPx.Size.X <= 0 || applyPx.Size.Y <= 0)
        {
            // Nothing that wakes the layer is in view: nothing to redraw this frame.
            rect.Visible = false;
            view.Mode = "none";
            view.Why = null;
            view.ApplyShare = view.CopyShare = 0f;
            return;
        }

        Vector2 scale = apply.GetShaderParameter(_scaleParam ??= "disp_pixel_scale").AsVector2();
        Rect2I copyPx = Grow(applyPx, size, MaxOffset(scale));
        bool copyAll = copyPx.Size == size;

        // The apply rect in the layer's canvas coordinates (the render target through the
        // viewport's final transform).
        Transform2D toCanvas = host.GetFinalTransform().AffineInverse();
        Vector2 a = toCanvas * (Vector2)applyPx.Position, b = toCanvas * (Vector2)applyPx.End;
        Vector2 pos = a.Min(b), end = a.Max(b);
        if (rect.Position != pos) rect.Position = pos;
        if (rect.Size != end - pos) rect.Size = end - pos;

        BackBufferCopy copy = EnsureCopy(view, rect);
        if (copyAll)
        {
            if (copy.CopyMode != BackBufferCopy.CopyModeEnum.Disabled) copy.CopyMode = BackBufferCopy.CopyModeEnum.Disabled;
            SetUniform(view, apply, WholeScreen);
        }
        else
        {
            var r = new Rect2(copyPx.Position, copyPx.Size);
            if (copy.Rect != r) copy.Rect = r;
            if (copy.CopyMode != BackBufferCopy.CopyModeEnum.Rect) copy.CopyMode = BackBufferCopy.CopyModeEnum.Rect;
            SetUniform(view, apply, UvRect(copyPx, size));
        }
        view.Applied = true;
        view.Mode = copyAll ? "rect, full copy" : "rect";
        view.Why = null;
        view.ApplyShare = Share(applyPx, size);
        view.CopyShare = copyAll ? 1f : Share(copyPx, size);
    }

    private static BackBufferCopy EnsureCopy(View view, ColorRect rect)
    {
        if (view.Copy is { } held && GodotObject.IsInstanceValid(held)) return held;
        // A child drawn before its parent: it hides with the rect, and copies before it draws.
        var copy = new BackBufferCopy
        {
            Name = "DispCopy",
            CopyMode = BackBufferCopy.CopyModeEnum.Disabled,
            ShowBehindParent = true,
        };
        rect.AddChild(copy);
        view.Copy = copy;
        return copy;
    }

    private static void SetUniform(View view, ShaderMaterial apply, Vector4 value)
    {
        if (view.Uniform == value) return;
        view.Uniform = value;
        apply.SetShaderParameter(_copyParam ??= CopyRectParam, value);
    }

    /// <summary>The whole screen for this frame, as upstream draws it.</summary>
    private static void Full(View view, Viewport? host, string why)
    {
        Restore(view, host);
        view.Mode = "full";
        view.Why = why;
        view.ApplyShare = view.CopyShare = 1f;
    }

    /// <summary>Upstream's rect, no copy of our own, the shader's default uniform.</summary>
    private static void Restore(View view, Viewport? host)
    {
        if (view.Copy is { } copy && GodotObject.IsInstanceValid(copy) && copy.CopyMode != BackBufferCopy.CopyModeEnum.Disabled)
        {
            copy.CopyMode = BackBufferCopy.CopyModeEnum.Disabled;
        }
        if (view.Rect is { } rect && GodotObject.IsInstanceValid(rect))
        {
            if (rect.Material is ShaderMaterial apply) SetUniform(view, apply, WholeScreen);
            if (rect.Position != Vector2.Zero) rect.Position = Vector2.Zero;
            // As Resize sizes it; Resize does it itself when it called us (host null).
            if (host is not null && GodotObject.IsInstanceValid(host))
            {
                Vector2 v = host.GetVisibleRect().Size;
                if (rect.Size != v) rect.Size = v;
            }
        }
        view.Applied = false;
    }

    // ---- The reply and the log ----

    /// <summary>Each view's placement at the last frame end, for the gate's `metrics` reply.</summary>
    public static Dictionary<string, object?> Describe()
    {
        var views = new List<Dictionary<string, object?>>();
        for (int i = 0; i < _views.Length; i++)
        {
            View view = _views[i];
            if (view.Rect is null) continue;
            views.Add(new Dictionary<string, object?>
            {
                ["view"] = i,
                ["mode"] = view.Mode,
                ["why"] = view.Why,
                ["applyShare"] = MathF.Round(view.ApplyShare, 3),
                ["copyShare"] = MathF.Round(view.CopyShare, 3),
            });
        }
        return new Dictionary<string, object?> { ["on"] = Enabled, ["views"] = views };
    }

    /// <summary>`[disp-gate] rect`: each view's mode now and its mean apply and copy shares since the last line.</summary>
    internal static string LogLine()
    {
        var sb = new System.Text.StringBuilder("[disp-gate] rect=").Append(Enabled ? "on" : "off");
        for (int i = 0; i < _views.Length; i++)
        {
            View view = _views[i];
            if (view.Rect is null) continue;
            sb.Append(" v").Append(i).Append("=").Append(view.Mode.Replace(' ', '_'));
            if (view.Why is not null) sb.Append('(').Append(view.Why).Append(')');
            sb.Append(" apply=").Append(view.ApplyShare.ToString("0.000"))
              .Append(" copy=").Append(view.CopyShare.ToString("0.000"));
            if (view.Frames > 0)
            {
                sb.Append(" mean1s apply=").Append((view.ApplySum / view.Frames).ToString("0.000"))
                  .Append(" copy=").Append((view.CopySum / view.Frames).ToString("0.000"))
                  .Append(" awake=").Append(view.Frames);
            }
            view.ApplySum = view.CopySum = 0;
            view.Frames = 0;
        }
        return sb.ToString();
    }
}
