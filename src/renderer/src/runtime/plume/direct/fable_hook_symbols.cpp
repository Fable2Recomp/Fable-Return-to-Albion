#include "runtime/plume/direct/fable_hook_symbols.h"

#include <array>

namespace refii::renderer {
namespace {

struct FableHookNameEntry {
  std::string_view symbol;
  const char *name;
};

struct FableHookAddressNameEntry {
  uint32_t address;
  const char *name;
};

struct FableHookSymbolSlotEntry {
  std::string_view symbol;
  uint32_t slot;
};

struct FableHookAddressSlotEntry {
  uint32_t address;
  uint32_t slot;
};

struct FableHookResourceKindEntry {
  std::string_view symbol;
  FableHookResourceKind kind;
};

struct FableCreatedResourceAddressSourceEntry {
  std::string_view symbol;
  FableCreatedResourceAddressSource source;
};

struct FableDrawSubmitKindEntry {
  std::string_view symbol;
  FableDrawSubmitKind kind;
};

struct FableVertexDeclarationHookKindEntry {
  std::string_view symbol;
  FableVertexDeclarationHookKind kind;
};

struct FableShaderConstantHookEntry {
  std::string_view symbol;
  FableShaderConstantHookKind kind;
  FableShaderConstantBank bank;
};

struct FableRootTextureHeaderHookEntry {
  std::string_view symbol;
  FableRootTextureHeaderHookKind kind;
};

struct FableCompatibilityHookEntry {
  std::string_view symbol;
  FableCompatibilityHookKind kind;
};

struct FableUnleashedExactHookEntry {
  std::string_view symbol;
  std::string_view unleashed_symbol;
  const char *role;
  FableUnleashedExactHookKind kind;
};

constexpr auto kFableHookNames = std::to_array<FableHookNameEntry>({
    {"sub_82B98930", "LockTextureRect"},
    {"sub_82B98950", "UnlockTextureRect"},
    {"sub_82B9EEC0", "UnlockVertexBuffer"},
    {"sub_82B9EED0", "UnlockIndexBuffer"},
    {"rex_lhGfx_LockBuffer", "D3D::LockResource"},
    {"rex_lhGfx_FlushBuffer", "D3D::UnlockResource"},
    {"rex_lhGfx_CreateVertexBuffer", "Fable::CreateVertexBuffer"},
    {"rex_lhGfx_CreateIndexBuffer", "Fable::CreateIndexBuffer"},
    {"rex_lhGfx_CreateGPUState", "D3DDevice_CreateVertexDeclaration"},
    {"sub_821FC8D8", "Fable::CreateSurfaceWrapper"},
    {"sub_82295380", "Fable::CreateTiledSurfaceWrapper"},
    {"sub_82B8AB30", "Fable::RenderDepthSurfaceCtor"},
    {"rex_lhTexture_Create", "D3DDevice_CreateSurface"},
    {"sub_82B98C88", "D3DSurface_GetContainer"},
    {"sub_821FA6C0", "D3D::FindSurfaceWithinTexture"},
    {"sub_821FAB38", "D3D::GetTextureDimensions"},
    {"sub_821FABD0", "D3D::Get2DSurfaceDesc"},
    {"sub_82B985C8", "D3D::Get3DSurfaceDesc"},
    {"sub_821FAD60", "D3D::GetLevelSize"},
    {"sub_82A4E8D0", "Fable::BinkVideoPlaneSurfaceSetup"},
    {"sub_82B6F040", "Fable::PresentationScratchSetup"},
    {"sub_82B6F5C8", "Fable::PresentationScratchTeardown"},
    {"sub_821A5BD0", "Fable::SceneStreamSyncPublishCursor"},
    {"sub_821A59E0", "Fable::SceneStreamPublish"},
    {"sub_822A90B8", "Fable::SceneStreamReadable"},
    {"sub_82294830", "Fable::SceneStreamRefresh"},
    {"sub_8222AB78", "Fable::SceneStreamBeginMessage"},
    {"rex_lhRenderer_BeginGPUFrame", "Fable::SceneStreamCriticalEnter"},
    {"rex_lhRenderer_EndGPUFrame", "Fable::SceneStreamCriticalLeave"},
    {"rex_lhRenderer_GetTimeDelta", "Fable::RendererGetTimeDelta"},
    {"sub_82A4F6A8", "Fable::SceneFactoryTableReset"},
    {"sub_82A4F718", "Fable::SceneFactoryTableResize"},
    {"sub_82A4F858", "Fable::SceneFactoryTableShrink"},
    {"sub_82A4EC60", "Fable::SceneFactoryAllocate"},
    {"sub_82A4EDF8", "Fable::SceneFactoryRelease"},
    {"sub_82A4F8D8", "Fable::SceneTableInsert"},
    {"sub_832B2CE0", "Fable::SceneTableReset"},
    {"sub_821E1F40", "Fable::SceneTableAssign"},
    {"sub_8229BA10", "Fable::DirectSceneFactory"},
    {"sub_821CEA80", "Fable::SceneFactoryUpstreamA"},
    {"sub_822DFE08", "Fable::SceneTableAssignCallerA"},
    {"sub_8235CA90", "Fable::MenuSceneOwnerCtorA"},
    {"sub_8235CCB8", "Fable::MenuSceneOwnerCtorB"},
    {"sub_8235CED8", "Fable::MenuSceneOwnerCtorC"},
    {"sub_8235D0F8", "Fable::MenuSceneOwnerActivate"},
    {"sub_8235D268", "Fable::MenuSceneFactory"},
    {"sub_8235EF70", "Fable::MenuTexturedSceneItemFactory"},
    {"sub_8235F108", "Fable::MenuSceneItemFactory"},
    {"sub_8235FDC0", "Fable::TitleMenuSceneSetupA"},
    {"sub_8235E5A0", "Fable::MenuSceneFactoryCaller"},
    {"sub_82361AF8", "Fable::MenuSceneScheduler"},
    {"sub_823C1658", "Fable::SceneTableAssignCallerB"},
    {"sub_82456E60", "Fable::DirectSceneFactoryCallerA"},
    {"sub_826E4E58", "Fable::DirectSceneFactoryCallerB"},
    {"sub_826E4F68", "Fable::DirectSceneFactoryCallerC"},
    {"sub_82870B70", "Fable::DirectSceneFactoryCallerD"},
    {"sub_82A77C48", "Fable::SceneTableAssignCallerC"},
    {"sub_82A780C0", "Fable::SceneTableAssignCallerD"},
    {"sub_82A931E0", "Fable::SceneTableAssignCallerE"},
    {"sub_82AB99F8", "Fable::DataDrivenSceneFactory"},
    {"sub_82AB9CF8", "Fable::DataDrivenSceneFactoryCaller"},
    {"sub_82ABA800", "Fable::SceneTableAssignCallerF"},
    {"sub_82ABAEF0", "Fable::LevelGraphicsSceneFactoryCaller"},
    {"sub_82292AC0", "Fable::SceneEnableUpdateA"},
    {"sub_82298998", "Fable::SceneEnableUpdateB"},
    {"sub_82299AE8", "Fable::SceneReadyGate"},
    {"sub_8226B078", "Fable::BindDepthSurface"},
    {"sub_821BA0F0", "Fable::SceneRenderSubmit"},
    {"rex_lhRenderer_RenderFrame", "Fable::RenderFrameOwner"},
    {"sub_821C3858", "Fable::FrameDirector"},
    {"sub_821C3C60", "Fable::SceneFrameSubmit"},
    {"sub_8226E180", "Fable::SceneStateFlush"},
    {"sub_8226E738", "Fable::SceneDrawSubmit"},
    {"sub_82ABBA60", "Fable::SceneObjectDrawFilter"},
    {"sub_82ABD088", "Fable::SceneObjectBatchTraversal"},
    {"sub_82219740", "Fable::SceneBatchDrawPrimitiveBridge"},
    {"sub_822194B8", "Fable::SceneBatchCommandToggle"},
    {"sub_8223CFA0", "Fable::SceneObjectListWalker"},
    {"sub_822A8F68", "Fable::SceneObjectListWalkerFlush"},
    {"sub_82205F68", "D3DDevice_BeginVertices"},
    {"sub_82217DB8", "D3DDevice_BeginIndexedVertices"},
    {"sub_821ECD30", "D3D::PrivateTextureRectangleActiveSlotWrapper"},
    {"sub_822326E0", "D3D::PrivateTextureRectangleWrapper"},
    {"sub_82263D28", "D3D::PrivateTextureRectangleSourceArgWrapper"},
    {"sub_8227F3B0", "D3D::PrivateTextureRectangleBatchWrapper"},
    {"sub_822A0A78", "D3D::PrivateTextureRectangleRectArgWrapper"},
    {"sub_822A0B08", "D3D::PrivateTextureRectangleSourceFieldWrapper"},
    {"sub_822C40C8", "D3D::PrivateTextureRectangleSourceObjectWrapper"},
    {"sub_822C4290", "D3D::PrivateTextureRectangleTitleGlobalWrapper"},
    {"sub_82A6E698", "D3D::PrivateTextureRectanglePostprocessFieldWrapper"},
    {"sub_82A7FDE0", "D3D::PrivateTextureRectangleSharedSourceWrapper"},
    {"sub_82A7FF20", "D3D::PrivateTextureRectangleSharedSourceBatchWrapper"},
    {"sub_82A82FE8", "D3D::PrivateTextureRectangleRenderObjectWrapper"},
    {"sub_82A6A458", "D3D::PrivateTextureRectangleSharedPostWrapper"},
    {"sub_82A6A598", "D3D::PrivateTextureRectanglePostprocessWrapper"},
    {"sub_82A6AA90", "D3D::PrivateTextureRectanglePostprocessAltWrapper"},
    {"sub_8228AB70", "D3D::PrivateTextureRectangleSetupWrapper"},
    {"sub_8228ACB8", "D3D::PrivateTextureRectangleSetupFlushWrapper"},
    {"sub_82242108", "D3D::PrivateTextureRectangleSiblingWrapper"},
    {"sub_82206888", "D3D::PrivateTextureRectangleDraw"},
    {"sub_8221B010", "D3D::SetPending_Shaders"},
    {"sub_8221AE18", "D3D::SetPending_HiZEnable"},
    {"sub_8221C7D8", "D3D::SetPending_RenderStates"},
    {"sub_8221D1B0", "D3D::SetPending_FetchConstants"},
    {"sub_822154B0", "D3D::SetPending_Predicated"},
    {"sub_8228EF80", "D3D::SetPending_Split"},
    {"sub_822B4008", "D3D::SetPending_ClipPlanes"},
    {"sub_82BA25C0", "D3D::CDevice::AddCommandsToPrimaryBuffer"},
    {"sub_8221E4A0", "D3D::DispatchDeferredPendingGpuState"},
    {"sub_8221F348", "D3D::ProcessPendingGpuStateQueue"},
    {"sub_8221F2E0", "D3D::FlushPendingGpuState"},
    {"sub_821FC540", "Fable::FlushTransientRenderTargetReleaseQueue"},
    {"sub_8221C3E8", "D3DDevice_DrawPrimitive"},
    {"sub_8221DFC0", "D3DDevice_DrawIndexedPrimitive"},
    {"sub_822224A8", "D3DDevice_SetVertexDeclaration"},
    {"sub_82BA8928", "D3D::PixDecodeVideoFrame"},
    {"sub_82BA7898", "D3D::PixEnableVideoPlayback"},
    {"sub_82BA7928", "D3D::PixDisableVideoPlayback"},
    {"sub_82BA7988", "D3D::PixTimeSinceVBlank"},
    {"sub_82BA79D0", "D3D::CodecSetState"},
    {"sub_82A42FA0", "Fable::BinkVideoPlaneSetup"},
    {"sub_832BCB30", "BinkFileReadFrame"},
    {"sub_832BCDE8", "BinkFileReadFrameAsync"},
    {"sub_832C49E8", "ExpandBink"},
    {"sub_82BA83C0", "D3D::PixRenderVideoFrame"},
    {"sub_82BA8EF8", "D3D::PixRenderVideoFramePacket"},
    {"sub_82BA95E0", "D3D::PixCaptureVideoFrame"},
    {"sub_82BAA848", "D3D::PixSyncronizeToPresentationInterval"},
    {"sub_82BAA920", "D3D::PixInitializePerfMemory"},
    {"sub_822866E0", "D3D::CDevice::AddCallsToPrimaryBuffer"},
    {"sub_822868C8", "D3D::CDevice::QueueIndirectBuffer"},
    {"sub_821E8D38", "D3D::CDevice::KickOffSegment"},
    {"sub_821E8EC0", "D3D::CDevice::KickOff"},
    {"sub_82196628", "D3DDevice_BeginExport"},
    {"sub_82196750", "D3DDevice_EndExport"},
    {"sub_822C6BA8", "D3D::InvalidateCaches"},
    {"sub_8219CD68", "D3DDevice_SetShaderGPRAllocation"},
    {"sub_821EC798", "D3DDevice_SetSamplerState_MipMapLodBias"},
    {"sub_82202D10", "D3DDevice_SetSamplerState_AddressU"},
    {"sub_8222C568", "D3DDevice_SetSamplerState_AddressV"},
    {"sub_822373F0", "D3DDevice_SetSamplerState_AddressW"},
    {"sub_822383A0", "D3DDevice_SetSamplerState_MagFilter"},
    {"sub_82238128", "D3DDevice_SetSamplerState_MinFilter"},
    {"sub_8222C818", "D3DDevice_SetSamplerState_MipFilter"},
    {"sub_822398D0", "D3DDevice_SetSamplerState_FilterQuality"},
    {"sub_821B1330", "D3DDevice_SetRenderState_AlphaFunc"},
    {"sub_821B1350", "D3DDevice_SetRenderState_StencilWriteMask"},
    {"sub_82225E08", "D3DDevice_SetRenderState_AlphaRef"},
    {"sub_8229ABB8", "D3DDevice_SetRenderState_StencilPass"},
    {"sub_822A6180", "D3DDevice_SetRenderState_StencilFunc"},
    {"sub_822C08E8", "D3DDevice_SetRenderState_StencilMask"},
    {"sub_82239910", "D3DDevice_SetRenderState_StencilRef"},
    {"sub_821D3570", "D3D::PatchVertexShaderToMatchVertexDeclaration"},
    {"sub_821E0220", "D3D::IncrementalShaderPatchAndLoad"},
    {"sub_821F7650", "D3D::DirectShaderPatch"},
    {"sub_82BA3D88", "D3D::PatchVertexShaderToMatchPixelShader"},
    {"sub_831CFA58", "XGRAPHICS::ProcessVertexInputPatch"},
    {"rex_XGRegisterVertexShader", "XGRegisterVertexShader"},
    {"sub_830FE488", "XGRAPHICS__ThreadSafeSSMPrecompileShader"},
    {"sub_831CBAF8", "XGRAPHICS__TranslateShader"},
    {"sub_821FBB78", "XGSetTextureHeader"},
    {"sub_8300BD38", "XGSetVertexBufferHeader"},
    {"sub_8300BDD0", "XGSetIndexBufferHeader"},
    {"sub_8300BE60", "XGSetConstantBufferHeader"},
    {"sub_8300BEE0", "XGSetLineTextureHeader"},
    {"sub_8300BF48", "XGSetCubeTextureHeader"},
    {"sub_821F52A0", "XGSurfaceSize"},
    {"sub_8300C290", "XGRAPHICS__TileSurface"},
    {"sub_8300CB40", "j_XGRAPHICS__TileSurface"},
    {"sub_8300CB48", "XGTileTextureLevel"},
    {"sub_83010E60", "XGRAPHICS__TileVolume"},
    {"sub_830117B8", "XGTileVolumeTextureLevel"},
    {"sub_8300C660", "XGRAPHICS__UntileSurface"},
    {"sub_8300CDA0", "j_XGRAPHICS__UntileSurface"},
    {"sub_8300CDA8", "XGUntileTextureLevel"},
    {"sub_830112F0", "XGRAPHICS__UntileVolume"},
    {"sub_83011AE0", "XGUntileVolumeTextureLevel"},
    {"sub_8300F7E8", "XGCopyVolume"},
    {"sub_8300F928", "XGCompressSurface"},
    {"sub_83012888", "XGEndianSwapSurface"},
    {"sub_83013A70", "XG_D3DXTex::CBlt::BltCopy"},
    {"sub_831C51F8", "XGRAPHICS__DoMemExport"},
    {"sub_8222BFA0", "D3D::SetLiteralShaderConstants"},
    {"sub_82BA76F8", "D3D::PM4LoadShaderConstantsImmediate"},
    {"sub_82BA77D0", "D3D::PM4SetTextureFetchConstant"},
    {"sub_8221A880", "D3DDevice_SetVertexShaderConstantF"},
    {"sub_82214E30", "D3DDevice_SetPixelShaderConstantF"},
    {"sub_822655F0", "D3DDevice_SetPredication"},
    {"sub_822AF568", "D3DDevice_SetScreenExtentQueryMode"},
    {"sub_8220B470", "D3DDevice_SetRenderState_CullMode"},
    {"sub_8221D190", "D3DDevice_SetRenderState_PackedBlendControl20"},
    {"sub_8226AE78", "D3DDevice_SetRenderState_PackedDepthModeBit2"},
    {"sub_821E0BF8", "D3DDevice_SetRenderState_PackedDepthModeBit5"},
    {"sub_82272E50", "D3DDevice_SetRenderState_PixelCenterOffset"},
    {"sub_82BA23E8", "D3DDevice_UnsetAll"},
    {"sub_82BAEA88", "D3D::SetupScaler"},
    {"sub_82BAE960", "D3D::InitializeDefaultScalerParameters"},
    {"sub_82BAEC38", "D3DDevice_SetScalerParameters"},
    {"sub_822A09A0", "D3D::SetColorClear"},
    {"sub_82B98248", "D3DDevice_SetGammaRamp"},
    {"sub_82B98318", "D3DDevice_SetGammaRamp"},
    {"sub_82D160D8", "D3DXLoadSurfaceFromSurface"},
    {"sub_82D21C68", "D3DXTex::InternalCopy"},
    {"sub_82D21D60", "D3DXTex::CLockSurface::Unlock"},
    {"sub_82D21EC0", "D3DXTex::CLockSurface::Lock"},
    {"sub_82D1EF90", "D3DXTex::CBlt::BltCopy"},
    {"sub_82D14B20", "D3DXLoadSurfaceFromMemory"},
    {"sub_82D14378", "D3DXTex::CreateTextureFromTexture"},
    {"sub_82D168C8", "D3DXFilterTexture"},
    {"sub_82BA2748", "D3DDevice_BlockUntilIdle"},
    {"sub_8217F3E8", "D3DDevice_QueryBufferSpace"},
    {"sub_821F6270", "D3DDevice_IsFencePending"},
    {"sub_82242668", "D3D::CDevice::BlockOnFence"},
    {"sub_82298630", "D3D::CDevice::SetFence"},
    {"sub_821FC178", "D3DResource_AddRef"},
    {"sub_821FC320", "D3DResource_Release"},
    {"sub_82BA2218", "D3DDevice_Release"},
    {"sub_82BA2270", "D3DDevice_AcquireThreadOwnership"},
    {"sub_82BA22B0", "D3DDevice_ReleaseThreadOwnership"},
    {"sub_82BA2500", "D3DDevice_GetDeviceState"},
    {"sub_82BA6508", "D3D::InitializePresentationParameters"},
    {"sub_82EF2A90", "D3DDevice::BeginScene"},
    {"rex_XGOffsetBaseTextureAddress", "XGOffsetBaseTextureAddress"},
    {"sub_82BA40F0", "D3DVertexShader_Bind"},
    {"rex_lhRenderer_PrepareFrame", "lhRenderer_PrepareFrame"},
    {"rex_lhRenderer_SetupFrame", "lhRenderer_SetupFrame"},
});

constexpr auto kFableUnleashedExactHooks =
    std::to_array<FableUnleashedExactHookEntry>({
        {"sub_82B98930", "sub_82BE9300", "LockTextureRect",
         FableUnleashedExactHookKind::kHook},
        {"sub_82B98950", "sub_82BE7780", "UnlockTextureRect",
         FableUnleashedExactHookKind::kHook},
        {"sub_8225F980", "sub_82BE6B98", "LockVertexBuffer",
         FableUnleashedExactHookKind::kHook},
        {"sub_82B9EEC0", "sub_82BE6BE8", "UnlockVertexBuffer",
         FableUnleashedExactHookKind::kHook},
        {"sub_82196980", "sub_82BE6CA8", "LockIndexBuffer",
         FableUnleashedExactHookKind::kHook},
        {"sub_82B9EED0", "sub_82BE6CF0", "UnlockIndexBuffer",
         FableUnleashedExactHookKind::kHook},
        {"sub_821FA070", "sub_82BE96F0", "GetSurfaceDesc",
         FableUnleashedExactHookKind::kHook},
        {"sub_82B98B28", "sub_82BE9498", "CreateTexture",
         FableUnleashedExactHookKind::kHook},
        {"rex_lhTexture_Create", "sub_82BE95B8", "CreateSurface",
         FableUnleashedExactHookKind::kHook},
        {"sub_822869A0", "sub_82BDD9F0", "SetRenderTarget",
         FableUnleashedExactHookKind::kHook},
        {"sub_82264590", "sub_82BDDD38", "SetDepthStencilSurface",
         FableUnleashedExactHookKind::kHook},
        {"sub_821E2FE0", "sub_82BFE4C8", "Clear",
         FableUnleashedExactHookKind::kHook},
        {"sub_821F9F58", "sub_82BDD8C0", "SetViewport",
         FableUnleashedExactHookKind::kHook},
        {"sub_821B6EE0", "sub_82BE9818", "SetTexture",
         FableUnleashedExactHookKind::kHook},
        {"sub_821F9C08", "sub_82BDCFB0", "SetScissorRect",
         FableUnleashedExactHookKind::kHook},
        {"sub_8221C3E8", "sub_82BE5900", "DrawPrimitive",
         FableUnleashedExactHookKind::kHook},
        {"sub_8221DFC0", "sub_82BE5CF0", "DrawIndexedPrimitive",
         FableUnleashedExactHookKind::kHook},
        {"sub_82265CD8", "sub_82BE52F8", "DrawPrimitiveUP",
         FableUnleashedExactHookKind::kHook},
        {"rex_lhGfx_CreateGPUState", "sub_82BE0428",
         "CreateVertexDeclaration", FableUnleashedExactHookKind::kHook},
        {"sub_82B99720", "sub_82BE0428", "CreateVertexDeclaration",
         FableUnleashedExactHookKind::kHook},
        {"sub_822224A8", "sub_82BE02E0", "SetVertexDeclaration",
         FableUnleashedExactHookKind::kHook},
        {"sub_82B994B8", "sub_82BE1A80", "CreateVertexShader",
         FableUnleashedExactHookKind::kHook},
        {"sub_82232510", "sub_82BE0110", "SetVertexShader",
         FableUnleashedExactHookKind::kHook},
        {"sub_821B6C60", "sub_82BDD0F8", "SetStreamSource",
         FableUnleashedExactHookKind::kHook},
        {"sub_8219CC30", "sub_82BDD218", "SetIndices",
         FableUnleashedExactHookKind::kHook},
        {"sub_82B992B0", "sub_82BE1990", "CreatePixelShader",
         FableUnleashedExactHookKind::kHook},
        {"sub_82208BB0", "sub_82BDFE58", "SetPixelShader",
         FableUnleashedExactHookKind::kHook},
        {"sub_82D16CF8", "sub_82C003B8", "D3DXFillTexture",
         FableUnleashedExactHookKind::kHook},
        {"sub_82BA2270", "sub_82BD97A8",
         "D3DDevice_AcquireThreadOwnership", FableUnleashedExactHookKind::kStub},
        {"sub_82BA22B0", "sub_82BD97E8",
         "D3DDevice_ReleaseThreadOwnership", FableUnleashedExactHookKind::kStub},
        {"sub_82B98248", "sub_82BDD370", "D3DDevice_SetGammaRamp",
         FableUnleashedExactHookKind::kStub},
        {"sub_82B98318", "sub_82BDD370", "D3DDevice_SetGammaRamp",
         FableUnleashedExactHookKind::kStub},
        {"sub_8219CD68", "sub_82BE05B8",
         "D3DDevice_SetShaderGPRAllocation", FableUnleashedExactHookKind::kStub},
        {"sub_822655F0", "sub_82BE9B28", "D3DDevice_SetPredication",
         FableUnleashedExactHookKind::kStub},
        {"sub_82D168C8", "sub_82BFFF88", "D3DXFilterTexture",
         FableUnleashedExactHookKind::kStub},
        {"sub_82BA2218", "sub_82BD96D0", "D3DDevice_Release",
         FableUnleashedExactHookKind::kStub},
    });

constexpr auto kFableCreatedResourceKinds =
    std::to_array<FableHookResourceKindEntry>({
        {"sub_82B98B28", FableHookResourceKind::kTexture},
        {"sub_82B98978", FableHookResourceKind::kSurfaceView},
        {"sub_82B98A58", FableHookResourceKind::kSurfaceView},
        {"sub_82B98860", FableHookResourceKind::kSurfaceView},
        {"sub_821FC8D8", FableHookResourceKind::kRenderTargetSurface},
        {"rex_lhTexture_Create", FableHookResourceKind::kRenderTargetSurface},
        {"sub_82295380", FableHookResourceKind::kDepthSurface},
        {"sub_82B8AB30", FableHookResourceKind::kRenderTargetSurface},
        {"rex_lhGfx_CreateVertexBuffer", FableHookResourceKind::kVertexBuffer},
        {"rex_lhGfx_CreateIndexBuffer", FableHookResourceKind::kIndexBuffer},
        {"rex_lhGfx_CreateGPUState", FableHookResourceKind::kVertexDeclaration},
        {"sub_82B994B8", FableHookResourceKind::kVertexShader},
        {"sub_82B992B0", FableHookResourceKind::kPixelShader},
    });

constexpr auto kFableLockedBufferResourceKinds =
    std::to_array<FableHookResourceKindEntry>({
        {"sub_8225F980", FableHookResourceKind::kVertexBuffer},
        {"rex_lhGfx_LockBuffer", FableHookResourceKind::kVertexBuffer},
        {"sub_82196980", FableHookResourceKind::kIndexBuffer},
    });

constexpr auto kFableBoundShaderKinds =
    std::to_array<FableHookResourceKindEntry>({
        {"sub_82232510", FableHookResourceKind::kVertexShader},
        {"sub_82208BB0", FableHookResourceKind::kPixelShader},
    });

constexpr auto kFableRegisteredShaderKinds =
    std::to_array<FableHookResourceKindEntry>({
        {"rex_XGRegisterVertexShader", FableHookResourceKind::kVertexShader},
    });

constexpr auto kFableCreatedResourceAddressSources =
    std::to_array<FableCreatedResourceAddressSourceEntry>({
        {"rex_lhGfx_CreateVertexBuffer",
         FableCreatedResourceAddressSource::kR3},
        {"rex_lhGfx_CreateIndexBuffer", FableCreatedResourceAddressSource::kR3},
        {"sub_821FC8D8", FableCreatedResourceAddressSource::kR3Plus4},
        {"sub_82295380", FableCreatedResourceAddressSource::kR3Plus4},
        {"sub_82B8AB30", FableCreatedResourceAddressSource::kR3Plus0x14},
    });

constexpr auto kFableRootDrawSubmitHooks = std::to_array<std::string_view>({
    "sub_82205F68",
    "sub_82217DB8",
    "sub_8221C3E8",
    "sub_8221DFC0",
});

constexpr auto kFableWrappedDrawSubmitHooks = std::to_array<std::string_view>({
    "sub_82205F68",
    "sub_82217DB8",
    "sub_82206888",
    "sub_8221C3E8",
    "sub_8221DFC0",
    "sub_82265CD8",
});

constexpr auto kFableDrawSubmitKinds = std::to_array<FableDrawSubmitKindEntry>({
    {"sub_82205F68", FableDrawSubmitKind::kBeginVertices},
    {"sub_82217DB8", FableDrawSubmitKind::kBeginIndexedVertices},
    {"sub_8221C3E8", FableDrawSubmitKind::kDrawPrimitive},
    {"sub_8221DFC0", FableDrawSubmitKind::kDrawIndexedPrimitive},
    {"sub_82265CD8", FableDrawSubmitKind::kDrawVerticesUp},
});

constexpr auto kFableVertexDeclarationHookKinds =
    std::to_array<FableVertexDeclarationHookKindEntry>({
        {"sub_822224A8", FableVertexDeclarationHookKind::kSetVertexDeclaration},
        {"rex_XGSetVertexDeclaration",
         FableVertexDeclarationHookKind::kSetVertexDeclaration},
        {"sub_821E0220",
         FableVertexDeclarationHookKind::kIncrementalShaderPatch},
        {"sub_821F7650", FableVertexDeclarationHookKind::kDirectShaderPatch},
        {"sub_821D3570",
         FableVertexDeclarationHookKind::kPatchVertexShaderDeclaration},
    });

constexpr auto kFableShaderConstantHooks =
    std::to_array<FableShaderConstantHookEntry>({
        {"sub_82BA76F8", FableShaderConstantHookKind::kImmediateFloatVectors,
         FableShaderConstantBank::kUnknown},
        {"sub_82BA77D0", FableShaderConstantHookKind::kTextureFetch,
         FableShaderConstantBank::kUnknown},
        {"sub_8221A880", FableShaderConstantHookKind::kFloatVectors,
         FableShaderConstantBank::kVertex},
        {"sub_82214E30", FableShaderConstantHookKind::kFloatVectors,
         FableShaderConstantBank::kPixel},
        {"sub_82B991B0", FableShaderConstantHookKind::kIntVectors,
         FableShaderConstantBank::kVertex},
        {"sub_82B99208", FableShaderConstantHookKind::kIntVectors,
         FableShaderConstantBank::kPixel},
        {"sub_821C6708", FableShaderConstantHookKind::kBoolConstants,
         FableShaderConstantBank::kVertex},
        {"sub_8226E120", FableShaderConstantHookKind::kBoolConstants,
         FableShaderConstantBank::kPixel},
        {"sub_8222BFA0", FableShaderConstantHookKind::kLiteralFloatDwords,
         FableShaderConstantBank::kUnknown},
    });

constexpr auto kFableRenderPassHooks = std::to_array<std::string_view>({
    "sub_821BA0F0",
    "sub_821C3C60",
    "sub_8226E180",
    "sub_8226E738",
    "sub_8223CFA0",
    "sub_822A8F68",
    "sub_82B668D0",
});

constexpr auto kFablePendingStateHooks = std::to_array<std::string_view>({
    "sub_8221B010",
    "sub_8221C7D8",
    "sub_8221D1B0",
    "sub_822154B0",
    "sub_8228EF80",
    "sub_822B4008",
});

constexpr auto kFableCommandWriteHooks = std::to_array<std::string_view>({
    "sub_82BA25C0",
    "sub_822866E0",
    "sub_822868C8",
});

constexpr auto kFableCommandSubmitHooks = std::to_array<std::string_view>({
    "sub_821E8D38",
    "sub_821E8EC0",
});

constexpr auto kFableVideoStateHooks = std::to_array<std::string_view>({
    "sub_82BA7898",
    "sub_82BA7928",
    "sub_82BA79D0",
});

constexpr auto kFableDeviceStateHooks = std::to_array<std::string_view>({
    "sub_8219CD68",
    "sub_822655F0",
    "sub_822AF568",
    "sub_82BAA848",
    "sub_82BAA920",
    "sub_82BAE960",
    "sub_82BAEA88",
    "sub_82BAEC38",
});

constexpr auto kFableBinkVideoUploadHooks = std::to_array<std::string_view>({
    "sub_832BCB30",
    "sub_832BCDE8",
    "sub_832C49E8",
});

constexpr auto kFableRootTextureHeaderHooks =
    std::to_array<FableRootTextureHeaderHookEntry>({
        {"sub_821FB650", FableRootTextureHeaderHookKind::kSetTextureHeader},
        {"sub_821FB968", FableRootTextureHeaderHookKind::kSetBaseTextureHeader},
        {"sub_821FBB78", FableRootTextureHeaderHookKind::kSetBaseTextureHeader},
        {"sub_8300BEE0",
         FableRootTextureHeaderHookKind::kSetLineOrCubeTextureHeader},
        {"sub_8300BF48",
         FableRootTextureHeaderHookKind::kSetLineOrCubeTextureHeader},
        {"rex_XGOffsetBaseTextureAddress",
         FableRootTextureHeaderHookKind::kOffsetBaseTextureAddress},
    });

constexpr auto kFableCompatibilityHooks = std::to_array<
    FableCompatibilityHookEntry>({
    {"rex_lhGfx_CreateVertexBuffer",
     FableCompatibilityHookKind::kVertexBufferDescFromObject},
    {"sub_8225F980", FableCompatibilityHookKind::kVertexBufferDescFromObject},
    {"rex_lhGfx_LockBuffer",
     FableCompatibilityHookKind::kVertexBufferDescFromObject},
    {"rex_lhGfx_CreateIndexBuffer",
     FableCompatibilityHookKind::kIndexBufferDescFromObject},
    {"sub_82196980", FableCompatibilityHookKind::kIndexBufferDescFromObject},
    {"sub_822224A8", FableCompatibilityHookKind::kCurrentVertexDeclaration},
    {"rex_XGSetVertexDeclaration",
     FableCompatibilityHookKind::kCurrentVertexDeclaration},
    {"rex_lhGfx_CreateGPUState",
     FableCompatibilityHookKind::kVertexDeclarationHash},
    {"sub_82B99720", FableCompatibilityHookKind::kVertexDeclarationHash},
    {"sub_82B981D0", FableCompatibilityHookKind::kBackBufferFromRenderTarget},
    {"sub_82B6F040", FableCompatibilityHookKind::kBackBufferFromRenderTarget},
    {"sub_82D160D8", FableCompatibilityHookKind::kStretchRectSurfaceCopy},
    {"sub_82D21C68", FableCompatibilityHookKind::kStretchRectSurfaceCopy},
    {"sub_82D14B20", FableCompatibilityHookKind::kStretchRectSurfaceCopy},
});

constexpr auto kFableHookAddressNames =
    std::to_array<FableHookAddressNameEntry>({
        {0x82B98930, "LockTextureRect"},
        {0x82B98950, "UnlockTextureRect"},
        {0x82B9EEC0, "UnlockVertexBuffer"},
        {0x82B9EED0, "UnlockIndexBuffer"},
        {0x821FC178, "D3DResource_AddRef"},
        {0x821FC8D8, "Fable::CreateSurfaceWrapper"},
        {0x821F4EC0, "D3DDevice_CreateSurface"},
        {0x82B98C88, "D3DSurface_GetContainer"},
        {0x821FA6C0, "D3D::FindSurfaceWithinTexture"},
        {0x821FAB38, "D3D::GetTextureDimensions"},
        {0x821FABD0, "D3D::Get2DSurfaceDesc"},
        {0x82B985C8, "D3D::Get3DSurfaceDesc"},
        {0x821FAD60, "D3D::GetLevelSize"},
        {0x82295380, "Fable::CreateTiledSurfaceWrapper"},
        {0x82B8AB30, "Fable::RenderDepthSurfaceCtor"},
        {0x82A4E8D0, "Fable::BinkVideoPlaneSurfaceSetup"},
        {0x82B6F040, "Fable::PresentationScratchSetup"},
        {0x82B6F5C8, "Fable::PresentationScratchTeardown"},
        {0x82A4F0E0, "Fable::SceneStreamCriticalEnter"},
        {0x82A4F1D8, "Fable::SceneStreamCriticalLeave"},
        {0x82B6FCE8, "Fable::RendererGetTimeDelta"},
        {0x82A4F6A8, "Fable::SceneFactoryTableReset"},
        {0x82A4F718, "Fable::SceneFactoryTableResize"},
        {0x82A4F858, "Fable::SceneFactoryTableShrink"},
        {0x82A4EC60, "Fable::SceneFactoryAllocate"},
        {0x82A4EDF8, "Fable::SceneFactoryRelease"},
        {0x82A4F8D8, "Fable::SceneTableInsert"},
        {0x832B2CE0, "Fable::SceneTableReset"},
        {0x821E1F40, "Fable::SceneTableAssign"},
        {0x8229BA10, "Fable::DirectSceneFactory"},
        {0x821CEA80, "Fable::SceneFactoryUpstreamA"},
        {0x822DFE08, "Fable::SceneTableAssignCallerA"},
        {0x8235D268, "Fable::MenuSceneFactory"},
        {0x8235E5A0, "Fable::MenuSceneFactoryCaller"},
        {0x823C1658, "Fable::SceneTableAssignCallerB"},
        {0x82456E60, "Fable::DirectSceneFactoryCallerA"},
        {0x826E4E58, "Fable::DirectSceneFactoryCallerB"},
        {0x826E4F68, "Fable::DirectSceneFactoryCallerC"},
        {0x82870B70, "Fable::DirectSceneFactoryCallerD"},
        {0x82A77C48, "Fable::SceneTableAssignCallerC"},
        {0x82A780C0, "Fable::SceneTableAssignCallerD"},
        {0x82A931E0, "Fable::SceneTableAssignCallerE"},
        {0x82AB99F8, "Fable::DataDrivenSceneFactory"},
        {0x82AB9CF8, "Fable::DataDrivenSceneFactoryCaller"},
        {0x82ABA800, "Fable::SceneTableAssignCallerF"},
        {0x82ABAEF0, "Fable::LevelGraphicsSceneFactoryCaller"},
        {0x8226B078, "Fable::BindDepthSurface"},
        {0x821BA0F0, "Fable::SceneRenderSubmit"},
        {0x82388380, "Fable::RenderFrameOwner"},
        {0x821C3858, "Fable::FrameDirector"},
        {0x821C3C60, "Fable::SceneFrameSubmit"},
        {0x8226E180, "Fable::SceneStateFlush"},
        {0x8226E738, "Fable::SceneDrawSubmit"},
        {0x82ABBA60, "Fable::SceneObjectDrawFilter"},
        {0x82ABD088, "Fable::SceneObjectBatchTraversal"},
        {0x82219740, "Fable::SceneBatchDrawPrimitiveBridge"},
        {0x822194B8, "Fable::SceneBatchCommandToggle"},
        {0x8223CFA0, "Fable::SceneObjectListWalker"},
        {0x822A8F68, "Fable::SceneObjectListWalkerFlush"},
        {0x82EF2A90, "D3DDevice::BeginScene"},
        {0x82B6ED10, "lhRenderer_PrepareFrame"},
        {0x82B6EDE0, "lhRenderer_SetupFrame"},
        {0x821B6C60, "D3DDevice_SetStreamSource"},
        {0x821B6EE0, "D3DDevice_SetTexture"},
        {0x821B1330, "D3DDevice_SetRenderState_AlphaFunc"},
        {0x821B1350, "D3DDevice_SetRenderState_StencilWriteMask"},
        {0x82225E08, "D3DDevice_SetRenderState_AlphaRef"},
        {0x82196628, "D3DDevice_BeginExport"},
        {0x82196750, "D3DDevice_EndExport"},
        {0x822C6BA8, "D3D::InvalidateCaches"},
        {0x8219CD68, "D3DDevice_SetShaderGPRAllocation"},
        {0x821BFDF0, "D3DDevice_SetRenderState_ZFunc"},
        {0x821C91C8, "D3DDevice_SetRenderState_ColorWriteEnable"},
        {0x821DCAA0, "D3DDevice_SetRenderState_AlphaBlendEnable"},
        {0x821DC158, "D3DDevice_SetRenderState_SrcBlend"},
        {0x821E2FE0, "D3DDevice_ClearF"},
        {0x821EF830, "D3D::ClearRect"},
        {0x821E56F8, "D3DDevice_SetRenderState_StencilEnable"},
        {0x821EC798, "D3DDevice_SetSamplerState_MipMapLodBias"},
        {0x821E0BF8, "D3DDevice_SetRenderState_PackedDepthModeBit5"},
        {0x821F0490, "D3DDevice_SetRenderState_DestBlend"},
        {0x8221A880, "D3DDevice_SetVertexShaderConstantF"},
        {0x82214E30, "D3DDevice_SetPixelShaderConstantF"},
        {0x821C6708, "D3DDevice_SetVertexShaderConstantB"},
        {0x82B991B0, "D3DDevice_SetVertexShaderConstantI"},
        {0x8226E120, "D3DDevice_SetPixelShaderConstantB"},
        {0x82B99208, "D3DDevice_SetPixelShaderConstantI"},
        {0x821F9C08, "D3DDevice_SetScissorRect"},
        {0x821F9D00, "D3D::SetViewport"},
        {0x821F9F58, "D3DDevice_SetViewport"},
        {0x821F9918, "D3D::SetSurfaceClip"},
        {0x821FBB78, "XGSetTextureHeader"},
        {0x8225FDF0, "XGOffsetBaseTextureAddress"},
        {0x8300BD38, "XGSetVertexBufferHeader"},
        {0x8300BDD0, "XGSetIndexBufferHeader"},
        {0x8300BE60, "XGSetConstantBufferHeader"},
        {0x8300BEE0, "XGSetLineTextureHeader"},
        {0x8300BF48, "XGSetCubeTextureHeader"},
        {0x821F52A0, "XGSurfaceSize"},
        {0x8300C290, "XGRAPHICS__TileSurface"},
        {0x8300CB40, "j_XGRAPHICS__TileSurface"},
        {0x8300CB48, "XGTileTextureLevel"},
        {0x83010E60, "XGRAPHICS__TileVolume"},
        {0x830117B8, "XGTileVolumeTextureLevel"},
        {0x8300C660, "XGRAPHICS__UntileSurface"},
        {0x8300CDA0, "j_XGRAPHICS__UntileSurface"},
        {0x8300CDA8, "XGUntileTextureLevel"},
        {0x830112F0, "XGRAPHICS__UntileVolume"},
        {0x83011AE0, "XGUntileVolumeTextureLevel"},
        {0x8300F7E8, "XGCopyVolume"},
        {0x8300F928, "XGCompressSurface"},
        {0x83012888, "XGEndianSwapSurface"},
        {0x83013A70, "XG_D3DXTex::CBlt::BltCopy"},
        {0x831C51F8, "XGRAPHICS__DoMemExport"},
        {0x82208BB0, "D3DDevice_SetPixelShader"},
        {0x82215488, "D3DDevice_SetRenderState_AlphaTestEnable"},
        {0x821887D8, "D3DDevice_SetRenderState_StencilFail"},
        {0x82188800, "D3DDevice_SetRenderState_StencilZFail"},
        {0x8229ABB8, "D3DDevice_SetRenderState_StencilPass"},
        {0x822A6180, "D3DDevice_SetRenderState_StencilFunc"},
        {0x822C08E8, "D3DDevice_SetRenderState_StencilMask"},
        {0x82239910, "D3DDevice_SetRenderState_StencilRef"},
        {0x8221E410, "D3DDevice_SetRenderState_HighPrecisionBlendEnable"},
        {0x8220B470, "D3DDevice_SetRenderState_CullMode"},
        {0x82202D10, "D3DDevice_SetSamplerState_AddressU"},
        {0x8226B058, "D3DDevice_SetRenderState_ZWriteEnable"},
        {0x8221D190, "D3DDevice_SetRenderState_PackedBlendControl20"},
        {0x8222C568, "D3DDevice_SetSamplerState_AddressV"},
        {0x8222C818, "D3DDevice_SetSamplerState_MipFilter"},
        {0x8221B010, "D3D::SetPending_Shaders"},
        {0x8221AE18, "D3D::SetPending_HiZEnable"},
        {0x8221C7D8, "D3D::SetPending_RenderStates"},
        {0x8221D1B0, "D3D::SetPending_FetchConstants"},
        {0x822154B0, "D3D::SetPending_Predicated"},
        {0x8228EF80, "D3D::SetPending_Split"},
        {0x822B4008, "D3D::SetPending_ClipPlanes"},
        {0x82232510, "D3DDevice_SetVertexShader"},
        {0x82B993C0, "XGRegisterVertexShader"},
        {0x830FE488, "XGRAPHICS__ThreadSafeSSMPrecompileShader"},
        {0x831CBAF8, "XGRAPHICS__TranslateShader"},
        {0x82264590, "D3DDevice_SetDepthStencilSurface"},
        {0x822655F0, "D3DDevice_SetPredication"},
        {0x822869A0, "D3DDevice_SetRenderTarget"},
        {0x82B981D0, "D3DDevice_GetRenderTarget"},
        {0x822A09A0, "D3D::SetColorClear"},
        {0x82286D78, "D3DDevice_SetRenderState_ZEnable"},
        {0x822373F0, "D3DDevice_SetSamplerState_AddressW"},
        {0x82238128, "D3DDevice_SetSamplerState_MinFilter"},
        {0x822383A0, "D3DDevice_SetSamplerState_MagFilter"},
        {0x822398D0, "D3DDevice_SetSamplerState_FilterQuality"},
        {0x8229F790, "D3DDevice_SetRenderState_HiZWriteEnable"},
        {0x8226AE78, "D3DDevice_SetRenderState_PackedDepthModeBit2"},
        {0x82272E50, "D3DDevice_SetRenderState_PixelCenterOffset"},
        {0x822AF568, "D3DDevice_SetScreenExtentQueryMode"},
        {0x822C0968, "D3DDevice_SetRenderState_BlendOp"},
        {0x82B96EF0, "D3DDevice_SetRenderState_BlendOpAlpha"},
        {0x82B96F60, "D3DDevice_SetRenderState_SrcBlendAlpha"},
        {0x82B96FD0, "D3DDevice_SetRenderState_DestBlendAlpha"},
        {0x82B97040, "D3DDevice_SetRenderState_SeparateAlphaBlendEnable"},
        {0x82B97448, "D3DDevice_SetRenderState_ClipPlaneEnable"},
        {0x82B97BB0, "D3DDevice_SetSamplerState_MinFilterZ"},
        {0x82B97C90, "D3DDevice_SetSamplerState_MagFilterZ"},
        {0x82B97D48, "D3DDevice_SetSamplerState_SeparateZFilterEnable"},
        {0x8217F3E8, "D3DDevice_QueryBufferSpace"},
        {0x821F6270, "D3DDevice_IsFencePending"},
        {0x82BA2218, "D3DDevice_Release"},
        {0x82BA2270, "D3DDevice_AcquireThreadOwnership"},
        {0x82BA22B0, "D3DDevice_ReleaseThreadOwnership"},
        {0x82BA2500, "D3DDevice_GetDeviceState"},
        {0x82BA25C0, "D3D::CDevice::AddCommandsToPrimaryBuffer"},
        {0x82BA2748, "D3DDevice_BlockUntilIdle"},
        {0x8221E4A0, "D3D::DispatchDeferredPendingGpuState"},
        {0x8221F348, "D3D::ProcessPendingGpuStateQueue"},
        {0x8221F2E0, "D3D::FlushPendingGpuState"},
        {0x82BA40F0, "D3DVertexShader_Bind"},
        {0x8221C3E8, "D3DDevice_DrawPrimitive"},
        {0x8221DFC0, "D3DDevice_DrawIndexedPrimitive"},
        {0x822224A8, "D3DDevice_SetVertexDeclaration"},
        {0x82B99720, "D3DDevice_CreateVertexDeclaration"},
        {0x82BA23E8, "D3DDevice_UnsetAll"},
        {0x82BA32F8, "D3D::ComplexSwap"},
        {0x82BA34D8, "D3DDevice_Swap"},
        {0x82BA76F8, "D3D::PM4LoadShaderConstantsImmediate"},
        {0x82BA77D0, "D3D::PM4SetTextureFetchConstant"},
        {0x82BA7898, "D3D::PixEnableVideoPlayback"},
        {0x82BA7928, "D3D::PixDisableVideoPlayback"},
        {0x82BA7988, "D3D::PixTimeSinceVBlank"},
        {0x82BA79D0, "D3D::CodecSetState"},
        {0x82BA83C0, "D3D::PixRenderVideoFrame"},
        {0x82BA8928, "D3D::PixDecodeVideoFrame"},
        {0x82B98248, "D3DDevice_SetGammaRamp"},
        {0x82B98318, "D3DDevice_SetGammaRamp"},
        {0x82D160D8, "D3DXLoadSurfaceFromSurface"},
        {0x82D21C68, "D3DXTex::InternalCopy"},
        {0x82D21D60, "D3DXTex::CLockSurface::Unlock"},
        {0x82D21EC0, "D3DXTex::CLockSurface::Lock"},
        {0x82D1EF90, "D3DXTex::CBlt::BltCopy"},
        {0x82D14B20, "D3DXLoadSurfaceFromMemory"},
        {0x82D14378, "D3DXTex::CreateTextureFromTexture"},
        {0x82D168C8, "D3DXFilterTexture"},
        {0x82BA95E0, "D3D::PixCaptureVideoFrame"},
        {0x82BAA848, "D3D::PixSyncronizeToPresentationInterval"},
        {0x82BAA920, "D3D::PixInitializePerfMemory"},
        {0x82BAE960, "D3D::InitializeDefaultScalerParameters"},
        {0x82BAEA88, "D3D::SetupScaler"},
        {0x82BAEC38, "D3DDevice_SetScalerParameters"},
    });

constexpr auto kFableRenderStateSymbolSlots =
    std::to_array<FableHookSymbolSlotEntry>({
        {"sub_821BFDF0", 0},  // ZFunc
        {"sub_821C91C8", 1},  // ColorWriteEnable
        {"sub_821DCAA0", 2},  // AlphaBlendEnable
        {"sub_821DC158", 3},  // SrcBlend
        {"sub_821E56F8", 18}, // StencilEnable
        {"sub_821F0490", 5},  // DestBlend
        {"sub_82215488", 6},  // AlphaTestEnable
        {"sub_82286D78", 7},  // ZEnable
        {"sub_8229F790", 8},  // HiZWriteEnable
        {"sub_822C0968", 9},  // BlendOp
        {"sub_82B96EF0", 10}, // BlendOpAlpha
        {"sub_82B96F60", 11}, // SrcBlendAlpha
        {"sub_82B96FD0", 12}, // DestBlendAlpha
        {"sub_82B97040", 13}, // SeparateAlphaBlendEnable
        {"sub_82B97448", 14}, // ClipPlaneEnable
        {"sub_8221E410", 15}, // HighPrecisionBlendEnable
        {"sub_8226B058", 4},  // ZWriteEnable
        {"sub_8220B470", 19}, // CullMode
        {"sub_821887D8", 16}, // StencilFail
        {"sub_82188800", 17}, // StencilZFail
        {"sub_8229ABB8", 20}, // StencilPass
        {"sub_822A6180", 21}, // StencilFunc
        {"sub_822C08E8", 22}, // StencilMask
        {"sub_82239910", 23}, // StencilRef
        {"sub_821B1330", 24}, // AlphaFunc
        {"sub_821B1350", 27}, // StencilWriteMask
        {"sub_82225E08", 25}, // AlphaRef
        {"sub_82272E50", 26}, // PixelCenterOffset
        {"sub_8226AE78", 28}, // PackedDepthModeBit2
        {"sub_821E0BF8", 29}, // PackedDepthModeBit5
        {"sub_8221D190", 30}, // PackedBlendControl20
    });

constexpr auto kFableRenderStateAddressSlots =
    std::to_array<FableHookAddressSlotEntry>({
        {0x821BFDF0, 0},  // ZFunc
        {0x821C91C8, 1},  // ColorWriteEnable
        {0x821DCAA0, 2},  // AlphaBlendEnable
        {0x821DC158, 3},  // SrcBlend
        {0x821E56F8, 18}, // StencilEnable
        {0x821F0490, 5},  // DestBlend
        {0x82215488, 6},  // AlphaTestEnable
        {0x82286D78, 7},  // ZEnable
        {0x8229F790, 8},  // HiZWriteEnable
        {0x822C0968, 9},  // BlendOp
        {0x82B96EF0, 10}, // BlendOpAlpha
        {0x82B96F60, 11}, // SrcBlendAlpha
        {0x82B96FD0, 12}, // DestBlendAlpha
        {0x82B97040, 13}, // SeparateAlphaBlendEnable
        {0x82B97448, 14}, // ClipPlaneEnable
        {0x8221E410, 15}, // HighPrecisionBlendEnable
        {0x821887D8, 16}, // StencilFail
        {0x82188800, 17}, // StencilZFail
        {0x8226B058, 4},  // ZWriteEnable
        {0x8220B470, 19}, // CullMode
        {0x8229ABB8, 20}, // StencilPass
        {0x822A6180, 21}, // StencilFunc
        {0x822C08E8, 22}, // StencilMask
        {0x82239910, 23}, // StencilRef
        {0x821B1330, 24}, // AlphaFunc
        {0x821B1350, 27}, // StencilWriteMask
        {0x82225E08, 25}, // AlphaRef
        {0x82272E50, 26}, // PixelCenterOffset
        {0x8226AE78, 28}, // PackedDepthModeBit2
        {0x821E0BF8, 29}, // PackedDepthModeBit5
        {0x8221D190, 30}, // PackedBlendControl20
    });

constexpr auto kFableSamplerStateSymbolSlots =
    std::to_array<FableHookSymbolSlotEntry>({
        {"sub_82B97BB0", 0}, // MinFilterZ
        {"sub_82B97C90", 1}, // MagFilterZ
        {"sub_82B97D48", 2}, // SeparateZFilterEnable
        {"sub_821EC798", 3}, // MipMapLodBias
        {"sub_82238128", 0}, // MinFilter
        {"sub_822383A0", 1}, // MagFilter
        {"sub_82202D10", 4}, // AddressU
        {"sub_8222C568", 5}, // AddressV
        {"sub_822373F0", 6}, // AddressW
        {"sub_8222C818", 7}, // MipFilter
        {"sub_822398D0", 8}, // FilterQuality
    });

constexpr auto kFableSamplerStateAddressSlots =
    std::to_array<FableHookAddressSlotEntry>({
        {0x82238128, 0}, // MinFilter
        {0x822383A0, 1}, // MagFilter
        {0x82202D10, 4}, // AddressU
        {0x8222C568, 5}, // AddressV
        {0x822373F0, 6}, // AddressW
        {0x8222C818, 7}, // MipFilter
        {0x822398D0, 8}, // FilterQuality
        {0x82B97BB0, 0}, // MinFilterZ
        {0x82B97C90, 1}, // MagFilterZ
        {0x82B97D48, 2}, // SeparateZFilterEnable
        {0x821EC798, 3}, // MipMapLodBias
    });

template <typename T, size_t N>
const char *FindName(const std::array<T, N> &entries, std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.name;
    }
  }
  return nullptr;
}

