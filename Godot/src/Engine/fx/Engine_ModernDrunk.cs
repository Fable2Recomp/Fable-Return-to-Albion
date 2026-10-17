using Godot;

namespace Refii.Fx;

/// <summary>
/// The guest's displacement quads (the drunk warp, <see cref="Refii.Host.HostBridge.GetDisplacementQuads"/>)
/// for the modern renderer: each a textured <see cref="Polygon2D"/> in the displacement layer's own
/// viewport (<see cref="Engine_FxDisplacement"/>), added onto what the layer's meshes write.
/// </summary>
/// <remarks>
/// The title draws each quad from four pixel-space corners turned about their centre by its
/// rotation, uv per corner, into its displacement target with ONE / ONE (Engine_ClassicDisplacement).
/// Here the corners scale from the guest's screen to the layer's viewport; the texture is the
/// title's FXT_Fullscreen_Drunk, read raw, whose bytes are the offsets.
/// </remarks>
public static class Engine_ModernDrunk
{
    private const string DrunkTexture = "Art/GUI/HUD/FXT_Fullscreen_Drunk.tex";
    private const string ShaderPath = "res://src/Engine/fx/fx_displacement_quad.gdshader";
    private const int MaxQuads = 4;

    private static readonly float[] _quads = new float[1 + MaxQuads * 16];
    private static readonly Polygon2D?[] _nodes = new Polygon2D?[MaxQuads];
    private static readonly Vector2[] _corners = new Vector2[4];
    private static Texture2D? _texture;
    private static ShaderMaterial? _material;
    private static bool _tried;

    /// <summary>Quads drawn at the last pump.</summary>
    public static int Drawn { get; private set; }

    /// <summary>Follows the guest's quads. Main thread, once a frame.</summary>
    public static void Pump(Node context)
    {
        uint got = Refii.Host.HostBridge.GetDisplacementQuads(_quads);
        int count = got >= 1 ? System.Math.Min((int)_quads[0], MaxQuads) : 0;
        if (count > 0)
        {
            Engine_FxDisplacement.Ensure(context);
        }
        SubViewport? target = Engine_FxDisplacement.Current?.Target;
        if (count > 0 && (target is null || !Ensure()))
        {
            count = 0;
        }
        int drawn = 0;
        for (int i = 0; i < MaxQuads; i++)
        {
            Polygon2D? node = _nodes[i];
            if (node is not null && !GodotObject.IsInstanceValid(node))
            {
                node = _nodes[i] = null;
            }
            int at = 1 + i * 16;
            float w = _quads[at + 14], h = _quads[at + 15];
            if (i >= count || w <= 0f || h <= 0f)
            {
                if (node is not null)
                {
                    node.Visible = false;
                }
                continue;
            }
            if (node is null)
            {
                node = new Polygon2D { Name = $"DrunkQuad{i}", Texture = _texture, Material = _material };
                target!.AddChild(node);
                _nodes[i] = node;
            }
            Vector2 scale = new Vector2(target!.Size.X / w, target.Size.Y / h);
            float x0 = _quads[at], y0 = _quads[at + 1], x1 = _quads[at + 2], y1 = _quads[at + 3];
            float angle = _quads[at + 5];
            _corners[0] = new Vector2(x0, y0);
            _corners[1] = new Vector2(x0, y1);
            _corners[2] = new Vector2(x1, y1);
            _corners[3] = new Vector2(x1, y0);
            var centre = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            var points = new Vector2[4];
            var uv = new Vector2[4];
            Vector2 texels = _texture!.GetSize();
            for (int k = 0; k < 4; k++)
            {
                Vector2 d = _corners[k] - centre;
                Vector2 p = angle != 0f ? centre + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos) : _corners[k];
                points[k] = p * scale;
                uv[k] = new Vector2(_quads[at + 6 + k * 2], _quads[at + 7 + k * 2]) * texels;
            }
            node.Polygon = points;
            node.UV = uv;
            node.Visible = true;
            ++drawn;
        }
        if (drawn > 0)
        {
            Engine_FxDisplacement.NotifyActive();
        }
        Drawn = drawn;
    }

    private static bool Ensure()
    {
        if (_material is not null)
        {
            return true;
        }
        if (_tried)
        {
            return false;
        }
        _tried = true;
        _texture = Refii.Textures.Engine_TexLibrary.Get(DrunkTexture, Refii.Textures.TexSemantic.Unknown,
            Refii.Models.Engine_BnkMeshLoader.ReadBytes, out _);
        if (_texture is null)
        {
            GD.Print("[fx] the drunk displacement texture did not load; the warp stays off");
            return false;
        }
        _material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        return true;
    }
}
