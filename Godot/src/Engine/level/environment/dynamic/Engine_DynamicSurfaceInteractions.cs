using System;
using Godot;

namespace Refii.Level;

public partial class Engine_DynamicSurfaceInteractions : Node3D
{
    public const int ImpactLimit = 128;
    private readonly Engine_DynamicInteractionState _state = new();
    private readonly Engine_DynamicRoofMap _roofMap = new();
    private byte[]? _roofBytes;
    private Image? _roofImage;
    private ImageTexture? _roofTexture;
    private bool _roofPublished;
    private readonly Random _random = new(73421);
    private readonly float[] _expires = new float[ImpactLimit];
    private readonly Transform3D[] _impactTransforms = new Transform3D[ImpactLimit];
    private readonly Color[] _impactData = new Color[ImpactLimit];
    private readonly float[] _impactBuffer = new float[ImpactLimit * 16];
    private readonly Engine_DynamicFootContact _leftFoot = new();
    private readonly Engine_DynamicFootContact _rightFoot = new();
    private ulong _footRig;
    private bool _impactsDirty;
    private Action<Vector3, Vector2>? _distanceStep;
    private float _stepSnow, _stepPuddles;
    private Engine_DynamicSurfaceState? _queuedSurface;
    private DynamicWeatherState _queuedWeather;
    private Vector3 _queuedEye;
    private float _queuedDaylight;
    private SubViewport? _trackViewport;
    private MultiMesh? _trackInstances;
    private ShaderMaterial? _trackMaterial;
    private float[]? _trackBuffer;
    private int _trackRevision = -1;
    private int _visibleTracks;
    private Vector3 _trackOrigin;
    private bool _trackHasOrigin;
    private MultiMeshInstance3D? _impacts;
    private ShaderMaterial? _material;
    private float _time;
    private float _mapTimer;
    private int _nextImpact;
    private bool _enabled;
    private bool _mapPublished;
    private Vector3? _lastActor;

    public int TrackCount => _state.Tracks.Count;
    public int ImpactCount { get; private set; }
    public int MapUpdates { get; private set; }
    public int TrackUploads { get; private set; }
    public int TotalImpacts { get; private set; }
    public Engine_LandscapeGround? Ground { get; set; }
    public Node3D? GroundRoot { get; set; }

    public void QueueAdvance(Vector3 eye, in DynamicWeatherState weather, Engine_DynamicSurfaceState surface,
        bool enabled, float daylight)
    {
        if (!enabled) { Disable(); return; }
        _queuedEye = eye;
        _queuedWeather = weather;
        _queuedSurface = surface;
        _queuedDaylight = daylight;
    }

    public override void _Process(double delta)
    {
        if (_queuedSurface is not { } surface) return;
        _queuedSurface = null;
        using var measure = Refii.Core.Engine_FramePhases.Measure("sky.surface_interactions");
        DynamicActorFeet? feet = Refii.Game.GameEntities.Current is { } entities &&
            GodotObject.IsInstanceValid(entities) && entities.TryHeroFeet(out var pose) ? pose : null;
        Advance((float)delta, feet?.Root ?? Refii.Game.GameEntities.HeroPosition, _queuedEye,
            _queuedWeather, surface, true, _queuedDaylight, feet, false);
    }

