using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Models;

namespace Refii.Fx;

/// <summary>
/// FX mesh particles for both renderers: the meshes an effect emits (shields, shockwaves,
/// debris), drawn from the render twins the guest's effect keeps for them.
/// </summary>
/// <remarks>
/// THE TITLE'S RULE (`sub_821CEA80`, kind 4): one render twin per live mesh particle, made on the
/// particle's first frame and destroyed the frame it dies, its model the effect's mesh key; each
/// frame the effect states its position and rotation (messages 1, 2), uniform scale (6), tint (17
/// -> twin+240 -> PS c49 g_StandardMaterialTintColour) and alpha (16). The twin's own draw
/// (`sub_8221E5F0`) takes the alpha times its distance fade (full within twin+284, to nothing at
/// +292) and draws translucent below 0.996 (`sub_822251D8`: constant-colour blend by the alpha). A
/// displacement mesh (key IsDisplacementMesh, flag 0x800) is not scene geometry: its layer is the
/// displacement renderer's alone (`sub_82AA3C78`), where it draws its own diffuse (VS 172 / PS 173).
///
/// HERE each twin (<see cref="HostBridge.GetFxMeshes"/>, the effects' descriptors taken at
/// `sub_82456D58`) is a node on the model as the active renderer builds it, placed by the twin's
/// world rows, faded by node transparency and tinted by its instance uniform. Classic lights and
/// casts it as an entity through its own lists; Modern lets Godot light it and casts it through
/// Godot's shadows by the twin's flags. Modern's own simulated mesh particles stand down while
/// this draws (FableFx.GuestMeshTwins).
/// </remarks>
public partial class Engine_FxMeshTwins : Node
{
    private const int MaxMeshes = 512;
    private static readonly StringName ClassicTint = "ps_g_StandardMaterialTintColour";
    private static readonly StringName ModernTint = "entity_tint";

    private sealed class Particle
    {
        public required MeshInstance3D Node;
        public uint Model;
        public bool Displaces;
        public bool Attached;
        public long Seen;
    }

    private readonly float[] _buffer = new float[1 + MaxMeshes * HostBridge.FxMeshFloats];
    private readonly Dictionary<uint, Particle> _particles = new();
    private readonly List<uint> _gone = new();
    private readonly Dictionary<uint, Mesh?> _meshes = new();
    private readonly HashSet<uint> _loading = new();
    private readonly ConcurrentQueue<(uint Hash, Mesh? Mesh)> _loaded = new();
    private long _frames;

    /// <summary>The live instance.</summary>
    public static Engine_FxMeshTwins? Instance { get; private set; }

    /// <summary>Whether the guest's twin feed has answered: only then do simulated mesh particles stand down.</summary>
    public static bool FeedLive { get; private set; }

