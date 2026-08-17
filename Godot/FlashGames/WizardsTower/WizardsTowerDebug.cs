using Godot;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public sealed partial class WizardsTower
{
    public static Framework.Audio? Sound { get; private set; }

    public static IPubRandom? Chance { get; private set; }

    public static IPubClip? Root { get; private set; }

    public static bool DebugTrace { get; set; }

    public static void DebugToScreen(string? message)
    {
        if (DebugTrace && message is not null)
        {
            GD.Print($"[wizardstower] {message}");
        }
    }
}
