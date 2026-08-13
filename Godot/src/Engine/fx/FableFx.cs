using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;
using Refii.Textures;

namespace Refii.Fx;

[Tool]
[GlobalClass]
public partial class FableFx : Node3D
{
    public const string BankPath = "art/particles/particle_bank.bnk";

    public const string MixShaderPath = "res://src/Engine/fx/fx_particle.gdshader";

    public const string AddShaderPath = "res://src/Engine/fx/fx_particle_add.gdshader";

    public const string TextureFolder = "art/fx/pfx_textures/";

    [Export]
    public float ExposureScale { get; set; } = 1.0f;

    private static FxBank? _bank;
    private static bool _bankTried;

    private readonly Engine_FxSystem _system = new();
    private readonly Dictionary<string, VisualBatchMaterial> _materials = new();
    private readonly Dictionary<string, Engine_FxSystem.MeshInstanceData> _meshData = new();
    private readonly Dictionary<string, MultiMeshInstance3D> _meshNodes = new();

    private MeshInstance3D? _surface;
    private ImmediateMesh? _mesh;
    private bool _built;
    private string _builtName = string.Empty;

    private string _effectName = string.Empty;

    [Export]
    public string EffectName
    {
        get => _effectName;
        set
        {
            _effectName = value ?? string.Empty;
            _built = false;
        }
    }

    [Export]
    public bool Loop { get; set; } = true;

    [Export]
    public bool Playing { get; set; } = true;

    [Export]
    public float EffectScale { get; set; } = 1f;

    private static ulong _bankRetryAtMsec;

    public static FxBank? SharedBank()
    {
        if (_bank is not null)
        {
            return _bank;
        }

        ulong now = Time.GetTicksMsec();
        if (_bankTried && now < _bankRetryAtMsec)
        {
            return null;
        }

        _bankTried = true;
        _bankRetryAtMsec = now + 2000;
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(BankPath) ??
                        Engine_BnkMeshLoader.ReadBytes("data/" + BankPath);
        if (bytes is null)
        {
            string root = Refii.Core.Engine_GameData.Resolve();
            if (root.Length != 0)
            {
                foreach (string candidate in new[]
                         {
                             System.IO.Path.Combine(
                                 root, BankPath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                             System.IO.Path.Combine(
                                 root, "data",
                                 BankPath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                         })
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        bytes = System.IO.File.ReadAllBytes(candidate);
                        break;
                    }
                }
            }
        }

        if (bytes is null)
        {
            GD.Print($"[fx] particle bank not found at {BankPath}");
            return null;
        }

        var bank = new FxBank();
        if (!Engine_FxBank.Parse(bytes, bank))
        {
            GD.Print($"[fx] particle bank parse failed: {bank.Error}");
            return null;
        }

        GD.Print(
            $"[fx] bank loaded: {bank.Effects.Count} effects, " +
            $"{bank.Emitters.Count} emitters, {bank.Systems.Count} systems");
        _bank = bank;
        return bank;
    }

    public override void _Ready()
    {
        EnsureBuilt();
    }

    public override void _Process(double delta)
    {
        if (!Playing)
        {
            return;
        }

        EnsureBuilt();
        if (!_built || _mesh is null || _system.IsEmpty)
        {
            return;
        }

        _system.SetLoop(Loop);
        _system.Update((float)delta);

        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera is null && Engine.IsEditorHint())
        {
            camera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }

        if (camera is null)
        {
            return;
        }

        Transform3D local = GlobalTransform.AffineInverse() * camera.GlobalTransform;
        Vector3 cameraPos = local.Origin;
        Vector3 cameraRight = local.Basis.X.Normalized();
        Vector3 cameraUp = local.Basis.Y.Normalized();

        _system.EmitGeometry(
            _mesh, LookupMaterial, cameraPos, cameraRight, cameraUp, _meshData);

