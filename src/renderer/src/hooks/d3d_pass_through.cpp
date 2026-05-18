#include "renderer_hooks_private.h"

#define REFII_RENDERER_PASSTHROUGH_HOOK_AS(wrapper_symbol, imp_symbol, kind) \
  REX_EXTERN(wrapper_symbol) {                                               \
    REX_FUNC_PROLOGUE();                                                     \
    const refii::renderer::RendererHookArgs args =                           \
        refii::renderer::CaptureRendererHookArgs(ctx);                       \
    refii::renderer::OnRendererGuestHookEnter(#wrapper_symbol, kind, args,   \
                                              base);                         \
    if (refii::renderer::ShouldRunNativeRendererHook(#wrapper_symbol,        \
                                                     kind)) {                \
      __imp__##imp_symbol(ctx, base);                                        \
      refii::renderer::OnRendererGuestHookExit(#wrapper_symbol, kind, args,  \
                                               ctx, base);                   \
    } else {                                                                 \
      refii::renderer::OnRendererGuestHookNativeSkipped(                     \
          #wrapper_symbol, kind, args, ctx, base);                           \
    }                                                                        \
  }

#define REFII_RENDERER_GENERAL_HOOK(symbol)      \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(            \
      symbol, symbol,                             \
      refii::renderer::RendererHookKind::kGeneral)

#define REFII_RENDERER_RESOURCE_HOOK(symbol)     \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(            \
      symbol, symbol,                             \
      refii::renderer::RendererHookKind::kResource)

#define REFII_RENDERER_DRAW_HOOK(symbol)         \
  REFII_RENDERER_PASSTHROUGH_HOOK_AS(            \
      symbol, symbol,                             \
      refii::renderer::RendererHookKind::kDraw)

