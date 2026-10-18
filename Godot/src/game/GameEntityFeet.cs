using Godot;
using Refii.Level;

namespace Refii.Game;

public partial class GameEntities
{
    private readonly Engine_DynamicFootBones _heroFeet = new();

    public bool TryHeroFeet(out DynamicActorFeet feet)
    {
        feet = default;
        uint hero = _principals.Hero;
        return hero != 0 && IsInsideTree() && IsVisibleInTree() &&
            _nodes.TryGetValue(hero, out var node) && GodotObject.IsInstanceValid(node) && node.IsVisibleInTree() &&
            _skeletons.TryGetValue(hero, out var rig) && GodotObject.IsInstanceValid(rig.Node) &&
            _heroFeet.TryRead(rig.Node, node.GlobalPosition, out feet);
    }
}