    /// <summary>Mesh particles drawn on the last pump.</summary>
    public int Drawn { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        Name = "FxMeshTwins";
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>Follows the effects' mesh twins. Main thread, before the displacement pump.</summary>
    public void Pump(Camera3D camera)
    {
        ++_frames;
        while (_loaded.TryDequeue(out (uint Hash, Mesh? Mesh) done))
        {
            _loading.Remove(done.Hash);
            _meshes[done.Hash] = done.Mesh;
        }

        uint got = HostBridge.GetFxMeshes(_buffer);
        // The export answers with at least its count word; a bridge without it answers nothing.
        FeedLive |= got >= 1;
        int count = got >= 1 ? Math.Min((int)_buffer[0], MaxMeshes) : 0;
        Vector3 eye = ToGuest(camera.GlobalPosition);
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 1 + i * HostBridge.FxMeshFloats;
            uint descriptor = BitConverter.SingleToUInt32Bits(_buffer[at]);
            uint model = BitConverter.SingleToUInt32Bits(_buffer[at + 1]);
            bool displaces = ((uint)_buffer[at + 2] & 0x800u) != 0u;
            if (MeshOf(model) is not { } mesh)
            {
                continue;
            }
            if (!_particles.TryGetValue(descriptor, out Particle? particle))
            {
                var node = new MeshInstance3D
                {
                    Name = $"FxMesh_{descriptor:X8}",
                    TopLevel = true,
                    GIMode = GeometryInstance3D.GIModeEnum.Dynamic,
                };
                AddChild(node);
                particle = new Particle { Node = node };
                _particles[descriptor] = particle;
            }
            particle.Seen = _frames;
            if (particle.Model != model || particle.Displaces != displaces || particle.Node.Mesh != mesh)
            {
                particle.Model = model;
                particle.Displaces = displaces;
                Dress(particle, mesh, (uint)_buffer[at + 2]);
            }
            if (displaces && !Engine_ClassicMaterial.Active)
            {
                // Modern draws no displacement twin yet (see Dress).
                particle.Node.Visible = false;
                continue;
            }

            // The world rows (guest, translation in w) onto Godot: a Godot model point q is the
            // file's (q.x, -q.z, q.y); the guest world point goes to Godot as (-x, z, y).
            Vector3 Row(int r) => new(_buffer[at + 3 + r * 4], _buffer[at + 4 + r * 4], _buffer[at + 5 + r * 4]);
            Vector3 r0 = Row(0), r1 = Row(1), r2 = Row(2);
            var t = new Vector3(_buffer[at + 6], _buffer[at + 10], _buffer[at + 14]);
            Vector3 Guest(Vector3 file) => new(r0.Dot(file) + t.X, r1.Dot(file) + t.Y, r2.Dot(file) + t.Z);
            Vector3 origin = ToGodot(Guest(Vector3.Zero));
            var basis = new Basis(
                ToGodot(Guest(new Vector3(1f, 0f, 0f))) - origin,
                ToGodot(Guest(new Vector3(0f, 0f, 1f))) - origin,
                ToGodot(Guest(new Vector3(0f, -1f, 0f))) - origin);
            particle.Node.GlobalTransform = new Transform3D(basis, origin);

            // Alpha x the twin's distance fade.
            float alpha = _buffer[at + 19];
            float full = _buffer[at + 20], slope = _buffer[at + 21], end = _buffer[at + 22];
            float distance = eye.DistanceTo(Guest(Vector3.Zero));
            if (end > 0f && distance > end)
            {
                alpha = 0f;
            }
            else if (distance > full && slope != 0f)
            {
                alpha *= Math.Clamp(1f - (distance - full) * slope, 0f, 1f);
            }
            bool shown = alpha > 0f;
            particle.Node.Visible = shown;
            if (!shown)
            {
                continue;
            }
            ++drawn;
            if (displaces)
            {
                if (Engine_ClassicMaterial.Active)
                {
                    ++Engine_ClassicDisplacement.Drawn;
                }
                continue;
            }
            particle.Node.Transparency = alpha < 0.99609375f ? 1f - alpha : 0f;
            particle.Node.SetInstanceShaderParameter(Engine_ClassicMaterial.Active ? ClassicTint : ModernTint,
                new Vector4(_buffer[at + 15], _buffer[at + 16], _buffer[at + 17], _buffer[at + 18]));
        }

        _gone.Clear();
        foreach ((uint descriptor, Particle particle) in _particles)
        {
            if (particle.Seen != _frames)
            {
                particle.Node.QueueFree();
                _gone.Add(descriptor);
            }
        }
        foreach (uint descriptor in _gone)
        {
            _particles.Remove(descriptor);
        }
        Drawn = drawn;
    }

    /// <summary>
    /// The node on its mesh: scene geometry lit and cast as the twin's flags state (the effect's
    /// create flags carry no 0x40 / 0x80, so both cascades), or a displacement mesh on the
    /// displacement layer with its own diffuse.
    /// </summary>
    private void Dress(Particle particle, Mesh mesh, uint flags)
    {
        MeshInstance3D node = particle.Node;
        node.Mesh = mesh;
        if (particle.Displaces && !Engine_ClassicMaterial.Active)
        {
            // Modern: not drawn here yet; its own displacement layer still draws the simulated ones.
            node.Visible = false;
            node.Layers = 0u;
            return;
        }
        if (!Engine_ClassicMaterial.Active)
        {
            node.Layers = 1u;
            node.CastShadow = (Engine_ClassicShadows.CastingOf(flags) &
                (Engine_ClassicShadows.CastsNear | Engine_ClassicShadows.CastsFar)) != 0
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;
            return;
        }
        if (particle.Displaces)
        {
            node.Layers = Engine_ClassicDisplacement.Layer;
            node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                node.SetSurfaceOverrideMaterial(s, Engine_ClassicDisplacement.MeshMaterial(mesh.SurfaceGetMaterial(s)));
            }
            return;
        }
        node.Layers = 1u;
        node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        if (!particle.Attached)
        {
            particle.Attached = true;
            Engine_ClassicLitInstances.Attach(node);
            Engine_ClassicCasters.Mark(node, Engine_ClassicShadows.CastingOf(flags), 0f);
            Engine_ClassicCasters.Register(node, dynamic: true);
        }
    }

    /// <summary>The model a hash names, built off the main thread once; null until it stands.</summary>
    private Mesh? MeshOf(uint hash)
    {
        if (_meshes.TryGetValue(hash, out Mesh? mesh))
        {
            return mesh;
        }
        if (_loading.Contains(hash))
        {
            return null;
        }
        if (Refii.Level.Engine_PropModelIndex.Resolve(hash, Array.Empty<string>()) is not string path)
        {
            _meshes[hash] = null;
            return null;
        }
        _loading.Add(hash);
        System.Threading.Tasks.Task.Run(() =>
        {
            Mesh? loaded = null;
            try
            {
                loaded = ResourceLoader.Load<Mesh>(Engine_BnkMeshLoader.PathPrefix + path, "Mesh");
            }
            catch (Exception)
            {
                loaded = null;
            }
            _loaded.Enqueue((hash, loaded is not null && loaded.GetSurfaceCount() > 0 ? loaded : null));
        });
        return null;
    }

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
}
