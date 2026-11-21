using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `occlusion_culling` as off, on or auto, and the occlusion buffer's own settings. Auto keeps the
/// buildings' occluders (Engine_BuildingOccluders) but turns the viewport's occlusion culling on only
/// while one stands within `occlusion_auto_metres` of the camera, and off again past 1.5 times that.
/// </summary>
/// <remarks>
/// WHY. Godot updates the occlusion buffer every frame while the viewport has it on, whether or not
/// any occluder is near: it casts its camera rays on the worker threads and the render thread waits
/// for them, then builds the depth mips and tests every instance that passed the frustum. Nothing
/// checks for an empty or far-away occluder set first. At Medium that cost 2-3 ms of render-thread
/// CPU at every forest and lake stop (no draw saved there), against -15% GPU on a town street.
///
/// The occluders stay in the scenario while culling is off; Godot only takes in their changes on
/// the next frame the buffer is on, and builds its BVH on a thread of its own.
/// </remarks>
public static class AndroidOcclusionAuto
{
    /// <summary>The shared refii.toml setting: "false", "true" or "auto".</summary>
    public const string Key = "occlusion_culling";

    /// <summary>Auto's switch-on distance in metres.</summary>
    public const string MetresKey = "occlusion_auto_metres";

    /// <summary>Godot's rendering/occlusion_culling/occlusion_rays_per_thread.</summary>
    public const string RaysKey = "occlusion_rays_per_thread";

    /// <summary>Godot's rendering/occlusion_culling/bvh_build_quality: 0 low, 1 medium, 2 high.</summary>
    public const string BuildQualityKey = "occlusion_bvh_quality";

    public const float DefaultMetres = 50f;
    public const int DefaultRays = 512;
    public const int DefaultBuildQuality = 2;

    /// <summary>Auto switches off past this many times its switch-on distance.</summary>
    public const float OffFactor = 1.5f;

    /// <summary>Seconds between auto's distance checks.</summary>
    private const double CheckSeconds = 0.25;

    public enum Mode
    {
        Off,
        On,
        Auto,
    }

    /// <summary>A setting's mode: true, 1 or on is on; auto is auto; anything else off. Pure.</summary>
    public static Mode Parse(string? value)
    {
        string text = (value ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
        return text switch
        {
            "true" or "1" or "on" => Mode.On,
            "auto" => Mode.Auto,
            _ => Mode.Off,
        };
    }

    /// <summary>
    /// The mode on this platform. A phone reads off, on or auto; a desktop reads upstream's boolean, so
    /// auto there is off. Pure.
    /// </summary>
    public static Mode ModeFor(IReadOnlyDictionary<string, string> values, bool android)
    {
        if (android)
        {
            values.TryGetValue(Key, out string? mode);
            return Parse(mode);
        }
        return Engine_RefiiConfig.Bool(values, Key, false) ? Mode.On : Mode.Off;
    }

    /// <summary>Whether auto wants culling on, given its state now and the nearest occluder. Pure.</summary>
    public static bool Decide(bool active, float nearestMetres, float onMetres) =>
        active ? nearestMetres <= onMetres * OffFactor : nearestMetres <= onMetres;

    /// <summary>Distance from a point to a box; 0 inside it. Pure.</summary>
    public static float Distance(Aabb box, Vector3 point) =>
        point.Clamp(box.Position, box.End).DistanceTo(point);

    /// <summary>The auto distance as set, clamped to 5..500 m. Pure.</summary>
    public static float Metres(IReadOnlyDictionary<string, string> values)
    {
        string raw = Engine_RefiiConfig.String(values, MetresKey, string.Empty);
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) && float.IsFinite(m)
            ? Math.Clamp(m, 5f, 500f)
            : DefaultMetres;
    }

    /// <summary>The rays per worker thread as set, at least 32. Pure.</summary>
    public static int Rays(IReadOnlyDictionary<string, string> values) =>
        Math.Max(32, Int(values, RaysKey, DefaultRays));

    /// <summary>The BVH build quality as set, 0..2. Pure.</summary>
    public static int BuildQuality(IReadOnlyDictionary<string, string> values) =>
        Math.Clamp(Int(values, BuildQualityKey, DefaultBuildQuality), 0, 2);

