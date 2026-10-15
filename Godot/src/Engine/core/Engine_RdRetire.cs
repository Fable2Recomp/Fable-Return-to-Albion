using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// Rendering-device textures replaced while a <see cref="Texture2Drd"/> still points at them,
/// freed only once the switch has landed and the frames that sampled them have finished.
/// </summary>
/// <remarks>
/// A resized target is re-pointed through a deferred call on the main thread, so for a frame or
/// more every material sampling the <c>Texture2Drd</c> still names the old texture. Freed at once,
/// that texture takes those materials' uniform sets with it: draws fail ("uniform_set is null"),
/// the renderer later frees the sets a second time ("Attempted to free invalid ID"), and a frame
/// already queued on the GPU reads a destroyed image — device lost, a crash to desktop entering
/// Brightwood as the impostor atlas grew (2026-10-16). Render thread only, like the callbacks
/// that own the textures.
/// </remarks>
public static class Engine_RdRetire
{
    /// <summary>Frames drawn before a retired texture is freed: the deferred re-point, then the GPU's queue.</summary>
    private const ulong Frames = 8;

    private static readonly List<(Rid Rid, ulong After)> Pending = new();

    /// <summary>Frees <paramref name="rid"/> once <see cref="Frames"/> more frames have been drawn.</summary>
    public static void Retire(Rid rid)
    {
        if (rid.IsValid)
        {
            Pending.Add((rid, (ulong)Engine.GetFramesDrawn() + Frames));
        }
    }

    /// <summary>Frees what has waited long enough, in the order retired. Call from a render callback.</summary>
    public static void Pump(RenderingDevice rd)
    {
        if (Pending.Count == 0)
        {
            return;
        }
        ulong now = (ulong)Engine.GetFramesDrawn();
        int kept = 0;
        for (int i = 0; i < Pending.Count; i++)
        {
            (Rid rid, ulong after) = Pending[i];
            if (now < after)
            {
                Pending[kept++] = Pending[i];
                continue;
            }
            if (rd.TextureIsValid(rid))
            {
                rd.FreeRid(rid);
            }
        }
        Pending.RemoveRange(kept, Pending.Count - kept);
    }
}