    public void Advance(float delta, Vector3? actor, Vector3 eye, in DynamicWeatherState weather,
        Engine_DynamicSurfaceState surface, bool enabled, float daylight,
        DynamicActorFeet? feet = null, bool distanceSteps = true)
    {
        if (!enabled || !eye.IsFinite()) { Disable(); return; }
        if (!float.IsFinite(delta) || delta <= 0f) return;
        _enabled = true;
        Vector3 centre = actor is { } hero && hero.IsFinite() ? hero : eye;
        PublishRoofs(new Vector2(centre.X, centre.Z));
        _time += delta;
        _state.Fade(delta, weather.SnowFall, surface.Snow);
        if (surface.Snow > 0.08f && _trackViewport is null) BuildTrackMap();
        bool warmingImpacts = _impacts is null && (surface.Snow > 0.08f || weather.Rain > 0.001f);
        if (warmingImpacts)
        {
            BuildImpacts();
            Vector3 warmCentre = actor is { } warmActor && warmActor.IsFinite() ? warmActor : eye;
            _impacts!.Multimesh.CustomAabb = new Aabb(warmCentre - Vector3.One * 3f, Vector3.One * 6f);
        }
        if (actor is { } position && _lastActor is { } previous && position.DistanceSquaredTo(previous) > 64f)
        {
            _state.Reset();
            _leftFoot.Reset();
            _rightFoot.Reset();
        }
        _lastActor = actor;
        if (feet is { } animated && (surface.Snow > 0.08f || surface.Puddles > 0.05f))
        {
            if (_footRig != animated.Rig)
            {
                _leftFoot.Reset();
                _rightFoot.Reset();
                _footRig = animated.Rig;
            }
            AnimateFoot(delta, animated.Left, _leftFoot, surface);
            AnimateFoot(delta, animated.Right, _rightFoot, surface);
        }
        else
        {
            _leftFoot.Reset();
            _rightFoot.Reset();
            _footRig = 0;
            if (distanceSteps)
            {
                bool grounded = actor is { } at && TryGround(at, out var ground) &&
                    MathF.Abs(at.Y - ground.Position.Y) < 0.4f;
                _stepSnow = surface.Snow;
                _stepPuddles = surface.Puddles;
                _state.Move(actor, grounded, _distanceStep ??= DistanceStep);
            }
        }
        int count = _state.RainCount(delta, weather.Rain, true);
        for (int i = 0; i < count; i++)
        {
            float angle = (float)_random.NextDouble() * Mathf.Tau;
            float radius = MathF.Sqrt((float)_random.NextDouble()) * 10f;
            Vector3 sample = centre + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * radius;
            if (!TryGround(sample, out var hit) || !Exposed(hit.Position)) continue;
            Spawn(hit, 0f, Math.Clamp(weather.Rain, 0.2f, 1f));
        }
        _mapTimer -= delta;
        bool moved = new Vector2(centre.X - _trackOrigin.X, centre.Z - _trackOrigin.Z).LengthSquared() > 4f ||
            MathF.Abs(centre.Y - _trackOrigin.Y) > 8f;
        if (_trackRevision != _state.Revision || (_state.Tracks.Count > 0 && (_mapTimer <= 0f || moved)))
        {
            _mapTimer = 0.15f;
            PublishTracks(centre, moved);
        }
        RefreshImpacts(centre);
        if (_impacts is not null)
        {
            _impacts.Visible = ImpactCount > 0 || warmingImpacts;
            if (_impacts.Visible)
            {
                _material!.SetShaderParameter("impact_time", _time);
                _material.SetShaderParameter("brightness", 0.2f + 0.8f * Math.Clamp(daylight, 0f, 1f));
            }
        }
    }

    private void AnimateFoot(float delta, DynamicFootPose foot, Engine_DynamicFootContact state,
        Engine_DynamicSurfaceState surface)
    {
        if (!TryGround(foot.Position, out var ground)) { state.Reset(); return; }
        if (state.Advance(delta, foot, ground)) Step(ground, foot.Forward, surface.Snow, surface.Puddles);
    }

    private void DistanceStep(Vector3 point, Vector2 forward)
    {
        if (TryGround(point, out var contact)) Step(contact, forward, _stepSnow, _stepPuddles);
    }

    public bool TryGround(Vector3 point, out DynamicSurfaceContact contact)
    {
        contact = default;
        return Ground is not null && Engine_DynamicInteractionState.TryContact(Ground,
            GroundRoot is { } root && GodotObject.IsInstanceValid(root) && root.IsInsideTree()
                ? root.GlobalTransform : Transform3D.Identity, point, out contact);
    }