template <typename T, size_t N>
const char *FindAddressName(const std::array<T, N> &entries, uint32_t address) {
  for (const auto &entry : entries) {
    if (entry.address == address) {
      return entry.name;
    }
  }
  return nullptr;
}

template <size_t N>
bool ContainsSymbol(const std::array<std::string_view, N> &entries,
                    std::string_view symbol) {
  for (std::string_view entry : entries) {
    if (entry == symbol) {
      return true;
    }
  }
  return false;
}

template <typename T, size_t N>
FableHookResourceKind FindResourceKind(const std::array<T, N> &entries,
                                       std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.kind;
    }
  }
  return FableHookResourceKind::kUnknown;
}

template <typename T, size_t N>
FableCreatedResourceAddressSource
FindResourceAddressSource(const std::array<T, N> &entries,
                          std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.source;
    }
  }
  return FableCreatedResourceAddressSource::kResult;
}

template <typename T, size_t N>
FableDrawSubmitKind FindDrawSubmitKind(const std::array<T, N> &entries,
                                       std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.kind;
    }
  }
  return FableDrawSubmitKind::kNone;
}

template <typename T, size_t N>
FableVertexDeclarationHookKind
FindVertexDeclarationHookKind(const std::array<T, N> &entries,
                              std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.kind;
    }
  }
  return FableVertexDeclarationHookKind::kNone;
}

