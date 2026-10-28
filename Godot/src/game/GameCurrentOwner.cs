using System;

namespace Refii.Game;

/// <summary>Retires a main-thread current owner by reference identity without disposing its borrowed resources.</summary>
/// <remarks>Godot object equality can involve native instance state after deletion. Reference
/// identity also prevents a delayed exit from clearing the replacement owner. This policy does
/// not cancel worker leases or promise immediate collection of otherwise referenced objects.</remarks>
internal static class GameCurrentOwner
{
    /// <summary>Clears the current reference only if it still names the departing owner.</summary>
    /// <typeparam name="T">The managed owner type; no Godot native API is required.</typeparam>
    /// <param name="current">Main-thread reference that published the live owner.</param>
    /// <param name="departing">The nonnull owner whose lifetime has ended.</param>
    /// <returns>True if this call released the current reference; false for a stale or repeated exit.</returns>
    internal static bool Retire<T>(ref T? current, T departing) where T : class
    {
        ArgumentNullException.ThrowIfNull(departing);
        if (!ReferenceEquals(current, departing)) return false;
        current = null;
        return true;
    }
}
