using System.Collections.Generic;

namespace Refii.Launcher.Tools;

/// <summary>
/// Reference hero model parts used by character diagnostics and lighting lab tools.
/// </summary>
public static class CharacterLightLab
{
    public static readonly IReadOnlyList<string> Parts = new[]
    {
        "art/characters/hero/ch_heromale_morph_body",
        "art/characters/hero/ch_heromale_morph_thin",
        "art/characters/hero/ch_eye_adult_brown_l",
        "art/characters/hero/ch_eye_adult_brown_r",
    };
}