        foreach ((string path, Engine_FxSystem.MeshInstanceData data) in _meshData)
        {
            if (!_meshNodes.TryGetValue(path, out MultiMeshInstance3D? node))
            {
                node = BuildMeshNode(path);
                if (node is null)
                {
                    continue;
                }

                _meshNodes[path] = node;
                AddChild(node);
            }

            if (node.Multimesh is not MultiMesh multi)
            {
                continue;
            }

            int count = data.Instances.Count;
            if (multi.InstanceCount < count)
            {
                multi.InstanceCount = Math.Max(count, 16);
            }

            multi.VisibleInstanceCount = count;
            for (int i = 0; i < count; i++)
            {
                (Transform3D transform, Color colour) = data.Instances[i];
                multi.SetInstanceTransform(i, transform);
                multi.SetInstanceColor(i, colour);
            }
        }
    }

    public void Restart()
    {
        _built = false;
        EnsureBuilt();
    }

    private void EnsureBuilt()
    {
        if (_built && _builtName == _effectName)
        {
            return;
        }

        FxBank? bank = SharedBank();
        if (bank is null || _effectName.Length == 0)
        {
            return;
        }

        foreach (MultiMeshInstance3D node in _meshNodes.Values)
        {
            node.QueueFree();
        }

        _meshNodes.Clear();
        _meshData.Clear();

        var placement = new FxPlacement { Scale = EffectScale };
        if (Engine_BnkFxLoader.TryParseHashToken(_effectName, out uint hash))
        {
            placement.EffectHash = hash;
        }
        else
        {
            placement.EffectName = _effectName;
        }

        _system.Build(bank, new[] { placement });

        if (_surface is null)
        {
            _mesh = new ImmediateMesh();
            _surface = new MeshInstance3D
            {
                Name = "FxSurface",
                Mesh = _mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 512f,
            };
            AddChild(_surface);
        }

        _built = true;
        _builtName = _effectName;

        if (!placement.Resolved)
        {
            GD.Print($"[fx] effect '{_effectName}' not found in bank");
        }
    }

    private readonly HashSet<string> _materialsMissingTexture = new();
    private ulong _textureRetryAtMsec;

    private VisualBatchMaterial? LookupMaterial(string key)
    {
        if (_materials.TryGetValue(key, out VisualBatchMaterial? cached))
        {
            if (_materialsMissingTexture.Contains(key) &&
                Time.GetTicksMsec() >= _textureRetryAtMsec)
            {
                _textureRetryAtMsec = Time.GetTicksMsec() + 2000;
                int retrySplit = key.LastIndexOf('|');
                Texture2D? late = LoadTexture(retrySplit >= 0 ? key[..retrySplit] : key);
                if (late is not null && cached.Material is ShaderMaterial sm)
                {
                    sm.SetShaderParameter("fx_tex", late);
                    _materialsMissingTexture.Remove(key);
                }
            }

            return cached;
        }

        int split = key.LastIndexOf('|');
        string texture = split >= 0 ? key[..split] : key;
        string mode = split >= 0 ? key[(split + 1)..] : string.Empty;
        bool additive = mode is "add" or "addone";

        Shader? shader = GD.Load<Shader>(additive ? AddShaderPath : MixShaderPath);
        if (shader is null)
        {
            return null;
        }

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter(
            "fx_exposure", Engine_FxSystem.Exposure * ExposureScale);

        if (additive)
        {
            material.SetShaderParameter("fx_src_alpha", mode == "add" ? 1.0f : 0.0f);
        }

        Texture2D? tex = LoadTexture(texture);
        if (tex is not null)
        {
            material.SetShaderParameter("fx_tex", tex);
        }
        else
        {
            _materialsMissingTexture.Add(key);
        }

        var batch = new VisualBatchMaterial { Material = material };
        _materials[key] = batch;
        return batch;
    }

    private static Texture2D? LoadTexture(string name)
    {
        string leaf = name.Replace('\\', '/');
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        foreach (string candidate in new[]
                 {
                     TextureFolder + leaf,
                     name.Replace('\\', '/'),
                 })
        {
            byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(candidate) ??
                            Engine_BnkMeshLoader.ReadBytes("data/" + candidate);
            if (bytes is null)
            {
                continue;
            }

            Texture2D? texture = Engine_TexImage.TryCreateTexture(bytes, out _);
            if (texture is not null)
            {
                return texture;
            }
        }

        GD.Print($"[fx] texture '{name}' unresolved");
        return null;
    }

    private static MultiMeshInstance3D? BuildMeshNode(string meshPath)
    {
        string virtualPath = meshPath.Replace('\\', '/');
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath) ??
                        Engine_BnkMeshLoader.ReadBytes("data/" + virtualPath);
        if (bytes is null)
        {
            GD.Print($"[fx] mesh '{meshPath}' unresolved");
            return null;
        }

        ArrayMesh? mesh = Engine_MdlMesh.TryCreateMesh(
            bytes, virtualPath, out string error, out _);
        if (mesh is null)
        {
            GD.Print($"[fx] mesh '{meshPath}' failed: {error}");
            return null;
        }

        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = 16,
            VisibleInstanceCount = 0,
        };

        return new MultiMeshInstance3D
        {
            Name = "FxMesh_" + System.IO.Path.GetFileNameWithoutExtension(virtualPath),
            Multimesh = multi,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 512f,
        };
    }
}