    private static bool Exposed(Vector3 point)
    {
        if (Engine_WeatherShelter.HasBox)
        {
            Vector3 q = (Engine_WeatherShelter.ToBox * point).Abs();
            if (MathF.Max(q.X, MathF.Max(q.Y, q.Z)) <= 1f) return false;
        }
        foreach (var roof in Engine_ClassicRoofs.Roofs)
            if (point.Y + 0.08f < roof.Top && Engine_ClassicRoofs.Contains(roof, new Vector2(-point.X, point.Z)))
                return false;
        return true;
    }

    public void Step(DynamicSurfaceContact contact, Vector2 forward, float snow, float puddles)
    {
        if (!_enabled || !Exposed(contact.Position)) return;
        if (snow > 0.08f)
        {
            _state.Stamp(contact, forward, Math.Clamp(snow * 2f, 0f, 1f));
            Spawn(contact, 1f, snow);
        }
        else if (puddles > 0.05f && PuddleAt(contact.Position, contact.Normal, puddles))
            Spawn(contact, 2f, puddles);
    }

    public void PreviewStep(Vector3 position, Vector2 forward, float snow, float puddles)
    {
        if (TryGround(position, out var hit)) Step(hit, forward, snow, puddles);
    }

    public static bool PuddleAt(Vector3 point, Vector3 normal, float puddles)
    {
        if (normal.Y < 0.92f) return false;
        Vector2 p = new Vector2(point.X, point.Z) * 0.27f;
        Vector2 cell = p.Floor();
        Vector2 f = p - cell;
        f = f * f * (Vector2.One * 3f - f * 2f);
        float noise = Mathf.Lerp(Mathf.Lerp(Hash(cell), Hash(cell + Vector2.Right), f.X),
            Mathf.Lerp(Hash(cell + Vector2.Down), Hash(cell + Vector2.One), f.X), f.Y);
        return noise > 1f - Engine_DynamicInteractionState.Unit(puddles) * 0.8f;
    }

    private static float Hash(Vector2 p)
    {
        unchecked
        {
            uint h = ((uint)(int)MathF.Floor(p.X) * 1597334677u) ^ ((uint)(int)MathF.Floor(p.Y) * 3812015801u);
            h = (h ^ (h >> 16)) * 2246822519u;
            h = (h ^ (h >> 13)) * 3266489917u;
            return ((h ^ (h >> 16)) & 16777215u) / 16777216f;
        }
    }