    private static int Int(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        int.TryParse(Engine_RefiiConfig.String(values, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private sealed record Settings(Mode Mode, float Metres, int Rays, int BuildQuality);

    private static readonly Lazy<Settings> Read = new(() =>
    {
        try
        {
            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            return new Settings(ModeFor(values, Engine_Platform.IsAndroid), Metres(values), Rays(values), BuildQuality(values));
        }
        catch (Exception)
        {
            return new Settings(Mode.Off, DefaultMetres, DefaultRays, DefaultBuildQuality);
        }
    });

    /// <summary>The mode for this run, read once at the first use.</summary>
    public static Mode Current => Read.Value.Mode;

    /// <summary>Whether occluders are built at all (on or auto).</summary>
    public static bool Builds => Current != Mode.Off;

    /// <summary>Whether auto decides the viewport's switch.</summary>
    public static bool Auto => Current == Mode.Auto;

    private static readonly List<(GeometryInstance3D Owner, Aabb[] Boxes)> Placed = new();
    private static Viewport? _viewport;
    private static bool _active;
    private static bool _tuned;
    private static long _lastCheck;

    /// <summary>A building's occluders just placed; <paramref name="placements"/> as Engine_BuildingOccluders got them.</summary>
    public static void Track(GeometryInstance3D owner, Mesh mesh, IReadOnlyList<Transform3D>? placements)
    {
        Tune();
        if (!Auto)
        {
            return;
        }

        _viewport = owner.GetViewport();
        Transform3D world = owner.GlobalTransform;
        Aabb local = mesh.GetAabb();
        int count = placements?.Count ?? 1;
        var boxes = new Aabb[count];
        for (int i = 0; i < count; i++)
        {
            boxes[i] = world * ((placements?[i] ?? Transform3D.Identity) * local);
        }
        Placed.Add((owner, boxes));
    }

    /// <summary>Godot's buffer settings, when set away from its defaults; once.</summary>
    private static void Tune()
    {
        if (_tuned)
        {
            return;
        }
        _tuned = true;
        Settings s = Read.Value;
        if (s.Rays != DefaultRays)
        {
            RenderingServer.ViewportSetOcclusionRaysPerThread(s.Rays);
        }
        if (s.BuildQuality != DefaultBuildQuality)
        {
            RenderingServer.ViewportSetOcclusionCullingBuildQuality(
                (RenderingServer.ViewportOcclusionCullingBuildQuality)s.BuildQuality);
        }
        GD.Print($"[occlusion] {Key} = {s.Mode.ToString().ToLowerInvariant()}, auto within {s.Metres:0} m, "
            + $"{s.Rays} rays per thread, BVH quality {s.BuildQuality}");
    }

    /// <summary>Auto's check, from Engine_BuildingOccluders.Pump each frame; it looks every quarter second.</summary>
    public static void Tick()
    {
        if (!Auto || _viewport is null)
        {
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastCheck, now).TotalSeconds < CheckSeconds)
        {
            return;
        }
        _lastCheck = now;

        if (!GodotObject.IsInstanceValid(_viewport))
        {
            _viewport = null;
            return;
        }
        if (_viewport.GetCamera3D() is not { } camera)
        {
            return;
        }

        Vector3 eye = camera.GlobalPosition;
        float nearest = float.PositiveInfinity;
        for (int i = Placed.Count - 1; i >= 0; i--)
        {
            (GeometryInstance3D owner, Aabb[] boxes) = Placed[i];
            if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree())
            {
                Placed.RemoveAt(i);
                continue;
            }
            foreach (Aabb box in boxes)
            {
                nearest = MathF.Min(nearest, Distance(box, eye));
            }
        }

        bool want = Decide(_active, nearest, Read.Value.Metres);
        if (want != _active)
        {
            _active = want;
            _viewport.UseOcclusionCulling = want;
            GD.Print($"[occlusion] auto {(want ? "on" : "off")}, nearest occluder "
                + (float.IsFinite(nearest) ? $"{nearest:0} m" : "none"));
        }
    }

    /// <summary>The region's occluders are gone; auto switches off at its next check.</summary>
    public static void Clear() => Placed.Clear();
}
