using Godot;
using System.Collections.Generic;
using Refii.Core;
using Refii.Hero;
using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Game;

public partial class GameCharacterMorphs : Node
{
    private int _seenGeneration = -1;
    private static int _published;
    public static int PreviewGeneration { get; private set; }
    public static float ManualTan { get; private set; } = 0.5f;

    public static void Preview(IReadOnlyDictionary<string, string>? values)
    {
        string Read(string key) => values is null ? Engine_Switches.String(key, string.Empty) :
            values.TryGetValue(key, out string? value) ? value : string.Empty;
        ManualTan = Engine_CharacterMorphs.ManualTan(Read);
        Engine_CharacterMorphs.Publish(Read, HostBridge.SetModSetting, ++_published);
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
        ManualTan = Engine_CharacterMorphs.ManualTan(key => Engine_Switches.String(key, string.Empty));
        Engine_CharacterMorphs.Publish(key => Engine_Switches.String(key, string.Empty),
            HostBridge.SetModSetting, ++_published);
    }
}