    private void BuildTrackMap()
    {
        _trackBuffer = new float[Engine_DynamicInteractionState.TrackLimit * Engine_DynamicInteractionState.InstanceStride];
        _trackInstances = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One },
            InstanceCount = Engine_DynamicInteractionState.TrackLimit,
            VisibleInstanceCount = 1,
            CustomAabb = new Aabb(new Vector3(0, 0, -1),
                new Vector3(Engine_DynamicInteractionState.MapSize, Engine_DynamicInteractionState.MapSize, 2)),
        };
        _trackInstances.Buffer = _trackBuffer;
        _trackMaterial = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://src/Engine/level/environment/dynamic/dynamic_snow_tracks.gdshader"),
        };
        _trackViewport = new SubViewport
        {
            Name = "SnowTrackMap", Size = new(Engine_DynamicInteractionState.MapSize, Engine_DynamicInteractionState.MapSize),
            Disable3D = true, TransparentBg = true, UseHdr2D = true, GuiDisableInput = true,
            World2D = new World2D(),
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        };
        AddChild(_trackViewport);
        _trackViewport.AddChild(new MultiMeshInstance2D { Multimesh = _trackInstances, Material = _trackMaterial });
    }

    private void PublishTracks(Vector3 centre, bool moved)
    {
        if (_state.Tracks.Count == 0)
        {
            if (_mapPublished) RenderingServer.GlobalShaderParameterSet("dynamic_track_projection", Vector4.Zero);
            _mapPublished = false;
            _trackRevision = _state.Revision;
            return;
        }
        if (_trackViewport is null) BuildTrackMap();
        moved |= !_trackHasOrigin;
        if (moved)
        {
            _trackOrigin = centre.Floor();
            _trackHasOrigin = true;
        }
        if (moved || _trackRevision != _state.Revision)
        {
            _visibleTracks = _state.WriteInstances(_trackBuffer!, _trackOrigin);
            _trackInstances!.Buffer = _trackBuffer!;
            _trackInstances.VisibleInstanceCount = _visibleTracks;
            _trackRevision = _state.Revision;
            TrackUploads++;
        }
        if (_visibleTracks == 0)
        {
            if (_mapPublished) RenderingServer.GlobalShaderParameterSet("dynamic_track_projection", Vector4.Zero);
            _mapPublished = false;
            return;
        }
        _trackMaterial!.SetShaderParameter("erosion", _state.Erosion);
        _trackViewport!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        if (!_mapPublished || moved)
        {
            if (!_mapPublished) RenderingServer.GlobalShaderParameterSet("dynamic_track_map", _trackViewport.GetTexture());
            RenderingServer.GlobalShaderParameterSet("dynamic_track_projection",
                new Vector4(_trackOrigin.X, _trackOrigin.Z, Engine_DynamicInteractionState.MapExtent, 1f));
            RenderingServer.GlobalShaderParameterSet("dynamic_track_height", _trackOrigin.Y);
        }
        _mapPublished = true;
        MapUpdates++;
    }

    private void PublishRoofs(Vector2 centre)
    {
        bool changed = _roofMap.Update(centre);
        if (!_roofMap.HasRoofs)
        {
            if (_roofPublished) RenderingServer.GlobalShaderParameterSet(SurfaceWeatherNames.dynamic_roof_projection, Vector4.Zero);
            _roofPublished = false;
            return;
        }
        if (changed || _roofTexture is null)
        {
            _roofBytes ??= new byte[_roofMap.Pixels.Length * sizeof(float)];
            Buffer.BlockCopy(_roofMap.Pixels, 0, _roofBytes, 0, _roofBytes.Length);
            if (_roofImage is null)
            {
                _roofImage = Image.CreateFromData(Engine_DynamicRoofMap.Size, Engine_DynamicRoofMap.Size,
                    false, Image.Format.Rgf, _roofBytes);
                _roofTexture = ImageTexture.CreateFromImage(_roofImage);
            }
            else
            {
                _roofImage.SetData(Engine_DynamicRoofMap.Size, Engine_DynamicRoofMap.Size,
                    false, Image.Format.Rgf, _roofBytes);
                _roofTexture!.Update(_roofImage);
            }
        }
        if (!_roofPublished)
        {
            RenderingServer.GlobalShaderParameterSet("dynamic_roof_map", _roofTexture!);
            _roofPublished = true;
        }
        if (changed)
        {
            RenderingServer.GlobalShaderParameterSet(SurfaceWeatherNames.dynamic_roof_projection,
                new Vector4(_roofMap.Centre.X, _roofMap.Centre.Y, Engine_DynamicRoofMap.Extent, 1f));
        }
    }

    private void Spawn(DynamicSurfaceContact contact, float kind, float strength)
    {
        if (_impacts is null) BuildImpacts();
        Vector3 normal = contact.Normal.Normalized();
        Vector3 right = normal.Cross(Vector3.Forward).Normalized();
        Basis basis = new(right, normal, right.Cross(normal));
        int slot = _nextImpact++ % ImpactLimit;
        _impactTransforms[slot] = new Transform3D(basis, contact.Position + normal * 0.018f);
        _impactData[slot] = new Color(_time, kind,
            Engine_DynamicInteractionState.Unit(strength), (float)_random.NextDouble());
        _expires[slot] = _time + (kind == 1f ? 0.85f : kind == 2f ? 0.8f : 0.4f);
        _impactsDirty = true;
        TotalImpacts++;
    }

    private void RefreshImpacts(Vector3 centre)
    {
        int count = 0;
        foreach (float expires in _expires) if (expires > _time) count++;
        if (!_impactsDirty && count == ImpactCount) return;
        ImpactCount = count;
        _impactsDirty = false;
        if (_impacts is null) return;
        Vector3 min = centre, max = centre;
        int index = 0;
        for (int slot = 0; slot < ImpactLimit; slot++)
        {
            if (_expires[slot] <= _time) continue;
            var t = _impactTransforms[slot];
            var data = _impactData[slot];
            min = min.Min(t.Origin);
            max = max.Max(t.Origin);
            _impactBuffer[index++] = t.Basis.X.X;
            _impactBuffer[index++] = t.Basis.Y.X;
            _impactBuffer[index++] = t.Basis.Z.X;
            _impactBuffer[index++] = t.Origin.X;
            _impactBuffer[index++] = t.Basis.X.Y;
            _impactBuffer[index++] = t.Basis.Y.Y;
            _impactBuffer[index++] = t.Basis.Z.Y;
            _impactBuffer[index++] = t.Origin.Y;
            _impactBuffer[index++] = t.Basis.X.Z;
            _impactBuffer[index++] = t.Basis.Y.Z;
            _impactBuffer[index++] = t.Basis.Z.Z;
            _impactBuffer[index++] = t.Origin.Z;
            _impactBuffer[index++] = data.R;
            _impactBuffer[index++] = data.G;
            _impactBuffer[index++] = data.B;
            _impactBuffer[index++] = data.A;
        }
        var multi = _impacts.Multimesh;
        if (count > 0) multi.Buffer = _impactBuffer;
        multi.VisibleInstanceCount = count;
        multi.CustomAabb = new Aabb(min - Vector3.One, max - min + Vector3.One * 2f);
    }

    private void BuildImpacts()
    {
        const int parts = 7;
        var vertices = new Vector3[parts * 4];
        var uv = new Vector2[vertices.Length];
        var ids = new Vector2[vertices.Length];
        Vector2[] corners = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        int[] quad = { 0, 1, 2, 0, 2, 3 };
        var indices = new int[parts * 6];
        for (int i = 0; i < vertices.Length; i++) { uv[i] = corners[i % 4]; ids[i] = new Vector2(i / 4, 0); }
        for (int i = 0; i < indices.Length; i++) indices[i] = i / 6 * 4 + quad[i % 6];
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = ids;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(Vector3.One * -1f, Vector3.One * 2f);
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true, Mesh = mesh, InstanceCount = ImpactLimit,
            VisibleInstanceCount = 1,
        };
        multi.Buffer = _impactBuffer;
        _material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://src/Engine/level/environment/dynamic/dynamic_surface_impact.gdshader"),
        };
        _impacts = new MultiMeshInstance3D
        {
            Name = "SurfaceImpacts", Multimesh = multi, MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 1f,
        };
        AddChild(_impacts);
        _impacts.TopLevel = true;
        _impacts.GlobalTransform = Transform3D.Identity;
    }

    public void Disable()
    {
        _queuedSurface = null;
        if (!_enabled) return;
        _enabled = false;
        _state.Reset();
        _lastActor = null;
        _leftFoot.Reset();
        _rightFoot.Reset();
        _footRig = 0;
        Array.Clear(_expires);
        Array.Clear(_impactBuffer);
        _impactsDirty = false;
        _nextImpact = 0;
        ImpactCount = 0;
        _mapTimer = 0f;
        _trackRevision = -1;
        _trackHasOrigin = false;
        _visibleTracks = 0;
        if (_trackInstances is not null) _trackInstances.VisibleInstanceCount = 0;
        if (_trackViewport is not null) _trackViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        if (_impacts is not null)
        {
            _impacts.Visible = false;
            _impacts.Multimesh.VisibleInstanceCount = 0;
        }
        if (_mapPublished) RenderingServer.GlobalShaderParameterSet(SurfaceWeatherNames.dynamic_track_projection, Vector4.Zero);
        _mapPublished = false;
        if (_roofPublished) RenderingServer.GlobalShaderParameterSet(SurfaceWeatherNames.dynamic_roof_projection, Vector4.Zero);
        _roofPublished = false;
    }

    public override void _ExitTree()
    {
        Disable();
        _roofTexture?.Dispose();
        _roofImage?.Dispose();
    }
}
