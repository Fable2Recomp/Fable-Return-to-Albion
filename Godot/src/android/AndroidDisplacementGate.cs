using System;
using System.Collections.Generic;
using Godot;
using Refii.Fx;

namespace Refii.AndroidRuntime;

/// <summary>
/// The displacement (heat-haze) layer's wakers, named for the debug channel, and
/// `android_displacement_in_view`: effects and bodies wake the layer only while their haze can
/// be on screen. Android only; off, every waker wakes it as before.
/// </summary>
/// <remarks>
/// WHY. Any frame the layer is awake costs a full back-buffer copy and a full-screen pass
/// (Engine_FxDisplacement's apply rect reads the screen texture), about 2 ms on an Adreno 640.
/// Upstream wakes it for every live effect with a haze visual within effect reach, behind the
/// camera too, and for every shown body with haze parts. Static haze meshes already wake it in a
/// way each view can take back when none is in its frustum (Engine_FxDisplacement.SleepUnseen).
/// <para>ON. Effect and body wakes go the static meshes' way: each states a world box, and a
/// view whose camera frustum touches no box (and no static mesh) is put back to sleep that frame.
/// The boxes are the renderer's own cull boxes or larger, so the picture is the same. The drunk
/// warp is full-screen and always wakes it.</para>
/// Hooks: FableFx (effects), Engine_FxSystem (mesh particles), Engine_DisplacementMeshes.Pump
/// (bodies, static count, the take-back test), Engine_ModernDrunk and GameSession (drunk, pump
/// reached), GameDebugServer `metrics` (<see cref="Describe"/>). The frame end runs on its own
/// node (<see cref="AndroidDisplacementFrameEnd"/>), which logs `[disp-gate]` once a second with
/// <see cref="LogKey"/> on. Main thread. Live.
/// </remarks>
public static class AndroidDisplacementGate
{
    public const string Key = "android_displacement_in_view";

    /// <summary>The effect layer instances' cull margin (FableFx's Engine_FxLayerInstance.Create).</summary>
    public const float EffectMargin = 1f;

    /// <summary>Over a body's stated box: its pose may outgrow it between restates.</summary>
    public const float BodyPad = 1f;

    /// <summary>Boxes kept per frame; a wake past this wakes the layer outright.</summary>
    public const int Capacity = 128;

    /// <summary>Wakers named in the debug reply.</summary>
    public const int Named = 4;

    public enum Kind { Effect, FxMesh, Body, Drunk, Static }

    private static readonly string[] KindNames = { "effect", "fxmesh", "body", "drunk", "static" };

    private struct Waker
    {
        public Kind Kind;
        public object? Owner;
        public string? Label;
        public Aabb Box;
        public bool HasBox;
        public bool Gated;
    }

    private static Waker[] _now = new Waker[Capacity];
    private static Waker[] _before = new Waker[Capacity];
    private static int _nowCount, _beforeCount;
    private static ulong _nowFrame = ulong.MaxValue, _beforeFrame = ulong.MaxValue;
    private static readonly int[] _calls = new int[KindNames.Length];
    private static int _gated, _hard;
    private static ulong _testedFrame = ulong.MaxValue;
    private static Waker _lastOutright, _lastGated;
    private static ulong _lastOutrightFrame = ulong.MaxValue, _lastGatedFrame = ulong.MaxValue;
    private static ulong _askedFrame = ulong.MaxValue;

    /// <summary>A wake with no box (or past <see cref="Capacity"/>) this frame and the last: the haze can be anywhere on screen.</summary>
    private static bool _nowBoxless, _beforeBoxless;

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

    /// <summary>`android_displacement_gate_log`: the `[disp-gate]` log lines (debug, off by default).</summary>
    public const string LogKey = "android_displacement_gate_log";

    private static bool? _logging;
    private static bool _logHooked;