template <typename T, size_t N>
const FableShaderConstantHookEntry *
FindShaderConstantHook(const std::array<T, N> &entries,
                       std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return &entry;
    }
  }
  return nullptr;
}

template <typename T, size_t N>
FableRootTextureHeaderHookKind
FindRootTextureHeaderHookKind(const std::array<T, N> &entries,
                              std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.kind;
    }
  }
  return FableRootTextureHeaderHookKind::kNone;
}

template <typename T, size_t N>
FableCompatibilityHookKind
FindCompatibilityHookKind(const std::array<T, N> &entries,
                          std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.kind;
    }
  }
  return FableCompatibilityHookKind::kNone;
}

template <typename T, size_t N>
uint32_t FindSymbolSlot(const std::array<T, N> &entries,
                        std::string_view symbol) {
  for (const auto &entry : entries) {
    if (entry.symbol == symbol) {
      return entry.slot;
    }
  }
  return UINT32_MAX;
}

template <typename T, size_t N>
uint32_t FindAddressSlot(const std::array<T, N> &entries, uint32_t address) {
  for (const auto &entry : entries) {
    if (entry.address == address) {
      return entry.slot;
    }
  }
  return UINT32_MAX;
}

const FableUnleashedExactHookEntry *
FindUnleashedExactHook(std::string_view symbol) {
  for (const auto &entry : kFableUnleashedExactHooks) {
    if (entry.symbol == symbol) {
      return &entry;
    }
  }
  return nullptr;
}

} // namespace

