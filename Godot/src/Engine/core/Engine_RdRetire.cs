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

    /// <summary>The in-game Tracy capture's pool for RenderingDevice textures we create and free.</summary>
    public const string TracedPool = "render targets";

    private static readonly List<(Rid Rid, ulong After)> Pending = new();

    /// <summary>Frees <paramref name="rid"/> once <see cref="Frames"/> more frames have been drawn.</summary>
    public static void Retire(Rid rid)
    {
        if (rid.IsValid)
        {
            Engine_TracedMemory.Free(TracedPool, rid.Id);
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

    /// <summary>
    /// Runs <paramref name="release"/> where the device's frees are legal: at once on the render
    /// thread, otherwise queued behind everything already sent to it.
    /// </summary>
    /// <remarks>
    /// `RenderingDevice.free_rid` refuses every thread but the render thread
    /// (`ERR_RENDER_THREAD_GUARD`). A CompositorEffect's predelete runs on whichever thread drops
    /// its last reference, normally the main thread: with `thread_model = 2` its frees did
    /// nothing but print an error, and the objects leaked (so did a predelete from the
    /// finalizer thread under Safe). Queued, the release also runs after an `Initialise` still
    /// waiting on the queue, so it frees what that made.
    ///
    /// THE CALLABLE WRAPS A CLOSURE, NOT THE EFFECT'S METHOD: a Callable whose target is the
    /// GodotObject being deleted is dropped as a call on a freed instance. The release must touch
    /// only managed fields, never the dying object's Godot API, and must ask for the device
    /// again (null once it is torn down). Nothing waits for it: a predelete at shutdown can come
    /// after the render thread has exited, and a sync would never return.
    /// </remarks>
    public static void OnRenderThread(System.Action release)
    {
        if (RenderingServer.IsOnRenderThread())
        {
            release();
            return;
        }
        RenderingServer.CallOnRenderThread(Callable.From(() => release()));
    }
}
