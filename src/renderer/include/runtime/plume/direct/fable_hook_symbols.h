#pragma once

#include <cstdint>
#include <string_view>

namespace refii::renderer {

enum class FableHookResourceKind : uint8_t {
  kUnknown,
  kTexture,
  kSurfaceView,
  kRenderTargetSurface,
  kDepthSurface,
  kVertexBuffer,
  kIndexBuffer,
  kVertexDeclaration,
  kVertexShader,
  kPixelShader,
};

enum class FableCreatedResourceAddressSource : uint8_t {
  kResult,
  kR3,
  kR3Plus4,
  kR3Plus0x14,
};

enum class FableDrawSubmitKind : uint8_t {
  kNone,
  kBeginVertices,
  kBeginIndexedVertices,
  kDrawPrimitive,
  kDrawIndexedPrimitive,
  kDrawVerticesUp,
};

enum class FableVertexDeclarationHookKind : uint8_t {
  kNone,
  kSetVertexDeclaration,
  kIncrementalShaderPatch,
  kDirectShaderPatch,
  kPatchVertexShaderDeclaration,
};

enum class FableShaderConstantBank : uint8_t {
  kUnknown,
  kVertex,
  kPixel,
};

enum class FableShaderConstantHookKind : uint8_t {
  kNone,
  kImmediateFloatVectors,
  kTextureFetch,
  kFloatVectors,
  kIntVectors,
  kBoolConstants,
  kLiteralFloatDwords,
};

enum class FableRootTextureHeaderHookKind : uint8_t {
  kNone,
  kSetTextureHeader,
  kSetBaseTextureHeader,
  kSetLineOrCubeTextureHeader,
  kOffsetBaseTextureAddress,
};

enum class FableCompatibilityHookKind : uint8_t {
  kNone,
  kVertexBufferDescFromObject,
  kIndexBufferDescFromObject,
  kCurrentVertexDeclaration,
  kVertexDeclarationHash,
  kBackBufferFromRenderTarget,
  kStretchRectSurfaceCopy,
};

enum class FableUnleashedExactHookKind : uint8_t {
  kNone,
  kHook,
  kStub,
};

bool IsFablePresentHook(std::string_view symbol);
bool IsFableDisplaySwapHook(std::string_view symbol);
bool IsFablePresentedDrawCaptureHook(std::string_view symbol);
bool IsFableRootImageCreateHook(std::string_view symbol);
bool IsFableResourceLockHook(std::string_view symbol);
bool IsFableResourceUnlockHook(std::string_view symbol);
bool IsFableResourceLifetimeHook(std::string_view symbol);
bool IsFableResourceHeaderBuildHook(std::string_view symbol);
bool IsFableResourceUpdateReplacementHook(std::string_view symbol);
bool IsFableRootDrawSubmitHook(std::string_view symbol);
bool IsFableWrappedDrawSubmitHook(std::string_view symbol);
bool IsFableRenderPassHook(std::string_view symbol);
bool IsFablePendingStateHook(std::string_view symbol);
bool IsFableCommandWriteHook(std::string_view symbol);
bool IsFableCommandSubmitHook(std::string_view symbol);
bool IsFableVideoStateHook(std::string_view symbol);
bool IsFableDeviceStateHook(std::string_view symbol);
bool IsFableVertexShaderBindHook(std::string_view symbol);
bool IsFableBinkVideoUploadHook(std::string_view symbol);
bool IsFableEndExportInvalidateCaller(uint32_t source_lr);
bool IsFableUnleashedExactReplacementHook(std::string_view symbol);

const char* FableHookName(std::string_view symbol);
const char* FableHookAddressName(uint32_t address);
const char* FablePm4OpcodeName(uint32_t opcode);
const char* FableUnleashedExactReplacementRole(std::string_view symbol);
const char* FableUnleashedExactReplacementSource(std::string_view symbol);

FableHookResourceKind FableCreatedResourceKind(std::string_view symbol);
FableHookResourceKind FableLockedBufferResourceKind(std::string_view symbol);
FableCreatedResourceAddressSource FableCreatedResourceAddressSourceForSymbol(
    std::string_view symbol);
FableDrawSubmitKind FableDrawSubmitKindForSymbol(std::string_view symbol);
FableHookResourceKind FableBoundShaderKindForSymbol(std::string_view symbol);
FableHookResourceKind FableRegisteredShaderKindForSymbol(
    std::string_view symbol);
FableVertexDeclarationHookKind FableVertexDeclarationHookKindForSymbol(
    std::string_view symbol);
FableShaderConstantHookKind FableShaderConstantHookKindForSymbol(
    std::string_view symbol);
FableShaderConstantBank FableShaderConstantBankForSymbol(
    std::string_view symbol);
FableRootTextureHeaderHookKind FableRootTextureHeaderHookKindForSymbol(
    std::string_view symbol);
FableCompatibilityHookKind FableCompatibilityHookKindForSymbol(
    std::string_view symbol);
FableUnleashedExactHookKind FableUnleashedExactHookKindForSymbol(
    std::string_view symbol);

uint32_t FableRenderStateSlotForSymbol(std::string_view symbol);
uint32_t FableRenderStateSlotForAddress(uint32_t address);
uint32_t FableSamplerStateSlotForSymbol(std::string_view symbol);
uint32_t FableSamplerStateSlotForAddress(uint32_t address);

}  // namespace refii::renderer