    /// <summary>Whether <see cref="LogKey"/> is on, read once and again on each live change; always false off a phone.</summary>
    public static bool Logging
    {
        get
        {
            if (_logging is { } known) return known;
            bool on = false;
            try
            {
                on = Refii.Core.Engine_Platform.IsAndroid &&
                    Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), LogKey, false);
            }
            catch (Exception)
            {
                on = false;
            }

            if (!_logHooked)
            {
                _logHooked = true;
                Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
                {
                    if (string.Equals(name, LogKey, StringComparison.OrdinalIgnoreCase)) _logging = null;
                };
            }

            _logging = on;
            return on;
        }
    }

    /// <summary>Whether any gate, rect or logging feature is active; always false on desktop.</summary>
    private static bool Active => Enabled || AndroidDisplacementRect.Enabled || Logging;

    /// <summary>Test seam: forces the setting; null reads it again.</summary>
    internal static void Force(bool? on)
    {
        _enabled = on;
        _logging = null;
    }

    // ---- Hooks ----

    /// <summary>An effect drew haze this frame (FableFx): its disp mesh's stated box under its transform.</summary>
    public static void WakeEffect(Node3D effect, Transform3D placed, Mesh? disp)
    {
        if (!Active)
        {
            Engine_FxDisplacement.NotifyActive();
            return;
        }
        bool stated = disp is ArrayMesh array && array.CustomAabb.Size != Vector3.Zero;
        Aabb local = stated ? ((ArrayMesh)disp!).CustomAabb : disp?.GetAabb() ?? default;
        Aabb box = EffectBox(placed, local);
        if (Logging && Logged.Add(effect.GetInstanceId())) LogFirstWake(effect, placed, local, stated, box);
        Wake(Kind.Effect, effect, null, box, disp is not null);
    }

    private static readonly HashSet<ulong> Logged = new();

    /// <summary>
    /// The first wake of each effect: where it is, its disp mesh's box (node-local) and the world
    /// box the gate tests. `node` is the node's GlobalTransform read now, beside the cached one the
    /// effect gave (`at`): a difference means a stale cache.
    /// </summary>
    private static void LogFirstWake(Node3D effect, Transform3D placed, Aabb local, bool stated, Aabb box)
    {
        string name = effect is FableFx fx ? fx.EffectName : (string)effect.Name;
        Vector3 node = effect.IsInsideTree() ? effect.GlobalTransform.Origin : new Vector3(float.NaN, float.NaN, float.NaN);
        GD.Print($"[disp-gate] effect {name} at {V(placed.Origin)}, mesh aabb {A(local)} ({(stated ? "stated" : "mesh")}), " +
            $"transform basis scale {V(placed.Basis.Scale)}, node {V(node)}, world box {A(box)}, " +
            $"path {(effect.IsInsideTree() ? effect.GetPath().ToString() : "-")}");
    }

    private static string V(Vector3 v) => $"({v.X:0.0},{v.Y:0.0},{v.Z:0.0})";

    private static string A(Aabb a) => $"pos {V(a.Position)} size {V(a.Size)}";

    /// <summary>An effect's mesh particle with a haze visual (Engine_FxSystem), at its node-local position; named by its mesh.</summary>
    public static void WakeMeshParticle(object system, string meshPath, Transform3D? placed, Vector3 local, float scale)
    {
        if (!Active)
        {
            Engine_FxDisplacement.NotifyActive();
            return;
        }
        Aabb box = placed is { } world ? MeshParticleBox(world, local, scale) : default;
        Wake(Kind.FxMesh, system, meshPath, box, placed is not null);
    }

    /// <summary>
    /// A shown body with haze parts (Engine_DisplacementMeshes.Pump). True when the gate took the
    /// wake, so the pump does not count it; false (off) leaves it to the pump as before.
    /// </summary>
    public static bool TakesBody(MeshInstance3D body, Mesh parts, Transform3D placed)
    {
        if (!Active) return false;
        Aabb stated = body.CustomAabb;
        Aabb local = parts.GetAabb();
        if (stated.Size != Vector3.Zero) local = local.Merge(stated);
        else if (body.Mesh is { } mesh) local = local.Merge(mesh.GetAabb());
        return Wake(Kind.Body, body, null, BodyBox(placed, local), true, wakeOutright: false);
    }

    /// <summary>The drunk warp drew (Engine_ModernDrunk, GameSession): full-screen, always wakes the layer.</summary>
    public static void WakeDrunk()
    {
        if (!Active)
        {
            Engine_FxDisplacement.NotifyActive();
            return;
        }
        Wake(Kind.Drunk, null, null, default, false);
    }

    /// <summary>Static haze meshes shown this frame (Engine_DisplacementMeshes.Pump), counted for the reply.</summary>
    public static void NoteStatic(int shown)
    {
        if (!Active || shown <= 0) return;
        Rotate(Engine.GetProcessFrames());
        _calls[(int)Kind.Static] += shown;
    }

    private static readonly Func<Camera3D, bool> Probe = ProbeView;
    private static readonly Func<Camera3D, bool> GateOnly = SeenBy;
    private static bool _seenAny;

    /// <summary>
    /// Before the pump's own take-back test (Engine_DisplacementMeshes.Pump): when a view would be
    /// tested and a gated box is in its frustum, states the layer active this frame, so the static
    /// meshes' test leaves every view awake. Main thread.
    /// </summary>
    public static void HoldIfSeen()
    {
        if (!Active) return;
        _askedFrame = Engine.GetProcessFrames();
        if (!Enabled) return;
        _seenAny = false;
        // The probe never puts a view to sleep; it only looks.
        Engine_FxDisplacement.SleepUnseen(Probe);
        if (_seenAny) Engine_FxDisplacement.NotifyActive();
    }

    /// <summary>GameSession reached Engine_DisplacementMeshes.Pump this frame (for the log).</summary>
    public static void AfterPump()
    {
        if (!Active) return;
        _pumpFrame = Engine.GetProcessFrames();
    }

    private static ulong _pumpFrame = ulong.MaxValue;

    /// <summary>
    /// The frame's last word (<see cref="AndroidDisplacementFrameEnd"/>, after GameSession and the
    /// layer's own _Process): with the setting on and no take-back test run by the pump, takes back
    /// each view no gated box is in; then keeps the frame for the reply and the log. Main thread.
    /// </summary>
    internal static void EndFrame()
    {
        ulong frame = Engine.GetProcessFrames();
        if (Enabled && _askedFrame != frame)
        {
            Engine_FxDisplacement.SleepUnseen(GateOnly);
        }
        AndroidDisplacementRect.Place(frame);
        Fill(_last, frame);
        _haveLast = true;
        if (Logging && Time.GetTicksMsec() >= _nextLog)
        {
            _nextLog = Time.GetTicksMsec() + 1000;
            foreach (string line in LogLines(frame)) GD.Print(line);
        }
    }

    private static ulong _nextLog;
    private static AndroidDisplacementFrameEnd? _frameEnd;

    /// <summary>Puts the frame-end node in the tree once something has woken the layer.</summary>
    internal static void EnsureFrameEnd()
    {
        if (_frameEnd is not null && GodotObject.IsInstanceValid(_frameEnd)) return;
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        _frameEnd = new AndroidDisplacementFrameEnd { Name = "DisplacementGateFrameEnd" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, _frameEnd);
    }

    // The layer's own state, read for the log line only (private upstream fields).
    private const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
    private static readonly System.Reflection.FieldInfo? ActiveFrameField = typeof(Engine_FxDisplacement).GetField("_activeFrame", Hidden);
    private static readonly System.Reflection.FieldInfo? StaticFrameField = typeof(Engine_FxDisplacement).GetField("_staticFrame", Hidden);
    private static readonly System.Reflection.FieldInfo? EnabledField = typeof(Engine_FxDisplacement).GetField("_enabled", Hidden);
    private static readonly System.Reflection.FieldInfo? HostField = typeof(Engine_FxDisplacement).GetField("_host", Hidden);
    private static readonly System.Reflection.FieldInfo? RectField = typeof(Engine_FxDisplacement).GetField("_rect", Hidden);
    private static readonly System.Reflection.FieldInfo? CameraField = typeof(Engine_FxDisplacement).GetField("_camera", Hidden);

    private static string Ago(ulong frame, object? value) =>
        value is ulong at ? (at > frame ? "never" : (frame - at).ToString()) : "?";

    /// <summary>
    /// `[disp-gate]`: this frame's layer state, why it is awake, and what woke it. activeAgo and
    /// staticAgo are the frames since the layer's own outright and static stamps (0 or 1 keeps it
    /// awake); lastOutright and lastGated are the gate's own record of the same, so a stamp newer
    /// than the gate's record means a caller the gate does not see.
    /// </summary>
    private static List<string> LogLines(ulong frame)
    {
        // Short lines, each tagged: a long one came out of logcat cut short.
        var lines = new List<string>();
        var sb = new System.Text.StringBuilder("[disp-gate] frame=").Append(frame);
        Engine_FxDisplacement? layer = Engine_FxDisplacement.Current;
        bool alive = layer is not null && GodotObject.IsInstanceValid(layer);
        sb.Append(" drawing=").Append(_last.View0 ? 1 : 0).Append(',').Append(_last.View1 ? 1 : 0);
        sb.Append(" inViewOnly=").Append(Enabled ? "on" : "off");
        sb.Append(" pumped=").Append(_pumpFrame == frame ? "yes" : "no");
        sb.Append(" pumpTest=").Append(_askedFrame == frame ? "yes" : "no");
        if (alive)
        {
            sb.Append(" activeAgo=").Append(Ago(frame, ActiveFrameField?.GetValue(null)));
            sb.Append(" staticAgo=").Append(Ago(frame, StaticFrameField?.GetValue(null)));
            sb.Append(" enabled=").Append(EnabledField?.GetValue(layer) ?? "?");
            sb.Append(" rect=").Append(RectField?.GetValue(layer) is CanvasItem rect && GodotObject.IsInstanceValid(rect) ? (rect.Visible ? "shown" : "hidden") : "?");
            sb.Append(" host=").Append(HostField?.GetValue(layer) is GodotObject host ? (GodotObject.IsInstanceValid(host) ? "alive" : "freed") : "none");
        }
        else
        {
            sb.Append(" layer=none");
        }
        sb.Append(" calls=");
        for (int i = 0; i < KindNames.Length; i++) sb.Append(i == 0 ? "" : ",").Append(KindNames[i]).Append(':').Append(_last.Calls[i]);
        sb.Append(" outright=").Append(_last.Hard).Append(" gated=").Append(_last.Gated);
        sb.Append(" gatedInView=").Append(_last.GatedInView?.ToString() ?? "untested");
        lines.Add(sb.ToString());
        lines.Add(AndroidDisplacementRect.LogLine());
        lines.Add($"[disp-gate] last outright={WakerText(_lastOutright)}@{Ago(frame, _lastOutrightFrame)} gated={WakerText(_lastGated)}@{Ago(frame, _lastGatedFrame)}");

        Camera3D? dispCamera = alive ? CameraField?.GetValue(layer) as Camera3D : null;
        if (dispCamera is not null && !GodotObject.IsInstanceValid(dispCamera)) dispCamera = null;
        // The camera the gate tests: the host's own, live (LiveCamera).
        Camera3D? live = dispCamera is not null ? LiveCamera(dispCamera) : null;
        if (live is not null && !GodotObject.IsInstanceValid(live)) live = null;
        // The camera the world view draws with, as the debug channel reads it.
        Camera3D? world = Refii.Game.GameEntities.Current?.GetViewport()?.GetCamera3D();
        lines.Add("[disp-gate] cam disp=" + CameraText(dispCamera) + " live=" + CameraText(live) +
            " world=" + CameraText(world is not null && GodotObject.IsInstanceValid(world) ? world : null) +
            " liveIsDisp=" + (live is not null && ReferenceEquals(live, dispCamera) ? "yes" : "no"));
        bool frustum = live is not null && ReadFrustum(live, Frustum);
        Vector3? eye = live?.GlobalPosition;
        for (int i = 0; i < _last.Named.Count; i++)
        {
            Waker w = _last.Named[i];
            var row = new System.Text.StringBuilder("[disp-gate] waker ").Append(WakerText(w));
            if (w.HasBox)
            {
                row.Append(" c=").Append(V(w.Box.GetCenter())).Append(" s=").Append(V(w.Box.Size));
                if (eye is { } at) row.Append(" d=").Append(at.DistanceTo(w.Box.GetCenter()).ToString("0.0")).Append('m');
                if (frustum) row.Append(Engine_DisplacementMeshes.BoxTouchesFrustum(w.Box, Transform3D.Identity, Frustum) ? " in" : " out");
            }
            lines.Add(row.ToString());
        }
        return lines;
    }

    private static string CameraText(Camera3D? camera) =>
        camera is null ? "-" : V(camera.GlobalPosition) + "->" + V(-camera.GlobalTransform.Basis.Z);

    private static string WakerText(Waker w)
    {
        string name = w.Owner is Node node && GodotObject.IsInstanceValid(node)
            ? (node is FableFx fx ? fx.EffectName + "|" + (node.IsInsideTree() ? node.GetPath().ToString() : (string)node.Name) : (node.IsInsideTree() ? node.GetPath().ToString() : (string)node.Name))
            : w.Label ?? "-";
        return KindNames[(int)w.Kind] + ":" + name;
    }

    // ---- The decision ----

    /// <summary>An effect's haze box in the world: the disp mesh's box grown by the instance's cull margin. Pure.</summary>
    public static Aabb EffectBox(Transform3D placed, Aabb local) => (placed * local).Grow(EffectMargin);

    /// <summary>A mesh particle's box: generous, as the mesh's own size is not known here. Pure.</summary>
    public static Aabb MeshParticleBox(Transform3D placed, Vector3 local, float scale) =>
        new Aabb(placed * local, Vector3.Zero).Grow(FableFx.FreezeRadius * MathF.Max(1f, MathF.Abs(scale)));

    /// <summary>A body's haze box: its parts and stated box under its transform, padded. Pure.</summary>
    public static Aabb BodyBox(Transform3D placed, Aabb local) => (placed * local).Grow(BodyPad);

    /// <summary>Whether any of <paramref name="boxes"/> touches the frustum, by the static meshes' rule. Pure.</summary>
    public static bool AnyTouches(ReadOnlySpan<Aabb> boxes, Plane[] planes)
    {
        foreach (Aabb box in boxes)
        {
            if (Engine_DisplacementMeshes.BoxTouchesFrustum(box, Transform3D.Identity, planes)) return true;
        }
        return false;
    }

    /// <summary>
    /// Notes one wake at <paramref name="frame"/>. True when it was kept as a box a view may take
    /// back (the setting on, a box, room left); false when it must wake the layer outright.
    /// Boxes of the same owner in a row are merged.
    /// </summary>
    internal static bool Note(Kind kind, object? owner, string? label, Aabb box, bool hasBox, bool enabled, ulong frame)
    {
        Rotate(frame);
        _calls[(int)kind]++;
        if (!hasBox) _nowBoxless = true;
        bool gate = enabled && hasBox && kind != Kind.Drunk;
        if (_nowCount > 0 && owner is not null && ReferenceEquals(_now[_nowCount - 1].Owner, owner) && _now[_nowCount - 1].Kind == kind)
        {
            ref Waker last = ref _now[_nowCount - 1];
            if (last.HasBox && hasBox) last.Box = last.Box.Merge(box);
            else if (gate) gate = false;
        }
        else if (_nowCount < Capacity)
        {
            _now[_nowCount++] = new Waker { Kind = kind, Owner = owner, Label = label, Box = box, HasBox = hasBox, Gated = gate };
        }
        else
        {
            gate = false;
            _nowBoxless = true;
        }
        if (gate)
        {
            _gated++;
            _lastGated = new Waker { Kind = kind, Owner = owner, Label = label };
            _lastGatedFrame = frame;
        }
        else
        {
            _hard++;
            _lastOutright = new Waker { Kind = kind, Owner = owner, Label = label };
            _lastOutrightFrame = frame;
        }
        return gate;
    }

    /// <summary>Whether a box noted at <paramref name="frame"/> or the frame before touches the frustum.</summary>
    internal static bool SeenIn(Plane[] planes, ulong frame)
    {
        if (_nowFrame == frame || _nowFrame + 1 == frame)
        {
            if (AnyWakerTouches(_now, _nowCount, planes)) return true;
        }
        if (_beforeFrame + 1 == frame && AnyWakerTouches(_before, _beforeCount, planes)) return true;
        return false;
    }

    /// <summary>
    /// Adds the boxes noted at <paramref name="frame"/> and the frame before (the frames that keep
    /// the layer awake now) to <paramref name="into"/>. False when one of those wakes had no box,
    /// so its haze can be anywhere (<see cref="AndroidDisplacementRect"/> then draws full-screen).
    /// </summary>
    internal static bool CollectBoxes(ulong frame, List<Aabb> into)
    {
        bool boxed = true;
        if (_nowFrame == frame || _nowFrame + 1 == frame)
        {
            if (_nowBoxless) boxed = false;
            for (int i = 0; i < _nowCount; i++) if (_now[i].HasBox) into.Add(_now[i].Box);
        }
        if (_beforeFrame + 1 == frame)
        {
            if (_beforeBoxless) boxed = false;
            for (int i = 0; i < _beforeCount; i++) if (_before[i].HasBox) into.Add(_before[i].Box);
        }
        return boxed;
    }

    private static bool AnyWakerTouches(Waker[] wakers, int count, Plane[] planes)
    {
        for (int i = 0; i < count; i++)
        {
            if (wakers[i].HasBox && Engine_DisplacementMeshes.BoxTouchesFrustum(wakers[i].Box, Transform3D.Identity, planes)) return true;
        }
        return false;
    }

    /// <summary>Test seam: forgets every noted wake.</summary>
    internal static void Reset()
    {
        _nowCount = _beforeCount = 0;
        _nowFrame = _beforeFrame = ulong.MaxValue;
        Array.Clear(_calls);
        _gated = _hard = 0;
        _seenAny = false;
        _haveLast = false;
        _nowBoxless = _beforeBoxless = false;
    }

    private static bool Wake(Kind kind, object? owner, string? label, Aabb box, bool hasBox, bool wakeOutright = true)
    {
        EnsureFrameEnd();
        bool gated = Note(kind, owner, label, box, hasBox, Enabled, Engine.GetProcessFrames());
        if (gated) Engine_FxDisplacement.NotifyStaticShown();
        else if (wakeOutright) Engine_FxDisplacement.NotifyActive();
        return gated;
    }

    private static void Rotate(ulong frame)
    {
        if (frame == _nowFrame) return;
        (_before, _now) = (_now, _before);
        _beforeCount = _nowCount;
        _beforeFrame = _nowFrame;
        _beforeBoxless = _nowBoxless;
        _nowBoxless = false;
        _nowCount = 0;
        _nowFrame = frame;
        Array.Clear(_now);
        Array.Clear(_calls);
        _gated = _hard = 0;
        _seenAny = false;
    }

    private static readonly Plane[] Frustum = new Plane[6];

    private static bool SeenBy(Camera3D camera)
    {
        ulong frame = Engine.GetProcessFrames();
        Rotate(frame);
        bool seen = SeenFrom(LiveCamera(camera), frame);
        _testedFrame = frame;
        if (seen) _seenAny = true;
        return seen;
    }

    private static bool ProbeView(Camera3D camera)
    {
        ulong frame = Engine.GetProcessFrames();
        Rotate(frame);
        _testedFrame = frame;
        if (SeenFrom(LiveCamera(camera), frame)) _seenAny = true;
        return true;
    }

    /// <summary>
    /// Whether a box noted this frame or the last is in <paramref name="camera"/>'s frustum; true
    /// when the frustum cannot be read.
    /// </summary>
    internal static bool SeenFrom(Camera3D camera, ulong frame) => !ReadFrustum(camera, Frustum) || SeenIn(Frustum, frame);

    /// <summary>
    /// The camera the view is drawn from now, for the layer view whose own camera is
    /// <paramref name="tested"/>: its host viewport's camera. The layer copies that camera into its
    /// own only on frames it is awake, so its own may be stale; this one never is. The layer's own
    /// when the host cannot be found.
    /// </summary>
    private static Camera3D LiveCamera(Camera3D tested)
    {
        for (int view = 0; view < 2; view++)
        {
            Engine_FxDisplacement? layer = view == 0 ? Engine_FxDisplacement.Current : Engine_FxDisplacement.SecondView;
            if (layer is null || !GodotObject.IsInstanceValid(layer)) continue;
            // Read again only when the layer changed or its host went (split screen re-hosts), and
            // every 30 frames, as Host() can move a layer to another live viewport.
            ulong frame = Engine.GetProcessFrames();
            if (!ReferenceEquals(_hostLayer[view], layer) || _hostOf[view] is not { } cached ||
                !GodotObject.IsInstanceValid(cached) || frame - _hostReadFrame[view] >= 30)
            {
                _hostLayer[view] = layer;
                _hostOf[view] = HostField?.GetValue(layer) as Viewport;
                _hostCamera[view] = CameraField?.GetValue(layer) as Camera3D;
                _hostReadFrame[view] = frame;
            }
            if (!ReferenceEquals(_hostCamera[view], tested) || _hostOf[view] is not { } host || !GodotObject.IsInstanceValid(host)) continue;
            if (host.GetCamera3D() is { } live && GodotObject.IsInstanceValid(live)) return live;
        }
        return tested;
    }

    private static readonly Engine_FxDisplacement?[] _hostLayer = new Engine_FxDisplacement?[2];
    private static readonly Viewport?[] _hostOf = new Viewport?[2];
    private static readonly Camera3D?[] _hostCamera = new Camera3D?[2];
    private static readonly ulong[] _hostReadFrame = new ulong[2];

    internal static bool ReadFrustum(Camera3D camera, Plane[] into)
    {
        Godot.Collections.Array<Plane> planes = camera.GetFrustum();
        using var handle = (Godot.Collections.Array)planes;
        if (planes.Count != into.Length) return false;
        for (int i = 0; i < into.Length; i++) into[i] = planes[i];
        return true;
    }

    // ---- The debug reply ----

    private sealed class Snapshot
    {
        public ulong Frame;
        public bool View0, View1;
        public int Gated, Hard;
        public bool? GatedInView;
        public readonly int[] Calls = new int[KindNames.Length];
        public readonly List<Waker> Named = new();
    }

    /// <summary>The last frame end's, written in place: no allocation a frame.</summary>
    private static readonly Snapshot _last = new();
    private static bool _haveLast;

    private static void Fill(Snapshot snap, ulong frame)
    {
        snap.Frame = frame;
        snap.View0 = Engine_FxDisplacement.IsViewActive(0);
        snap.View1 = Engine_FxDisplacement.IsViewActive(1);
        snap.Gated = snap.Hard = 0;
        snap.GatedInView = null;
        Array.Clear(snap.Calls);
        snap.Named.Clear();
        if (_nowFrame == frame)
        {
            snap.Gated = _gated;
            snap.Hard = _hard;
            snap.GatedInView = _testedFrame == frame ? _seenAny : null;
            Array.Copy(_calls, snap.Calls, _calls.Length);
            // Hard wakes first: they are what keeps the layer on.
            for (int pass = 0; pass < 2 && snap.Named.Count < Named; pass++)
            {
                for (int i = 0; i < _nowCount && snap.Named.Count < Named; i++)
                {
                    if (_now[i].Gated == (pass == 1)) snap.Named.Add(_now[i]);
                }
            }
        }
    }

    /// <summary>
    /// The layer's state at the last frame end and what woke it, for the `metrics` reply.
    /// <paramref name="camera"/> (the main view's, now) answers each named waker's `inView`.
    /// </summary>
    public static Dictionary<string, object?> Describe(Camera3D? camera)
    {
        if (Active) EnsureFrameEnd();
        Snapshot snap = _last;
        if (!_haveLast)
        {
            snap = new Snapshot();
            Fill(snap, Engine.GetProcessFrames());
        }
        var calls = new Dictionary<string, object?>();
        for (int i = 0; i < KindNames.Length; i++) calls[KindNames[i]] = snap.Calls[i];
        if ((camera is null || !GodotObject.IsInstanceValid(camera)) && Engine_FxDisplacement.Current is { } layer &&
            GodotObject.IsInstanceValid(layer))
        {
            camera = CameraField?.GetValue(layer) as Camera3D;
        }
        bool haveFrustum = camera is not null && GodotObject.IsInstanceValid(camera) && ReadFrustum(camera, Frustum);
        var named = new List<Dictionary<string, object?>>();
        foreach (Waker waker in snap.Named)
        {
            var row = new Dictionary<string, object?> { ["kind"] = KindNames[(int)waker.Kind] };
            if (waker.Owner is Node node && GodotObject.IsInstanceValid(node))
            {
                row["name"] = node is FableFx fx ? fx.EffectName : (string)node.Name;
                row["path"] = node.IsInsideTree() ? node.GetPath().ToString() : (string)node.Name;
            }
            else if (waker.Label is not null)
            {
                row["name"] = waker.Label;
            }
            if (waker.HasBox && haveFrustum)
            {
                row["inView"] = Engine_DisplacementMeshes.BoxTouchesFrustum(waker.Box, Transform3D.Identity, Frustum);
            }
            named.Add(row);
        }
        ulong now = Engine.GetProcessFrames();
        return new Dictionary<string, object?>
        {
            ["inViewOnly"] = Enabled,
            ["pumped"] = _haveLast && _pumpFrame == snap.Frame,
            ["framesAgo"] = snap.Frame > now ? (object?)null : now - snap.Frame,
            ["drawing"] = new[] { snap.View0, snap.View1 },
            ["calls"] = calls,
            ["outright"] = snap.Hard,
            ["gated"] = snap.Gated,
            ["gatedInView"] = snap.GatedInView,
            ["wakers"] = named,
            ["rect"] = AndroidDisplacementRect.Describe(),
        };
    }
}

/// <summary>Runs <see cref="AndroidDisplacementGate"/>'s frame end after every other _Process (GameSession included).</summary>
public sealed partial class AndroidDisplacementFrameEnd : Node
{
    public override void _Ready() => ProcessPriority = int.MaxValue - 1;

    public override void _Process(double delta) => AndroidDisplacementGate.EndFrame();
}