REFII_RENDERER_RESOURCE_HOOK(sub_82B98930) // Mapped: LockTextureRect
REFII_RENDERER_RESOURCE_HOOK(sub_82B98950) // Mapped: UnlockTextureRect
REFII_RENDERER_RESOURCE_HOOK(sub_8225F980) // Lumen: D3DVertexBuffer_Lock
REFII_RENDERER_RESOURCE_HOOK(sub_82B9EEC0) // Mapped: UnlockVertexBuffer
REFII_RENDERER_RESOURCE_HOOK(sub_82196980) // Lumen: D3DIndexBuffer_Lock
REFII_RENDERER_RESOURCE_HOOK(sub_82B9EED0) // Mapped: UnlockIndexBuffer
REFII_RENDERER_RESOURCE_HOOK(sub_821FC178) // Lumen: D3DResource_AddRef
REFII_RENDERER_RESOURCE_HOOK(sub_821FC320) // Lumen: D3DResource_Release
REFII_RENDERER_RESOURCE_HOOK(sub_821FA070) // Lumen: D3DSurface_GetDesc
REFII_RENDERER_RESOURCE_HOOK(sub_82B98C88) // Lumen: D3DSurface_GetContainer
REFII_RENDERER_RESOURCE_HOOK(sub_821FA6C0) // Lumen: D3D::FindSurfaceWithinTexture
REFII_RENDERER_RESOURCE_HOOK(sub_821FAB38) // Lumen: D3D::GetTextureDimensions
REFII_RENDERER_RESOURCE_HOOK(sub_821FABD0) // Lumen: D3D::Get2DSurfaceDesc
REFII_RENDERER_RESOURCE_HOOK(sub_82B985C8) // Lumen: D3D::Get3DSurfaceDesc
REFII_RENDERER_RESOURCE_HOOK(sub_821FAD60) // Lumen: D3D::GetLevelSize
REFII_RENDERER_RESOURCE_HOOK(sub_82D160D8) // Lumen: D3DXLoadSurfaceFromSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82D21C68) // Lumen: D3DXTex::InternalCopy(D3DSurface*,D3DSurface*)
REFII_RENDERER_RESOURCE_HOOK(sub_82D14B20) // Lumen: D3DXLoadSurfaceFromMemory
REFII_RENDERER_RESOURCE_HOOK(sub_82B98B28) // Lumen: D3DDevice_CreateTexture
REFII_RENDERER_RESOURCE_HOOK(sub_82B98978) // Lumen: D3DCubeTexture_GetCubeMapSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82B98A58) // Lumen: D3DVolumeTexture_GetVolumeLevel
REFII_RENDERER_RESOURCE_HOOK(sub_82B98860) // Lumen: D3DTexture_GetSurfaceLevel
REFII_RENDERER_RESOURCE_HOOK(sub_821FC8D8) // IDA: Fable surface allocation wrapper
REFII_RENDERER_RESOURCE_HOOK(sub_82295380) // IDA: Fable tiled surface allocation wrapper
REFII_RENDERER_RESOURCE_HOOK(sub_82B8AB30) // IDA: Fable render/depth surface object ctor
REFII_RENDERER_RESOURCE_HOOK(rex_lhTexture_Create) // Lumen: D3DDevice_CreateSurface
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_SetupRenderTargets)
REFII_RENDERER_RESOURCE_HOOK(sub_82A4E8D0) // Fable Bink video plane surface setup
REFII_RENDERER_RESOURCE_HOOK(sub_82B6F040) // IDA: Fable presentation/depth scratch setup
REFII_RENDERER_RESOURCE_HOOK(sub_82B6F5C8) // IDA: Fable presentation/depth scratch teardown
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_CreateVertexBuffer)
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_CreateIndexBuffer)
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_LockBuffer)  // Lumen: D3D::LockResource
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_FlushBuffer) // Lumen: D3D::UnlockResource
REFII_RENDERER_DRAW_HOOK(sub_82196628) // Lumen: D3DDevice_BeginExport
REFII_RENDERER_DRAW_HOOK(sub_82196750) // Lumen: D3DDevice_EndExport
REFII_RENDERER_DRAW_HOOK(sub_822C6BA8) // Lumen: D3D::InvalidateCaches
REFII_RENDERER_DRAW_HOOK(sub_8219CD68) // Lumen: D3DDevice_SetShaderGPRAllocation
REFII_RENDERER_DRAW_HOOK(sub_821EC798) // Lumen: D3DDevice_SetSamplerState_MipMapLodBias
REFII_RENDERER_DRAW_HOOK(sub_82202D10) // IDA: D3D sampler callback AddressU
REFII_RENDERER_DRAW_HOOK(sub_8222C568) // IDA: D3D sampler callback AddressV
REFII_RENDERER_DRAW_HOOK(sub_822373F0) // IDA: D3D sampler callback AddressW
REFII_RENDERER_DRAW_HOOK(sub_822383A0) // IDA: D3D sampler callback MagFilter
REFII_RENDERER_DRAW_HOOK(sub_82238128) // IDA: D3D sampler callback MinFilter
REFII_RENDERER_DRAW_HOOK(sub_8222C818) // IDA: D3D sampler callback MipFilter
REFII_RENDERER_DRAW_HOOK(sub_822398D0) // IDA: D3D sampler callback filter quality
REFII_RENDERER_DRAW_HOOK(sub_8226B078) // IDA: Fable depth-surface bind helper
REFII_RENDERER_DRAW_HOOK(sub_8226E180) // IDA: Fable scene state flush/update path
REFII_RENDERER_DRAW_HOOK(sub_8226E738) // IDA: Fable scene draw submission path
REFII_RENDERER_DRAW_HOOK(sub_82EF2A90) // Lumen: D3DDevice::BeginScene
REFII_RENDERER_DRAW_HOOK(sub_82205F68) // Lumen: D3DDevice_BeginVertices
REFII_RENDERER_DRAW_HOOK(sub_82217DB8) // Lumen: D3DDevice_BeginIndexedVertices
REFII_RENDERER_DRAW_HOOK(sub_82206888) // IDA: private texture/video rectangle draw
REFII_RENDERER_DRAW_HOOK(sub_8221AE18) // Lumen: D3D::SetPending_HiZEnable
REFII_RENDERER_DRAW_HOOK(sub_8221B010) // Lumen: D3D::SetPending_Shaders
REFII_RENDERER_DRAW_HOOK(sub_8221C7D8) // Lumen: D3D::SetPending_RenderStates
REFII_RENDERER_DRAW_HOOK(sub_8221D1B0) // Lumen: D3D::SetPending_FetchConstants
REFII_RENDERER_DRAW_HOOK(sub_822154B0) // Lumen: D3D::SetPending_Predicated
REFII_RENDERER_DRAW_HOOK(sub_822655F0) // Lumen: D3DDevice_SetPredication
REFII_RENDERER_DRAW_HOOK(sub_822AF568) // Lumen: D3DDevice_SetScreenExtentQueryMode
REFII_RENDERER_GENERAL_HOOK(sub_8222BFA0) // Lumen: D3D::SetLiteralShaderConstants
REFII_RENDERER_GENERAL_HOOK(sub_82BA76F8) // Lumen: D3D::PM4LoadShaderConstantsImmediate
REFII_RENDERER_GENERAL_HOOK(sub_82BA77D0) // Lumen: D3D::PM4SetTextureFetchConstant
REFII_RENDERER_RESOURCE_HOOK(sub_82A42FA0) // Fable Bink video plane surface setup
REFII_RENDERER_GENERAL_HOOK(sub_832BCB30) // Bink frame decode/update
REFII_RENDERER_GENERAL_HOOK(sub_832BCDE8) // Bink frame decode/update
REFII_RENDERER_GENERAL_HOOK(sub_832C49E8) // Bink ExpandBink frame decode
REFII_RENDERER_GENERAL_HOOK(sub_8221A880) // IDA: D3DDevice_SetVertexShaderConstantF
REFII_RENDERER_GENERAL_HOOK(sub_82214E30) // IDA: D3DDevice_SetPixelShaderConstantF
REFII_RENDERER_GENERAL_HOOK(sub_821C6708) // Lumen: D3DDevice_SetVertexShaderConstantB
REFII_RENDERER_GENERAL_HOOK(sub_82B991B0) // Lumen: D3DDevice_SetVertexShaderConstantI
REFII_RENDERER_GENERAL_HOOK(sub_8226E120) // Lumen: D3DDevice_SetPixelShaderConstantB
REFII_RENDERER_GENERAL_HOOK(sub_82B99208) // Lumen: D3DDevice_SetPixelShaderConstantI
REX_EXTERN(sub_8221E550) {
  REX_FUNC_PROLOGUE();
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgs(ctx);
  refii::renderer::OnRendererStateDispatchHook(args, base);
  if (refii::renderer::ShouldRunNativeRendererHook(
          "sub_8221E550", refii::renderer::RendererHookKind::kDraw)) {
    __imp__sub_8221E550(ctx, base);
    refii::renderer::OnRendererGuestHookExit(
        "sub_8221E550", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base);
  } else {
    refii::renderer::OnRendererGuestHookNativeSkipped(
        "sub_8221E550", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base);
  }
}
REFII_RENDERER_DRAW_HOOK(sub_8221F2E0) // IDA: flush pending per-context GPU state
REFII_RENDERER_DRAW_HOOK(sub_82BA25C0) // Lumen: D3D::CDevice::AddCommandsToPrimaryBuffer
REFII_RENDERER_GENERAL_HOOK(sub_821F5028) // IDA: inline Fable RT descriptor builder
REFII_RENDERER_GENERAL_HOOK(sub_821FB650) // IDA: inline Fable texture/fetch descriptor builder
REFII_RENDERER_GENERAL_HOOK(sub_821FB968) // IDA: XGRAPHICS::SetBaseTextureHeader
REFII_RENDERER_RESOURCE_HOOK(sub_821FBB78) // Lumen: XGSetTextureHeader
REFII_RENDERER_RESOURCE_HOOK(rex_XGOffsetBaseTextureAddress) // Lumen: XGOffsetBaseTextureAddress
REFII_RENDERER_RESOURCE_HOOK(sub_8300BD38) // Lumen: XGSetVertexBufferHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BDD0) // Lumen: XGSetIndexBufferHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BE60) // Lumen: XGSetConstantBufferHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BEE0) // Lumen: XGSetLineTextureHeader
REFII_RENDERER_RESOURCE_HOOK(sub_8300BF48) // Lumen: XGSetCubeTextureHeader
REFII_RENDERER_GENERAL_HOOK(sub_821F52A0) // Lumen: XGSurfaceSize
REFII_RENDERER_RESOURCE_HOOK(sub_8300C290) // Lumen: XGRAPHICS__TileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CB40) // Lumen: j_XGRAPHICS__TileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CB48) // Lumen: XGTileTextureLevel
REFII_RENDERER_RESOURCE_HOOK(sub_83010E60) // Lumen: XGRAPHICS__TileVolume
REFII_RENDERER_RESOURCE_HOOK(sub_830117B8) // Lumen: XGTileVolumeTextureLevel
REFII_RENDERER_RESOURCE_HOOK(sub_8300C660) // Lumen: XGRAPHICS__UntileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CDA0) // Lumen: j_XGRAPHICS__UntileSurface
REFII_RENDERER_RESOURCE_HOOK(sub_8300CDA8) // Lumen: XGUntileTextureLevel
REFII_RENDERER_RESOURCE_HOOK(sub_830112F0) // Lumen: XGRAPHICS__UntileVolume
REFII_RENDERER_RESOURCE_HOOK(sub_83011AE0) // Lumen: XGUntileVolumeTextureLevel
REFII_RENDERER_DRAW_HOOK(sub_82BA7B28) // IDA: Fable Pix/video conversion draw chain
REFII_RENDERER_DRAW_HOOK(sub_82BA7898) // Lumen: D3D::PixEnableVideoPlayback
REFII_RENDERER_DRAW_HOOK(sub_82BA7928) // Lumen: D3D::PixDisableVideoPlayback
REFII_RENDERER_GENERAL_HOOK(sub_82BA7988) // Lumen: D3D::PixTimeSinceVBlank
REFII_RENDERER_DRAW_HOOK(sub_82BA79D0) // Lumen: D3D::CodecSetState
REFII_RENDERER_DRAW_HOOK(sub_82BA8928) // Lumen: D3D::PixDecodeVideoFrame
REFII_RENDERER_DRAW_HOOK(sub_822866E0) // Lumen: D3D::CDevice::AddCallsToPrimaryBuffer
REFII_RENDERER_DRAW_HOOK(sub_822868C8) // Lumen: D3D::CDevice::QueueIndirectBuffer
REFII_RENDERER_DRAW_HOOK(sub_8228EF80) // Lumen: D3D::SetPending_Split
REFII_RENDERER_DRAW_HOOK(sub_821E8D38) // Lumen: D3D::CDevice::KickOffSegment(void)
REFII_RENDERER_DRAW_HOOK(sub_821E8EC0) // Lumen: D3D::CDevice::KickOff(void)
REFII_RENDERER_GENERAL_HOOK(sub_821D3570) // Lumen: D3D::PatchVertexShaderToMatchVertexDeclaration
REFII_RENDERER_GENERAL_HOOK(sub_821E0220) // Lumen: D3D::IncrementalShaderPatchAndLoad
REFII_RENDERER_GENERAL_HOOK(sub_821F7650) // Lumen: D3D::DirectShaderPatch
REFII_RENDERER_GENERAL_HOOK(sub_82BA3D88) // Lumen: D3D::PatchVertexShaderToMatchPixelShader
REFII_RENDERER_GENERAL_HOOK(sub_831CFA58) // Lumen: XGRAPHICS::ProcessVertexInputPatch
REFII_RENDERER_DRAW_HOOK(sub_821BFDF0) // Lumen: D3DDevice_SetRenderState_ZFunc
REFII_RENDERER_DRAW_HOOK(sub_821C91C8) // Lumen: D3DDevice_SetRenderState_ColorWriteEnable
REFII_RENDERER_DRAW_HOOK(sub_821DCAA0) // Lumen: D3DDevice_SetRenderState_AlphaBlendEnable
REFII_RENDERER_DRAW_HOOK(sub_821DC158) // Lumen: D3DDevice_SetRenderState_SrcBlend
REFII_RENDERER_DRAW_HOOK(sub_821E56F8) // Lumen/IDA: D3DDevice_SetRenderState_StencilEnable
REFII_RENDERER_DRAW_HOOK(sub_821F0490) // Lumen: D3DDevice_SetRenderState_DestBlend
REFII_RENDERER_DRAW_HOOK(sub_82215488) // Lumen: D3DDevice_SetRenderState_AlphaTestEnable
REFII_RENDERER_DRAW_HOOK(sub_821B1330) // IDA: D3DDevice_SetRenderState_AlphaFunc
REFII_RENDERER_DRAW_HOOK(sub_821B1350) // IDA: D3DDevice_SetRenderState_StencilWriteMask
REFII_RENDERER_DRAW_HOOK(sub_82225E08) // IDA: D3DDevice_SetRenderState_AlphaRef
REFII_RENDERER_DRAW_HOOK(sub_8221D190) // IDA: packed blend control callback
REFII_RENDERER_DRAW_HOOK(sub_8226AE78) // IDA: packed depth mode callback
REFII_RENDERER_DRAW_HOOK(sub_821E0BF8) // IDA: packed depth mode callback
REFII_RENDERER_DRAW_HOOK(sub_82272E50) // IDA: D3DDevice_SetRenderState_PixelCenterOffset
REFII_RENDERER_DRAW_HOOK(sub_8220B470) // Lumen: D3DDevice_SetRenderState_CullMode
REFII_RENDERER_DRAW_HOOK(sub_82286D78) // Lumen: D3DDevice_SetRenderState_ZEnable
REFII_RENDERER_DRAW_HOOK(sub_8229F790) // Lumen: D3DDevice_SetRenderState_HiZWriteEnable
REFII_RENDERER_DRAW_HOOK(sub_822C0968) // Lumen: D3DDevice_SetRenderState_BlendOp
REFII_RENDERER_DRAW_HOOK(sub_8221E410) // Lumen: D3DDevice_SetRenderState_HighPrecisionBlendEnable
REFII_RENDERER_DRAW_HOOK(sub_8226B058) // IDA: D3DDevice_SetRenderState_ZWriteEnable
REFII_RENDERER_DRAW_HOOK(sub_821887D8) // Lumen: D3DDevice_SetRenderState_StencilFail
REFII_RENDERER_DRAW_HOOK(sub_82188800) // Lumen: D3DDevice_SetRenderState_StencilZFail
REFII_RENDERER_DRAW_HOOK(sub_8229ABB8) // IDA: D3DDevice_SetRenderState_StencilPass
REFII_RENDERER_DRAW_HOOK(sub_822A6180) // IDA: D3DDevice_SetRenderState_StencilFunc
REFII_RENDERER_DRAW_HOOK(sub_822B4008) // Lumen: D3D::SetPending_ClipPlanes
REFII_RENDERER_DRAW_HOOK(sub_822C08E8) // IDA: D3DDevice_SetRenderState_StencilMask
REFII_RENDERER_DRAW_HOOK(sub_82239910) // IDA: D3DDevice_SetRenderState_StencilRef
REFII_RENDERER_DRAW_HOOK(sub_82B96EF0) // Lumen: D3DDevice_SetRenderState_BlendOpAlpha
REFII_RENDERER_DRAW_HOOK(sub_82B96F60) // Lumen: D3DDevice_SetRenderState_SrcBlendAlpha
REFII_RENDERER_DRAW_HOOK(sub_82B96FD0) // Lumen: D3DDevice_SetRenderState_DestBlendAlpha
REFII_RENDERER_DRAW_HOOK(sub_82B97040) // Lumen: D3DDevice_SetRenderState_SeparateAlphaBlendEnable
REFII_RENDERER_DRAW_HOOK(sub_82B97448) // Lumen: D3DDevice_SetRenderState_ClipPlaneEnable
REFII_RENDERER_DRAW_HOOK(sub_82B97BB0) // Lumen: D3DDevice_SetSamplerState_MinFilterZ
REFII_RENDERER_DRAW_HOOK(sub_82B97C90) // Lumen: D3DDevice_SetSamplerState_MagFilterZ
REFII_RENDERER_DRAW_HOOK(sub_82B97D48) // Lumen: D3DDevice_SetSamplerState_SeparateZFilterEnable
REFII_RENDERER_GENERAL_HOOK(sub_82B981D0) // Lumen: D3DDevice_GetRenderTarget
REX_EXTERN(sub_82B668D0) {
  REX_FUNC_PROLOGUE();
  const refii::renderer::RendererHookArgs args =
      refii::renderer::CaptureRendererHookArgs(ctx);
  refii::renderer::OnRendererPassQueueHook(args, base);
  if (refii::renderer::ShouldRunNativeRendererHook(
          "sub_82B668D0", refii::renderer::RendererHookKind::kDraw)) {
    __imp__sub_82B668D0(ctx, base);
    refii::renderer::OnRendererPassQueueSnapshot(args, base);
    refii::renderer::OnRendererGuestHookExit(
        "sub_82B668D0", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base);
  } else {
    refii::renderer::OnRendererGuestHookNativeSkipped(
        "sub_82B668D0", refii::renderer::RendererHookKind::kDraw, args, ctx,
        base);
  }
}
REFII_RENDERER_DRAW_HOOK(sub_822869A0) // Lumen: D3DDevice_SetRenderTarget
REFII_RENDERER_DRAW_HOOK(sub_82264590) // Lumen: D3DDevice_SetDepthStencilSurface
REFII_RENDERER_DRAW_HOOK(sub_822A09A0) // Lumen: D3D::SetColorClear
REFII_RENDERER_DRAW_HOOK(sub_821E2FE0) // Lumen: D3DDevice_ClearF
REFII_RENDERER_DRAW_HOOK(sub_821EF830) // Lumen: D3D::ClearRect
REFII_RENDERER_DRAW_HOOK(sub_821F9D00) // Lumen: D3D::SetViewport
REFII_RENDERER_DRAW_HOOK(sub_821F9918) // Lumen: D3D::SetSurfaceClip
REFII_RENDERER_DRAW_HOOK(sub_821F9F58) // Lumen: D3DDevice_SetViewport
REFII_RENDERER_DRAW_HOOK(sub_821B6EE0) // Lumen: D3DDevice_SetTexture
REFII_RENDERER_DRAW_HOOK(sub_821F9C08) // Lumen: D3DDevice_SetScissorRect
REFII_RENDERER_DRAW_HOOK(sub_8221C3E8) // Mapped: DrawPrimitive
REFII_RENDERER_DRAW_HOOK(sub_8221DFC0) // Mapped: DrawIndexedPrimitive
REFII_RENDERER_DRAW_HOOK(sub_82265CD8) // Lumen: D3DDevice_DrawVerticesUP
REFII_RENDERER_DRAW_HOOK(rex_lhRenderer_EndScene)
REFII_RENDERER_DRAW_HOOK(sub_82BA23E8) // Lumen: D3DDevice_UnsetAll
REFII_RENDERER_DRAW_HOOK(sub_82BA34D8) // Lumen: D3DDevice_Swap
REFII_RENDERER_DRAW_HOOK(sub_82BA32F8) // Lumen: D3D::ComplexSwap
REFII_RENDERER_DRAW_HOOK(sub_82BA2DB8) // Lumen: D3D::SwapCallback
REFII_RENDERER_DRAW_HOOK(sub_82BA2F68) // Lumen: D3D::SynchronizeToPresentationInterval
REFII_RENDERER_DRAW_HOOK(rex_lhRenderer_PrepareFrame) // IDA: lhRenderer_PrepareFrame
REFII_RENDERER_DRAW_HOOK(rex_lhRenderer_SetupFrame) // Lumen: lhRenderer_SetupFrame
REFII_RENDERER_DRAW_HOOK(sub_82B98248) // Lumen: D3DDevice_SetGammaRamp
REFII_RENDERER_DRAW_HOOK(sub_82BA2748) // Lumen: D3DDevice_BlockUntilIdle
REFII_RENDERER_DRAW_HOOK(sub_82BA83C0) // IDA: D3D::PixRenderVideoFrame
REFII_RENDERER_DRAW_HOOK(sub_82BA8EF8) // IDA: Fable Pix/video frame packet builder
REFII_RENDERER_DRAW_HOOK(sub_82BA8E58) // Lumen: D3D::FrameDone
REFII_RENDERER_DRAW_HOOK(sub_82BA95E0) // Lumen: D3D::PixCaptureVideoFrame
REFII_RENDERER_DRAW_HOOK(sub_82BAA848) // Lumen: D3D::PixSyncronizeToPresentationInterval
REFII_RENDERER_DRAW_HOOK(sub_82BAA920) // Lumen: D3D::PixInitializePerfMemory
REFII_RENDERER_GENERAL_HOOK(sub_82BAE960) // Lumen: D3D::InitializeDefaultScalerParameters
REFII_RENDERER_DRAW_HOOK(sub_82BAEA88) // Lumen: D3D::SetupScaler
REFII_RENDERER_DRAW_HOOK(sub_82BAEC38) // Lumen: D3DDevice_SetScalerParameters
REFII_RENDERER_GENERAL_HOOK(sub_8217F3E8) // Lumen: D3DDevice_QueryBufferSpace
REFII_RENDERER_GENERAL_HOOK(sub_821F6270) // Lumen: D3DDevice_IsFencePending
REFII_RENDERER_GENERAL_HOOK(sub_82BA2218) // Lumen: D3DDevice_Release
REFII_RENDERER_GENERAL_HOOK(sub_82BA2270) // Lumen: D3DDevice_AcquireThreadOwnership
REFII_RENDERER_GENERAL_HOOK(sub_82BA2500) // Lumen: D3DDevice_GetDeviceState
REFII_RENDERER_DRAW_HOOK(sub_822224A8) // Mapped: SetVertexDeclaration
REFII_RENDERER_RESOURCE_HOOK(rex_lhGfx_CreateGPUState) // Lumen: D3DDevice_CreateVertexDeclaration
REFII_RENDERER_DRAW_HOOK(rex_XGSetVertexDeclaration) // Lumen: XGSetVertexDeclaration
REFII_RENDERER_RESOURCE_HOOK(sub_82278138) // Lumen: D3D::LockSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82B986C8) // Lumen: D3D::Lock3DSurface
REFII_RENDERER_RESOURCE_HOOK(sub_82B98778) // Lumen: D3DBaseTexture_LockTail
REFII_RENDERER_RESOURCE_HOOK(sub_82B98C48) // Lumen: D3DSurface_LockRect
REFII_RENDERER_RESOURCE_HOOK(sub_82B994B8) // Lumen: D3DDevice_CreateVertexShader
REFII_RENDERER_RESOURCE_HOOK(rex_XGRegisterVertexShader) // Lumen: XGRegisterVertexShader
REFII_RENDERER_RESOURCE_HOOK(sub_830FE488) // Lumen: XGRAPHICS__ThreadSafeSSMPrecompileShader
REFII_RENDERER_RESOURCE_HOOK(sub_831CBAF8) // Lumen: XGRAPHICS__TranslateShader
REFII_RENDERER_DRAW_HOOK(sub_82BA40F0) // Lumen: D3DVertexShader_Bind
REFII_RENDERER_DRAW_HOOK(sub_82232510) // Lumen: D3DDevice_SetVertexShader
REFII_RENDERER_DRAW_HOOK(sub_821B6C60) // Lumen: D3DDevice_SetStreamSource
REFII_RENDERER_DRAW_HOOK(sub_8219CC30) // Lumen: D3DDevice_SetIndices
REFII_RENDERER_RESOURCE_HOOK(sub_82B992B0) // Lumen: D3DDevice_CreatePixelShader
REFII_RENDERER_DRAW_HOOK(sub_82208BB0) // Lumen: D3DDevice_SetPixelShader
REFII_RENDERER_RESOURCE_HOOK(sub_82D16CF8) // Lumen: D3DXFillTexture
REFII_RENDERER_RESOURCE_HOOK(sub_82D17250) // Lumen: CreateTextureFromFileInMemoryEx

#undef REFII_RENDERER_DRAW_HOOK
#undef REFII_RENDERER_RESOURCE_HOOK
#undef REFII_RENDERER_GENERAL_HOOK
#undef REFII_RENDERER_PASSTHROUGH_HOOK_AS
