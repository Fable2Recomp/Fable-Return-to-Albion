#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"

namespace refii::renderer {

bool FablePrivateRectWrapperSymbolIsBatched(uint32_t symbol) {
  return symbol == kFablePrivateRectBatchedWrapperSymbol ||
         symbol == kFablePrivateRectBatchedIntermediateCallsite ||
         symbol == kFablePrivateRectBatchedFinalCallsite;
}

bool FablePrivateRectWrapperSymbolsCompatible(uint32_t lhs, uint32_t rhs) {
  if (lhs == rhs) {
    return true;
  }
  if (FablePrivateRectWrapperSymbolIsBatched(lhs) &&
      FablePrivateRectWrapperSymbolIsBatched(rhs)) {
    return true;
  }
  return (lhs == kFablePrivateRectSiblingWrapperSymbol &&
          rhs == kFablePrivateRectSiblingWrapperCallsite) ||
         (lhs == kFablePrivateRectSiblingWrapperCallsite &&
          rhs == kFablePrivateRectSiblingWrapperSymbol) ||
         (lhs == kFablePrivateRectTitleGlobalWrapperSymbol &&
          rhs == kFablePrivateRectTitleGlobalWrapperCallsite) ||
         (lhs == kFablePrivateRectTitleGlobalWrapperCallsite &&
          rhs == kFablePrivateRectTitleGlobalWrapperSymbol);
}

const char* DirectIdaPrivateRectWrapperProofName(uint32_t symbol) {
  switch (symbol) {
    case kFablePrivateRectSetupWrapperSymbol:
      return "ida-private-rect-setup-wrapper";
    case kFablePrivateRectSetupFlushWrapperSymbol:
      return "ida-private-rect-setup-flush-wrapper";
    case kFablePrivateRectCompactWrapperSymbol:
      return "ida-private-rect-compact-wrapper";
    case kFablePrivateRectSiblingWrapperSymbol:
    case kFablePrivateRectSiblingWrapperCallsite:
      return "ida-private-rect-sibling-wrapper";
    case kFablePrivateRectTitleLocalWrapperCallsite:
      return "ida-private-rect-title-local-wrapper";
    case kFablePrivateRectSourceObjectWrapperSymbol:
      return "ida-private-rect-source-object-wrapper";
    case kFablePrivateRectBatchedWrapperSymbol:
      return "ida-private-rect-batched-wrapper";
    case kFablePrivateRectBatchedIntermediateCallsite:
      return "ida-private-rect-batched-intermediate-wrapper";
    case kFablePrivateRectBatchedFinalCallsite:
      return "ida-private-rect-batched-final-wrapper";
    case kFablePrivateRectTitleGlobalWrapperSymbol:
    case kFablePrivateRectTitleGlobalWrapperCallsite:
      return "ida-private-rect-title-global-wrapper";
    case kFablePrivateRectNativeDepthWrapperSymbol:
      return "ida-private-rect-native-depth-wrapper";
    default:
      return nullptr;
  }
}

bool DirectIdaPrivateRectWrapperProofSymbol(uint32_t symbol) {
  return DirectIdaPrivateRectWrapperProofName(symbol) != nullptr;
}

bool DirectIdaSurfaceTextureWrapperProofSymbol(uint32_t symbol) {
  return DirectIdaPrivateRectWrapperProofSymbol(symbol) ||
         symbol == kFableBeginVerticesTitleWrapperSymbol ||
         symbol == kFableD3DBeginIndexedVerticesWrapperSymbol;
}

}  // namespace refii::renderer
