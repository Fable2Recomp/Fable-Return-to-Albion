using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer in the Godot EDITOR: the per-frame drive the running game gets from
/// <c>GameSession</c>, for a level opened as a scene.
/// </summary>
/// <remarks>
/// A level built for the editor is saved and reopened, so its classic materials come back with
/// frozen copies of the frame-global textures and nothing pumps the constants. Placed in the
/// level's root by <see cref="Refii.Level.Engine_LevelScene"/> under the classic look, this node
/// does in the editor what the game does: adds the classic nodes, re-points every classic
/// material at the live textures, and pumps the path from the editor's camera each frame.
/// Outside the editor it does nothing: the game and the level viewer drive the same path
/// themselves.
/// </remarks>
[Tool]
public partial class Engine_ClassicEditorDriver : Node
{
    public const string NodeName = "ClassicEditorDriver";

    private bool _bound;

    public override void _Ready()
    {
        SetProcess(Engine.IsEditorHint());
    }

    public override void _Process(double delta)
    {
        if (!Engine.IsEditorHint() || !Engine_ClassicMaterial.Active || GetParent() is not Node3D level)
        {
            return;
        }
        Camera3D? camera = null;
#if TOOLS
        camera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
#endif
        Engine_ClassicPath.Ensure(this);
        Engine_ClassicEditorFrame.Update(level);
        if (!_bound && Engine_ClassicShadows.Instance is not null)
        {
            _bound = true;
            int bound = 0;
            foreach (Node node in level.FindChildren("*", "GeometryInstance3D", true, false))
            {
                bound += Rebind((GeometryInstance3D)node);
            }
            GD.Print($"[classic] editor: {bound} material(s) re-bound to the live frame textures");
        }
        Vector2 size = camera?.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280f, 720f);
        Engine_ClassicPath.Pump(this, camera, size);
    }

    private static int Rebind(GeometryInstance3D node)
    {
        int count = 0;
        void One(Material? material)
        {
            if (material is ShaderMaterial shader &&
                (shader.HasMeta(Engine_ClassicMaterial.ClassicMeta) || shader.HasMeta(Refii.Level.Engine_ClassicWater.Meta)))
            {
                Engine_ClassicMaterial.RebindShared(shader);
                ++count;
            }
        }
        One(node.MaterialOverride);
        if (node is MeshInstance3D mesh && mesh.Mesh is not null)
        {
            for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
            {
                One(mesh.GetSurfaceOverrideMaterial(i));
                One(mesh.Mesh.SurfaceGetMaterial(i));
            }
        }
        else if (node is MultiMeshInstance3D batch && batch.Multimesh?.Mesh is { } shared)
        {
            One(shared.SurfaceGetMaterial(0));
        }
        return count;
    }
}