bool IsFablePresentHook(std::string_view symbol) {
  return symbol == "rex_lhRenderer_EndScene" || symbol == "sub_82BA34D8" ||
         symbol == "sub_82BA32F8";
}

bool IsFableDisplaySwapHook(std::string_view symbol) {
  return symbol == "sub_82BA34D8" || symbol == "sub_82BA32F8";
}

bool IsFablePresentedDrawCaptureHook(std::string_view symbol) {
  return symbol == "rex_lhRenderer_EndScene" || IsFableDisplaySwapHook(symbol);
}

bool IsFableRootImageCreateHook(std::string_view symbol) {
  return symbol == "rex_lhTexture_Create" || symbol == "sub_82B98B28";
}

bool IsFableResourceLockHook(std::string_view symbol) {
  return symbol == "sub_82B98930" || symbol == "sub_8225F980" ||
         symbol == "sub_82196980" || symbol == "rex_lhGfx_LockBuffer" ||
         symbol == "sub_82278138" || symbol == "sub_82B986C8" ||
         symbol == "sub_82B98778" || symbol == "sub_82B98C48";
}

bool IsFableResourceUnlockHook(std::string_view symbol) {
  return symbol == "sub_82B98950" || symbol == "sub_82B9EEC0" ||
         symbol == "sub_82B9EED0" || symbol == "rex_lhGfx_FlushBuffer";
}

