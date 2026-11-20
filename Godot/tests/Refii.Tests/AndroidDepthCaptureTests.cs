using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The scene-depth capture runs where the renderer runs it: pre-transparent on Mobile.</summary>
[TestSuite]
public class AndroidDepthCaptureTests
{
    [TestCase]
    public void MobileCapturesBeforeTransparents()
    {
        AssertThat(AndroidDepthCapture.CallbackType("mobile")).IsEqual(CompositorEffect.EffectCallbackTypeEnum.PreTransparent);
    }

    [TestCase]
    public void ForwardPlusKeepsPostOpaque()
    {
        AssertThat(AndroidDepthCapture.CallbackType("forward_plus")).IsEqual(CompositorEffect.EffectCallbackTypeEnum.PostOpaque);
        AssertThat(AndroidDepthCapture.CallbackType("gl_compatibility")).IsEqual(CompositorEffect.EffectCallbackTypeEnum.PostOpaque);
    }
}
