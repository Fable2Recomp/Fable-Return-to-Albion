using Godot;
using System.Collections.Generic;
using Refii.Core;
using Refii.Hero;
using Refii.Host;

namespace Refii.Game;

public partial class GameCharacterMorphs : Node
{
    private int _seenGeneration = -1;
    private static int _published;
    public static int PreviewGeneration { get; private set; }

    public static void Preview(IReadOnlyDictionary<string, string>? values)
    {
        Engine_CharacterMorphs.Publish(key => values is null ? Engine_Switches.String(key, string.Empty) :
            values.TryGetValue(key, out string? value) ? value : string.Empty, HostBridge.SetModSetting, ++_published);
        ++PreviewGeneration;
    }

    public override void _Ready() => Publish();

    public override void _Process(double delta)
    {
        if (!GameMorphMirror.OwnsInput && _seenGeneration != Engine_Switches.Generation) Publish();
    }

    private void Publish()
    {
        _seenGeneration = Engine_Switches.Generation;
        Engine_CharacterMorphs.Publish(key => Engine_Switches.String(key, string.Empty),
            HostBridge.SetModSetting, ++_published);
    }
}