bool IsFableResourceLifetimeHook(std::string_view symbol) {
  return symbol == "sub_821FC178" || // D3DResource_AddRef
         symbol == "sub_821FC320";   // D3DResource_Release
}

bool IsFableResourceHeaderBuildHook(std::string_view symbol) {
  return symbol == "sub_8300BD38" || // XGSetVertexBufferHeader
         symbol == "sub_8300BDD0" || // XGSetIndexBufferHeader
         symbol == "sub_8300BE60";   // XGSetConstantBufferHeader
}

bool IsFableResourceUpdateReplacementHook(std::string_view symbol) {
  return symbol == "sub_82B98950" ||       // UnlockTextureRect
         symbol == "sub_82B9EEC0" ||       // UnlockVertexBuffer
         symbol == "sub_82B9EED0" ||       // UnlockIndexBuffer
         symbol == "rex_lhGfx_FlushBuffer"; // D3D::UnlockResource
}

bool IsFableRootDrawSubmitHook(std::string_view symbol) {
  return ContainsSymbol(kFableRootDrawSubmitHooks, symbol);
}

bool IsFableWrappedDrawSubmitHook(std::string_view symbol) {
  return ContainsSymbol(kFableWrappedDrawSubmitHooks, symbol);
}

bool IsFableRenderPassHook(std::string_view symbol) {
  return ContainsSymbol(kFableRenderPassHooks, symbol);
}

