using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// When the scene-depth capture (<see cref="Refii.Fx.Engine_FxDepthCaptureEffect"/>) runs on the
/// Mobile renderer.
/// </summary>
/// <remarks>
/// Mobile never runs a post-opaque compositor effect: render_forward_mobile.cpp warns "Post opaque
/// rendering effect callback is not supported in the mobile renderer" and skips it, because opaque
/// and sky share subpasses. So on a phone the capture never ran, `fx_scene_depth_valid` stayed 0,
/// and everything that tests against the scene's depth went untested: property signs and other
/// world icons showed through walls, as did the highlight outline and soft particles. Pre-transparent
/// sees the same opaque depth and Mobile supports it; it ends the merged opaque and transparent pass
/// on frames where it is attached. Forward+ keeps post-opaque.
/// </remarks>
public static class AndroidDepthCapture
{
    /// <summary>The callback type for a renderer (<c>RenderingServer.GetCurrentRenderingMethod()</c>).</summary>
    public static CompositorEffect.EffectCallbackTypeEnum CallbackType(string renderingMethod) =>
        renderingMethod == "mobile"
            ? CompositorEffect.EffectCallbackTypeEnum.PreTransparent
            : CompositorEffect.EffectCallbackTypeEnum.PostOpaque;
}
