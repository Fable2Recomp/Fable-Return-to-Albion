using System;
using Godot;

namespace Refii.Level;

public partial class Engine_DynamicSurfaceInteractions : Node3D
{
    public const int ImpactLimit = 128;
    private readonly Engine_DynamicInteractionState _state = new();
    private readonly Random _random = new(73421);
    private readonly float[] _expires = new float[ImpactLimit];
    private float[]? _pixels;
    private byte[]? _bytes;
    private Image? _image;
    private ImageTexture? _texture;
    private MultiMeshInstance3D? _impacts;
    private ShaderMaterial? _material;
    private float _time;
    private float _mapTimer;
    private int _nextImpact;
    private bool _enabled;
    private bool _mapPublished;
    private Vector2 _mapCentre;
    private Vector3? _lastActor;

    public int TrackCount => _state.Tracks.Count;
    public int ImpactCount { get; private set; }
    public int MapUpdates { get; private set; }
    public int TotalImpacts { get; private set; }
    public Engine_LandscapeGround? Ground { get; set; }
    public Node3D? GroundRoot { get; set; }

    public void Advance(float delta, Vector3? actor, Vector3 eye, in DynamicWeatherState weather,
        Engine_DynamicSurfaceState surface, bool enabled, float daylight)
    {
        if (!enabled || !eye.IsFinite()) { Disable(); return; }
        if (!float.IsFinite(delta) || delta <= 0f) return;
        _enabled = true;
        _time += delta;
        _state.Fade(delta, weather.SnowFall, surface.Snow);
        bool grounded = false;
        if (actor is { } position && TryGround(position, out var contact))
        {
            grounded = MathF.Abs(position.Y - contact.Position.Y) < 0.4f && Exposed(contact.Position);
            if (_lastActor is { } previous && position.DistanceSquaredTo(previous) > 64f)
                _state.Reset();
        }
        _lastActor = actor;
        _state.Move(actor, grounded, (point, forward) =>
        {
            if (TryGround(point, out var contact) && Exposed(contact.Position))
                Step(contact, forward, surface.Snow, surface.Puddles);
        });
        Vector3 centre = actor is { } hero && hero.IsFinite() ? hero : eye;
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
        Vector2 mapCentre = new(MathF.Floor(centre.X), MathF.Floor(centre.Z));
        if (_mapTimer <= 0f || mapCentre.DistanceSquaredTo(_mapCentre) > 4f)
        {
            _mapTimer = 0.15f;
            PublishTracks(mapCentre);
        }
        ImpactCount = 0;
        foreach (float expires in _expires) if (expires > _time) ImpactCount++;
        if (_impacts is not null)
        {
            _impacts.Visible = ImpactCount > 0;
            _material!.SetShaderParameter("impact_time", _time);
            _material.SetShaderParameter("brightness", 0.2f + 0.8f * Math.Clamp(daylight, 0f, 1f));
        }
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
            _mapTimer = 0f;
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
        float value = MathF.Sin(p.Dot(new Vector2(127.1f, 311.7f))) * 43758.5453f;
        return value - MathF.Floor(value);
    }

    private void PublishTracks(Vector2 centre)
    {
        if (_state.Tracks.Count == 0)
        {
            if (_mapPublished) RenderingServer.GlobalShaderParameterSet("dynamic_track_projection", Vector4.Zero);
            _mapPublished = false;
            return;
        }
        _mapCentre = centre;
        _pixels ??= new float[Engine_DynamicInteractionState.MapSize * Engine_DynamicInteractionState.MapSize * 2];
        _bytes ??= new byte[_pixels.Length * sizeof(float)];
        _state.Rasterize(_pixels, centre);
        Buffer.BlockCopy(_pixels, 0, _bytes, 0, _bytes.Length);
        if (_image is null)
        {
            _image = Image.CreateFromData(Engine_DynamicInteractionState.MapSize, Engine_DynamicInteractionState.MapSize,
                false, Image.Format.Rgf, _bytes);
            _texture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _image.SetData(Engine_DynamicInteractionState.MapSize, Engine_DynamicInteractionState.MapSize,
                false, Image.Format.Rgf, _bytes);
            _texture!.Update(_image);
        }
        RenderingServer.GlobalShaderParameterSet("dynamic_track_map", _texture!);
        RenderingServer.GlobalShaderParameterSet("dynamic_track_projection",
            new Vector4(centre.X, centre.Y, Engine_DynamicInteractionState.MapExtent, 1f));
        _mapPublished = true;
        MapUpdates++;
    }

    private void Spawn(DynamicSurfaceContact contact, float kind, float strength)
    {
        if (_impacts is null) BuildImpacts();
        Vector3 normal = contact.Normal.Normalized();
        Vector3 right = normal.Cross(Vector3.Forward).Normalized();
        Basis basis = new(right, normal, right.Cross(normal));
        int slot = _nextImpact++ % ImpactLimit;
        _impacts!.Multimesh.SetInstanceTransform(slot, new Transform3D(basis, contact.Position + normal * 0.018f));
        _impacts.Multimesh.SetInstanceCustomData(slot, new Color(_time, kind,
            Engine_DynamicInteractionState.Unit(strength), (float)_random.NextDouble()));
        _expires[slot] = _time + (kind == 1f ? 0.85f : kind == 2f ? 0.8f : 0.4f);
        TotalImpacts++;
    }

    private void BuildImpacts()
    {
        const int parts = 7;
        var vertices = new Vector3[parts * 6];
        var uv = new Vector2[vertices.Length];
        var ids = new Vector2[vertices.Length];
        Vector2[] corners = { new(0, 0), new(1, 0), new(1, 1), new(0, 0), new(1, 1), new(0, 1) };
        for (int i = 0; i < vertices.Length; i++) { uv[i] = corners[i % 6]; ids[i] = new Vector2(i / 6, 0); }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = ids;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(Vector3.One * -1f, Vector3.One * 2f);
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true, Mesh = mesh, InstanceCount = ImpactLimit,
        };
        for (int i = 0; i < ImpactLimit; i++) multi.SetInstanceCustomData(i, new Color(-10000f, 0, 0, 0));
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
        if (!_enabled) return;
        _enabled = false;
        _state.Reset();
        _lastActor = null;
        Array.Clear(_expires);
        ImpactCount = 0;
        _mapTimer = 0f;
        if (_impacts is not null)
        {
            _impacts.Visible = false;
            for (int i = 0; i < ImpactLimit; i++)
                _impacts.Multimesh.SetInstanceCustomData(i, new Color(-10000f, 0, 0, 0));
        }
        if (_mapPublished) RenderingServer.GlobalShaderParameterSet("dynamic_track_projection", Vector4.Zero);
        _mapPublished = false;
    }

    public override void _ExitTree()
    {
        Disable();
        _texture?.Dispose();
        _image?.Dispose();
    }
}