bool IsFablePendingStateHook(std::string_view symbol) {
  return ContainsSymbol(kFablePendingStateHooks, symbol);
}

bool IsFableCommandWriteHook(std::string_view symbol) {
  return ContainsSymbol(kFableCommandWriteHooks, symbol);
}

bool IsFableCommandSubmitHook(std::string_view symbol) {
  return ContainsSymbol(kFableCommandSubmitHooks, symbol);
}

bool IsFableVideoStateHook(std::string_view symbol) {
  return ContainsSymbol(kFableVideoStateHooks, symbol);
}

bool IsFableDeviceStateHook(std::string_view symbol) {
  return ContainsSymbol(kFableDeviceStateHooks, symbol);
}

bool IsFableVertexShaderBindHook(std::string_view symbol) {
  return symbol == "sub_82BA40F0";
}

bool IsFableBinkVideoUploadHook(std::string_view symbol) {
  return ContainsSymbol(kFableBinkVideoUploadHooks, symbol);
}

bool IsFableEndExportInvalidateCaller(uint32_t source_lr) {
  return source_lr == 0x821968A0u || source_lr == 0x821968A4u ||
         source_lr == 0x821968C8u || source_lr == 0x821968CCu;
}

bool IsFableUnleashedExactReplacementHook(std::string_view symbol) {
  return FindUnleashedExactHook(symbol) != nullptr;
}

