using Godot;

namespace Refii.Models;

/// <summary>
/// The guest's live theme block (all 194 parameters, Engine_ClassicThemeParams names them) and
/// the sky object's sun and moon, read once a frame — the values its sky draws state, after the
/// guest's own time-of-day blending. Empty until the scene is up (and in a replayed lab frame).
/// </summary>
public static class Engine_ClassicTheme
{
    public const int Parameters = 194;
    private const int Floats = Parameters * 4 + 13;
    private static readonly float[] _values = new float[Floats];

    /// <summary>Whether this frame carried the block.</summary>
    public static bool Live { get; private set; }

    /// <summary>
    /// Main thread, once a frame. Where no guest runs (the editor, the lab) the block is the one
    /// <see cref="Engine_ClassicEditorFrame"/> evaluated from the level's own themes, with the
    /// sky's sun from that frame; the sky object's colour is not a theme term (its writer is not
    /// traced), so it stays black there, and no texture slots are stated.
    /// </summary>
    public static void Pump()
    {
        Live = Refii.Host.HostBridge.GetClassicTheme(_values) >= Floats;
        TexturesLive = Live && Refii.Host.HostBridge.GetClassicThemeTextures(_words) >= _words.Length;
        if (!Live && Engine_ClassicEditorFrame.HasTheme)
        {
            System.Array.Clear(_values);
            Engine_ClassicEditorFrame.Theme.CopyTo(_values);
            System.ReadOnlySpan<float> sky = Engine_ClassicEditorFrame.SkySun;
            _values[780] = sky[0];
            _values[781] = sky[1];
            _values[782] = sky[2];
            Live = true;
        }
    }

    /// <summary>The theme's texture slots (`sub_821D0C00`, T + 16 x (194 + slot)).</summary>
    public const int SlotToneMappingOverlay = 0, SlotSkyOverlay = 1, SlotSunBeams = 2, SlotSunDisc = 3,
        SlotStreaks = 4, SlotSunGlare = 5, SlotMoon = 6, SlotMoonGlare = 7, SlotCloudDensity0 = 8,
        SlotWaterNormal = 12, SlotWaterDetail = 13, SlotWaterHeight = 14;

    private const int Slots = 15;
    private const int SlotWords = 17;
    private static readonly uint[] _words = new uint[Slots * SlotWords];

    /// <summary>Whether this frame carried the texture slots.</summary>
    public static bool TexturesLive { get; private set; }

    /// <summary>
    /// A slot's <paramref name="into"/>.Length most-weighted textures, as `sub_821D0C00` picks
    /// them. With that many or fewer it copies them as they stand, unnormalised, and zeroes the
    /// rest. With more it keeps the heaviest, takes the weight of the vector's own element N (the
    /// UNSORTED one, +3108 + 8N) off the last kept when there are more than N + 1, and scales the
    /// kept weights to sum to one. Returns the count it reports.
    /// </summary>
    public static int Pick(int slot, System.Span<(float Weight, uint Hash)> into)
    {
        into.Clear();
        if (!TexturesLive || slot < 0 || slot >= Slots)
        {
            return 0;
        }

        int at = slot * SlotWords;
        int count = (int)_words[at];
        int copied = System.Math.Min(count, (SlotWords - 1) / 2);
        int n = into.Length;
        if (count <= n)
        {
            for (int i = 0; i < copied; i++)
            {
                into[i] = Element(at, i);
            }
            return count;
        }

        System.Span<(float Weight, uint Hash)> sorted = stackalloc (float, uint)[copied];
        for (int i = 0; i < copied; i++)
        {
            sorted[i] = Element(at, i);
        }
        System.MemoryExtensions.Sort(sorted, static (a, b) => b.Weight.CompareTo(a.Weight));
        for (int i = 0; i < n && i < copied; i++)
        {
            into[i] = sorted[i];
        }
        if (count > n + 1 && n < copied)
        {
            into[n - 1].Weight -= Element(at, n).Weight;
        }

        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            sum += into[i].Weight;
        }
        if (sum != 0f)
        {
            float scale = 1f / sum;
            for (int i = 0; i < n; i++)
            {
                into[i].Weight *= scale;
            }
        }
        return n;
    }

    /// <summary>The slot's single heaviest texture hash (`sub_821D0BA0`), or 0.</summary>
    public static uint TopHash(int slot)
    {
        System.Span<(float Weight, uint Hash)> one = stackalloc (float, uint)[1];
        Pick(slot, one);
        return one[0].Hash;
    }

    private static (float Weight, uint Hash) Element(int at, int i) =>
        (System.BitConverter.UInt32BitsToSingle(_words[at + 1 + i * 2]), _words[at + 2 + i * 2]);

    /// <summary>Lane <paramref name="lane"/> of parameter <paramref name="index"/>.</summary>
    public static float Param(int index, int lane = 0) => _values[index * 4 + lane];

    /// <summary>The sky object's sun colour (S+0) and direction (S+16).</summary>
    public static Vector3 SunColour => new(_values[776], _values[777], _values[778]);
    public static Vector3 SunDirection => new(_values[780], _values[781], _values[782]);

    /// <summary>The moon's direction (M+0) and phase (M+20, 0..7).</summary>
    public static Vector3 MoonDirection => new(_values[784], _values[785], _values[786]);
    public static int MoonPhase => (int)_values[788];
}
