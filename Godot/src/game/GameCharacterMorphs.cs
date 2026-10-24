using Godot;
using Refii.Core;
using Refii.Hero;
using Refii.Host;

namespace Refii.Game;

public partial class GameCharacterMorphs : Node
{
    private int _seenGeneration = -1;
    private static int _published;

    public override void _Ready() => Publish();

    public override void _Process(double delta)
    {
        if (_seenGeneration != Engine_Switches.Generation) Publish();
    }

    private void Publish()
    {
        _seenGeneration = Engine_Switches.Generation;
        Engine_CharacterMorphs.Publish(key => Engine_Switches.String(key, string.Empty),
            HostBridge.SetModSetting, ++_published);
    }
}