const char *FableHookName(std::string_view symbol) {
  return FindName(kFableHookNames, symbol);
}

const char *FableHookAddressName(uint32_t address) {
  return FindAddressName(kFableHookAddressNames, address);
}

const char *FablePm4OpcodeName(uint32_t opcode) {
  switch (opcode) {
  case 0x48:
    return "PM4_ME_INIT";
  case 0x10:
    return "PM4_NOP";
  case 0x22:
    return "PM4_DRAW_INDX";
  case 0x27:
    return "PM4_IM_LOAD";
  case 0x2B:
    return "PM4_IM_LOAD_IMMEDIATE";
  case 0x2D:
    return "PM4_SET_CONSTANT";
  case 0x2F:
    return "PM4_LOAD_ALU_CONSTANT";
  case 0x34:
    return "PM4_DRAW_INDX_BIN";
  case 0x35:
    return "PM4_DRAW_INDX_2_BIN";
  case 0x36:
    return "PM4_DRAW_INDX_2";
  case 0x37:
    return "PM4_INDIRECT_BUFFER_PFD";
  case 0x3B:
    return "PM4_INVALIDATE_STATE";
  case 0x3C:
    return "PM4_WAIT_REG_MEM";
  case 0x40:
    return "PM4_WAIT_UNTIL";
  case 0x5C:
    return "PM4_WAIT_UNTIL_READ";
  case 0x3F:
    return "PM4_INDIRECT_BUFFER";
  case 0x46:
    return "PM4_EVENT_WRITE";
  case 0x55:
    return "PM4_SET_CONSTANT2";
  case 0x56:
    return "PM4_SET_SHADER_CONSTANTS";
  case 0x64:
    return "PM4_XE_SWAP";
  default:
    return "PM4_TYPE3_UNKNOWN";
  }
}

