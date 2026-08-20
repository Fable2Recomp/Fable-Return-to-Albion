using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Refii.Physics;

[Tool]
[GlobalClass]
public partial class Engine_ClothBody : Node3D
{
    public const string NodeName = "ClothBody";

    public const string ModelPathsMeta = "cloth_model_paths";

    public const float FixedStep = 1.0f / 60.0f;

    public const float MaxStepBacklog = 0.25f;

    private readonly List<ClothInstance> _instances = new();

    private readonly List<ClothPiece> _pieces = new();

    private ClothPatch? _patch;

    private MeshInstance3D? _target;

    private float _accumulator;

    private bool _bound;

    public static readonly PhysVec4 GravityDown =
        new(0.0f, ClothInstance.DefaultGravityZ, 0.0f, 0.0f);

    [Export]
    public bool Simulate { get; set; } = true;

    [Export]
    public float WindStrength { get; set; } = 1.0f;

    [Export]
    public Vector3 WindDirection { get; set; } = Vector3.Zero;

    [Export]
    public float GravityStrength { get; set; } = 1.0f;

    [Export]
    public float Damping { get; set; } = ClothInstance.DefaultDamping;

    public IReadOnlyList<ClothInstance> Instances => _instances;

    public int PieceCount => _instances.Count;

    public override void _Ready()
    {
        Bind();
        SetPhysicsProcess(true);
    }

    public MeshInstance3D? Target()
    {
        if (_target is not null && GodotObject.IsInstanceValid(_target))
        {
            return _target;
        }

        _target = GetParent() as MeshInstance3D;
        return _target;
    }

    public void Bind()
    {
        if (_bound)
        {
            return;
        }

        _bound = true;
        _instances.Clear();
        _pieces.Clear();
        _patch = null;

        MeshInstance3D? target = Target();
        if (target?.Mesh is not ArrayMesh source)
        {
            return;
        }

        ClothPatch? patch = ClothPatch.Capture(source);
        if (patch is null)
        {
            return;
        }

        {
            foreach (ClothPiece piece in Engine_ClothPieces.ForMesh(
                         ModelPaths(), source, Refii.Level.Engine_PropStreamer.Resolve))
            {
                if (piece.ClothVertexOf.Length != patch.VertexCount)
                {
                    continue;
                }

                ClothInstance instance = ClothInstance.Create();
                instance.GravityDirWorld = GravityDown;
                instance.XformCur = Engine_PhysicsMatrix.FromTransform3D(GlobalTransform);
                instance.XformPrev = instance.XformCur;
                instance.Bind(piece.Asset);

                patch.MarkDriven(piece.ClothVertexOf);
                _pieces.Add(piece);
                _instances.Add(instance);
            }
        }

        if (_instances.Count == 0)
        {
            return;
        }

        _patch = patch;
        patch.RecomputeNormals();
        patch.Rebuild();
        target.Mesh = patch.Built;

        GD.Print($"[physics] {Name}: {_instances.Count} cloth piece(s) bound, driving " +
            $"{patch.DrivenSurfaceCount()} surface(s) of {patch.VertexCount} vertices");
    }

    public void Publish()
    {
        if (_patch is null)
        {
            return;
        }

        for (int i = 0; i < _pieces.Count && i < _instances.Count; i++)
        {
            _patch.Apply(_pieces[i], _instances[i].PosCur);
        }

        _patch.RecomputeNormals();
        _patch.Rebuild();
    }

    public string[] ModelPaths() =>
        HasMeta(ModelPathsMeta)
            ? GetMeta(ModelPathsMeta).AsStringArray()
            : System.Array.Empty<string>();

    public void SetModelPaths(IReadOnlyList<string> paths)
    {
        var stored = new Array<string>();
        foreach (string path in paths)
        {
            stored.Add(path);
        }

        SetMeta(ModelPathsMeta, stored);
        _bound = false;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Simulate || _instances.Count == 0)
        {
            return;
        }

        _accumulator += (float)delta;
        if (_accumulator > MaxStepBacklog)
        {
            _accumulator = MaxStepBacklog;
        }

        PhysMat4 world = Engine_PhysicsMatrix.FromTransform3D(GlobalTransform);

        bool stepped = false;
        while (_accumulator >= FixedStep)
        {
            _accumulator -= FixedStep;
            Step(FixedStep, world);
            stepped = true;
        }

        if (stepped)
        {
            Publish();
        }
    }

    public void Step(float dt, in PhysMat4 world)
    {
        foreach (ClothInstance instance in _instances)
        {
            instance.XformPrev = instance.XformCur;
            instance.XformCur = world;

            instance.Damping = Damping;
            instance.WindStrength = WindStrength;
            instance.GravityStrength = GravityStrength;
            instance.WindDirWorld = PhysVec4.FromVector3(WindDirection, 0.0f);
            instance.WindEnabled = WindDirection.LengthSquared() > 0.0f;

            Engine_ClothSolver.TransformCollidersToWorld(instance);
            Engine_ClothSolver.SimulateInstance(instance, dt);
        }
    }

    public void ResetToSkinned()
    {
        foreach (ClothInstance instance in _instances)
        {
            instance.ResetToSkinned();
        }

        _patch?.ResetToRest();
        Publish();
    }

    public override Array<Dictionary> _GetPropertyList()
    {
        var list = new Array<Dictionary>();

        if (_instances.Count == 0)
        {
            return list;
        }

        Group(list, "Cloth");
        for (int i = 0; i < _instances.Count; i++)
        {
            Row(list, "Piece " + (i + 1));
        }

        return list;
    }

    public override Variant _Get(StringName property)
    {
        string name = property.ToString();

        if (!name.StartsWith("Piece ", System.StringComparison.Ordinal) ||
            !int.TryParse(name["Piece ".Length..], out int index) ||
            index < 1 || index > _instances.Count)
        {
            return default;
        }

        ClothInstance instance = _instances[index - 1];
        ClothAsset? asset = instance.Asset;
        if (asset is null)
        {
            return default;
        }

        return $"{asset.VertexCount} verts, {asset.EdgeCount} constraints, " +
            $"sim [{asset.SimBegin}..{asset.SimEnd}), " +
            $"{instance.RelaxIterations} iter, residual {instance.Residual:0.####}";
    }

    private static void Group(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.Nil },
            { "usage", (int)PropertyUsageFlags.Group },
            { "hint_string", string.Empty },
        });

    private static void Row(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.String },
            { "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
        });
}
