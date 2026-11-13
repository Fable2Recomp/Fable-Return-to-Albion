using System.Runtime.CompilerServices;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// Skips the Environment writes that the lighting repeats every frame with the same values
/// (the SDFGI and SSAO constants). Godot's Environment setters carry no equality check: each
/// one crosses into the engine and queues a renderer update. Android only; elsewhere
/// <see cref="Unchanged"/> is always false and every write goes through as before.
/// </summary>
/// <remarks>
/// Only for properties that nothing but <c>Engine_LevelLighting.ConfigureEnvironment</c> writes
/// (the switches the GI refresh and the mist flip are not covered). The record is per
/// Environment, so a region's own Environment is written again the first time it is seen.
/// </remarks>
public static class AndroidEnvironmentWrites
{
    /// <summary>How many independent write groups an Environment records.</summary>
    public const int Groups = 4;

    /// <summary>The keys last written, per group.</summary>
    public sealed class Seen
    {
        public readonly float[] Key = new float[Groups];
        public readonly bool[] Has = new bool[Groups];
    }

    private static readonly ConditionalWeakTable<Godot.Environment, Seen> _seen = new();

    /// <summary>
    /// Whether <paramref name="group"/> of <paramref name="environment"/> was last written with
    /// <paramref name="key"/> (the inputs of that group's values). When it was not, the call
    /// records the key and answers false: the caller writes the group.
    /// </summary>
    public static bool Unchanged(Godot.Environment environment, int group, float key) =>
        Engine_Platform.IsAndroid && Record(_seen.GetOrCreateValue(environment), group, key);

    /// <summary>The recording step alone: true when the group already holds this key.</summary>
    public static bool Record(Seen s, int group, float key)
    {
        if (s.Has[group] && s.Key[group] == key)
        {
            return true;
        }

        s.Has[group] = true;
        s.Key[group] = key;
        return false;
    }
}