const char *FableUnleashedExactReplacementRole(std::string_view symbol) {
  const FableUnleashedExactHookEntry *entry = FindUnleashedExactHook(symbol);
  return entry ? entry->role : nullptr;
}

const char *FableUnleashedExactReplacementSource(std::string_view symbol) {
  const FableUnleashedExactHookEntry *entry = FindUnleashedExactHook(symbol);
  return entry ? entry->unleashed_symbol.data() : nullptr;
}

FableHookResourceKind FableCreatedResourceKind(std::string_view symbol) {
  return FindResourceKind(kFableCreatedResourceKinds, symbol);
}

FableHookResourceKind FableLockedBufferResourceKind(std::string_view symbol) {
  return FindResourceKind(kFableLockedBufferResourceKinds, symbol);
}

FableCreatedResourceAddressSource
FableCreatedResourceAddressSourceForSymbol(std::string_view symbol) {
  return FindResourceAddressSource(kFableCreatedResourceAddressSources, symbol);
}

FableDrawSubmitKind FableDrawSubmitKindForSymbol(std::string_view symbol) {
  return FindDrawSubmitKind(kFableDrawSubmitKinds, symbol);
}

FableHookResourceKind FableBoundShaderKindForSymbol(std::string_view symbol) {
  return FindResourceKind(kFableBoundShaderKinds, symbol);
}

FableHookResourceKind
FableRegisteredShaderKindForSymbol(std::string_view symbol) {
  return FindResourceKind(kFableRegisteredShaderKinds, symbol);
}

FableVertexDeclarationHookKind
FableVertexDeclarationHookKindForSymbol(std::string_view symbol) {
  return FindVertexDeclarationHookKind(kFableVertexDeclarationHookKinds,
                                       symbol);
}

FableShaderConstantHookKind
FableShaderConstantHookKindForSymbol(std::string_view symbol) {
  const FableShaderConstantHookEntry *entry =
      FindShaderConstantHook(kFableShaderConstantHooks, symbol);
  return entry ? entry->kind : FableShaderConstantHookKind::kNone;
}

FableShaderConstantBank
FableShaderConstantBankForSymbol(std::string_view symbol) {
  const FableShaderConstantHookEntry *entry =
      FindShaderConstantHook(kFableShaderConstantHooks, symbol);
  return entry ? entry->bank : FableShaderConstantBank::kUnknown;
}

FableRootTextureHeaderHookKind
FableRootTextureHeaderHookKindForSymbol(std::string_view symbol) {
  return FindRootTextureHeaderHookKind(kFableRootTextureHeaderHooks, symbol);
}

FableCompatibilityHookKind
FableCompatibilityHookKindForSymbol(std::string_view symbol) {
  return FindCompatibilityHookKind(kFableCompatibilityHooks, symbol);
}

FableUnleashedExactHookKind
FableUnleashedExactHookKindForSymbol(std::string_view symbol) {
  const FableUnleashedExactHookEntry *entry = FindUnleashedExactHook(symbol);
  return entry ? entry->kind : FableUnleashedExactHookKind::kNone;
}

uint32_t FableRenderStateSlotForSymbol(std::string_view symbol) {
  return FindSymbolSlot(kFableRenderStateSymbolSlots, symbol);
}

uint32_t FableRenderStateSlotForAddress(uint32_t address) {
  return FindAddressSlot(kFableRenderStateAddressSlots, address);
}

uint32_t FableSamplerStateSlotForSymbol(std::string_view symbol) {
  return FindSymbolSlot(kFableSamplerStateSymbolSlots, symbol);
}

uint32_t FableSamplerStateSlotForAddress(uint32_t address) {
  return FindAddressSlot(kFableSamplerStateAddressSlots, address);
}

} // namespace refii::renderer
