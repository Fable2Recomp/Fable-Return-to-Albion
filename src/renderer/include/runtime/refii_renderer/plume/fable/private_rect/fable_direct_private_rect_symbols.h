#pragma once

#include <cstdint>

namespace refii::renderer {

inline constexpr uint32_t kFableBeginVerticesTitleWrapperSymbol = 0x821EBF30u;
inline constexpr uint32_t kFablePrivateRectSetupWrapperSymbol = 0x8228AB70u;
inline constexpr uint32_t kFablePrivateRectSetupFlushWrapperSymbol =
    0x8228ACB8u;
inline constexpr uint32_t kFablePrivateRectSiblingWrapperSymbol = 0x82242108u;
inline constexpr uint32_t kFablePrivateRectSiblingWrapperCallsite =
    0x822421C4u;
inline constexpr uint32_t kFablePrivateRectCompactWrapperSymbol = 0x822326E0u;
inline constexpr uint32_t kFablePrivateRectTitleLocalWrapperCallsite =
    0x822779ACu;
inline constexpr uint32_t kFablePrivateRectSourceObjectWrapperSymbol =
    0x822C40C8u;
inline constexpr uint32_t kFablePrivateRectBatchedWrapperSymbol = 0x8227F3B0u;
inline constexpr uint32_t kFablePrivateRectBatchedIntermediateCallsite =
    0x8227F510u;
inline constexpr uint32_t kFablePrivateRectBatchedFinalCallsite = 0x8227F540u;
inline constexpr uint32_t kFablePrivateRectTitleGlobalWrapperSymbol =
    0x822C4290u;
inline constexpr uint32_t kFablePrivateRectTitleGlobalWrapperCallsite =
    0x822C4950u;
inline constexpr uint32_t kFablePrivateRectNativeDepthWrapperSymbol =
    0x82A7FDE0u;
inline constexpr uint32_t kFablePrivateRectDrawHelperSymbol = 0x82206888u;
inline constexpr uint32_t kFableD3DBeginIndexedVerticesWrapperSymbol =
    0x82217DB8u;
inline constexpr uint32_t kFableD3DTextureGetSurfaceLevelSymbol = 0x82B98860u;

bool FablePrivateRectWrapperSymbolIsBatched(uint32_t symbol);
bool FablePrivateRectWrapperSymbolsCompatible(uint32_t lhs, uint32_t rhs);

const char* DirectIdaPrivateRectWrapperProofName(uint32_t symbol);
bool DirectIdaPrivateRectWrapperProofSymbol(uint32_t symbol);
bool DirectIdaSurfaceTextureWrapperProofSymbol(uint32_t symbol);

}  // namespace refii::renderer
