using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

/// <summary>
/// The modern renderer's decals: each decal the guest states (<see cref="Engine_DecalFeed"/>) as
/// a Godot <see cref="Godot.Decal"/>, projected onto whatever the box meets and lit with the
/// surface it lands on.
/// </summary>
/// <remarks>
/// The title's decal is a 17 x 17 grid over Right = n(dir x Z), Up = n(Right x dir), point (c, r)
/// at pos + Right ((c - 8) / 8) sx - Up ((r - 8) / 8) sy with uv (c / 16, r / 16), pushed onto the
/// surface along dir; faded from 44.8 m over 19.2 m and gone past 64 m (Engine_ClassicDecals).
/// A Godot decal projects along its own -Y and maps its X and Z to u and v, so X = Right,
/// Z = -Up, Y = -dir lays the texture as the grid does. The box is centred on the stated point
/// (the decal was laid where it hit, so the surface is there) and as deep as the decal is wide.
/// The texture becomes the surface's albedo; Godot's lights, GI and shadows do the rest. A decal
/// without an owner leaves entities out (the title painted only what its projector saw when the
/// decal was laid, never someone walking over it later).
/// </remarks>
public partial class Engine_ModernDecals : Node3D
{
    /// <summary>The title's distance fade: full to 44.8 m, gone at 64 m.</summary>
    private const float FadeBegin = 44.8f, FadeLength = 19.2f;

    private readonly Engine_DecalFeed _feed = new();
    private readonly (uint Serial, Godot.Decal? Node)[] _drawn = new (uint, Godot.Decal?)[Engine_DecalFeed.Slots];
    private readonly Dictionary<uint, Texture2D?> _textures = new();

    private bool _pumpedOnce;

    /// <summary>The live instance.</summary>
    public static Engine_ModernDecals? Instance { get; private set; }

    /// <summary>Decals placed at the last pump.</summary>
    public int Drawn { get; private set; }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    /// <summary>The modern decals under <paramref name="host"/>, made on first use.</summary>
    public static Engine_ModernDecals Ensure(Node host)
    {
        if (Instance is { } cached && IsInstanceValid(cached) && cached.GetParent() == host)
        {
            return cached;
        }
        if (host.GetNodeOrNull<Engine_ModernDecals>(nameof(Engine_ModernDecals)) is { } existing)
        {
            Instance = existing;
            return existing;
        }
        var decals = new Engine_ModernDecals { Name = nameof(Engine_ModernDecals) };
        host.AddChild(decals);
        Instance = decals;
        return decals;
    }

    /// <summary>Follows the guest's decals. Main thread, once a frame.</summary>
    public void Pump()
    {
        if (_pumpedOnce && !Refii.Core.Engine_GuestFrame.Changed)
        {
            return;
        }
        _pumpedOnce = true;

        _feed.Pump();
        System.ReadOnlySpan<Engine_DecalFeed.Decal?> ring = _feed.Ring;
        int drawn = 0;
        for (int s = 0; s < ring.Length; s++)
        {
            Engine_DecalFeed.Decal? decal = ring[s];
            if (_drawn[s].Node is { } old && (decal is null || _drawn[s].Serial != decal.Serial))
            {
                old.QueueFree();
                _drawn[s] = default;
            }
            if (decal is null)
            {
                continue;
            }
            if (_drawn[s].Node is null)
            {
                if (Make(decal) is not { } made)
                {
                    continue;
                }
                AddChild(made);
                _drawn[s] = (decal.Serial, made);
                Place(made, decal);
            }
            else if (decal.Owner != 0u)
            {
                // An owned decal rides its owner.
                Place(_drawn[s].Node!, decal);
            }
            ++drawn;
        }
        Drawn = drawn;
    }

    private Godot.Decal? Make(Engine_DecalFeed.Decal decal)
    {
        if (!_textures.TryGetValue(decal.Texture, out Texture2D? texture))
        {
            texture = Refii.Level.Engine_EnvironmentTextures.Load(decal.Texture, Refii.Models.Engine_BnkMeshLoader.ReadBytes, out _);
            _textures[decal.Texture] = texture;
        }
        if (texture is null)
        {
            return null;
        }
        return new Godot.Decal
        {
            TopLevel = true,
            TextureAlbedo = texture,
            AlbedoMix = 1f,
            NormalFade = 0f,
            DistanceFadeEnabled = true,
            DistanceFadeBegin = FadeBegin,
            DistanceFadeLength = FadeLength,
            // The title projects a decal once, onto what its projector saw when it was laid: a
            // character walking over it later is not painted. A decal on an owner rides that
            // owner, so it keeps the entities' layer. Particles are never painted.
            CullMask = (decal.Owner != 0u
                ? uint.MaxValue
                : uint.MaxValue & ~Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask) & ~Refii.Models.Engine_ClassicParticles.Layer,
        };
    }

    /// <summary>The box onto the decal's grid: X = Right, Z = -Up, projecting along dir.</summary>
    private static void Place(Godot.Decal node, Engine_DecalFeed.Decal decal)
    {
        Vector3 dir = decal.Direction.Normalized();
        Vector3 right = dir.Cross(new Vector3(0f, 0f, 1f));
        // Straight down or up the title's own construction degenerates; any right serves.
        right = right.LengthSquared() < 1e-8f ? new Vector3(1f, 0f, 0f) : right.Normalized();
        Vector3 up = right.Cross(dir).Normalized();
        var basis = new Basis(ToGodot(right), ToGodot(-dir), ToGodot(-up));
        float width = 2f * Mathf.Max(decal.Half.X, 0.01f), height = 2f * Mathf.Max(decal.Half.Y, 0.01f);
        node.Size = new Vector3(width, Mathf.Max(Mathf.Max(width, height), 0.5f), height);
        node.GlobalTransform = new Transform3D(basis, ToGodot(decal.Position));
    }

    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
}
