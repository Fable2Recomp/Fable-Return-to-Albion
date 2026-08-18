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

    private float _accumulator;

    private bool _bound;

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

    public void Bind()
    {
        if (_bound)
        {
            return;
        }

        _bound = true;
        _instances.Clear();

        foreach (string path in ModelPaths())
        {
            foreach (ClothAsset asset in Engine_PhysicsIndex.AssetsFor(path))
            {
                ClothInstance instance = ClothInstance.Create();
                instance.XformCur = Engine_PhysicsMatrix.FromTransform3D(GlobalTransform);
                instance.XformPrev = instance.XformCur;
                instance.Bind(asset);
                _instances.Add(instance);
            }
        }

        if (_instances.Count != 0)
        {
            GD.Print($"[physics] {Name}: {_instances.Count} cloth piece(s) bound");
        }
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

        while (_accumulator >= FixedStep)
        {
            _accumulator -= FixedStep;
            Step(FixedStep, world);
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
