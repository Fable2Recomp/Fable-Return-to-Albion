#include "runtime/refii_renderer/hooks/general/fable_scene_boundary_hooks.h"

#include <array>
#include <cstddef>

namespace refii::renderer {

bool IsFableSceneStreamBoundaryHook(std::string_view symbol) {
  return symbol == "sub_821A5BD0" || // scene stream publish cursor sync
         symbol == "sub_821A59E0" || // scene stream publish marker
         symbol == "sub_822A90B8" || // scene stream readable check
         symbol == "sub_82294830" || // scene stream reader refresh
         symbol == "sub_8222A5D8" || // scene stream wait for readable window
         symbol == "sub_8222AB78" || // scene stream message begin/publish
         symbol == "sub_822DFE08" || // scene stream consumer
         symbol == "rex_lhRenderer_BeginGPUFrame" ||
         symbol == "rex_lhRenderer_EndGPUFrame";
}

bool IsFableSceneFactoryBoundaryHook(std::string_view symbol) {
  return symbol == "sub_82A4F6A8" || // scene factory table reset
         symbol == "sub_82A4F718" || // scene factory table resize
         symbol == "sub_82A4F858" || // scene factory table shrink
         symbol == "sub_82A4EC60" || // scene factory allocate/register
         symbol == "sub_82A4EDF8" || // scene factory release/unregister
         symbol == "sub_82A4F8D8" || // scene table insert/resize
         symbol == "sub_832B2CE0" || // scene table reset
         symbol == "sub_821E1F40" || // scene table assign
         symbol == "sub_821CEA80" || // scene factory upstream caller
         symbol == "sub_8229BA10" || // direct scene factory wrapper
         symbol == "sub_8235D268" || // menu/overlay scene factory
         symbol == "sub_8235EF70" || // menu textured scene item factory
         symbol == "sub_8235F108" || // menu scene item factory
         symbol == "sub_8235FDC0" || // title/menu scene setup
         symbol == "sub_8235E5A0" || // menu scene factory caller
         symbol == "sub_82361AF8" || // menu scene scheduler
         symbol == "sub_823C1658" || // post-frame scene assign caller
         symbol == "sub_82456E60" || // direct scene factory caller
         symbol == "sub_826E4E58" || // direct scene factory caller
         symbol == "sub_826E4F68" || // direct scene factory caller
         symbol == "sub_82870B70" || // direct scene factory caller
         symbol == "sub_82A77C48" || // scene table assign caller
         symbol == "sub_82A780C0" || // scene table assign caller
         symbol == "sub_82A931E0" || // scene table assign caller
         symbol == "sub_82AB99F8" || // data-driven scene factory
         symbol == "sub_82AB9CF8" || // data-driven scene factory caller
         symbol == "sub_82ABA800" || // scene table assign caller
         symbol == "sub_82ABAEF0";   // level graphics scene factory caller
}

int FableSceneStreamObserverHookIndex(std::string_view symbol) {
  constexpr std::array<std::string_view, 7> kSceneStreamHooks = {
      "sub_821A5BD0", "sub_821A59E0", "sub_822A90B8", "sub_82294830",
      "sub_8222AB78", "rex_lhRenderer_BeginGPUFrame",
      "rex_lhRenderer_EndGPUFrame"};
  for (size_t index = 0; index < kSceneStreamHooks.size(); ++index) {
    if (symbol == kSceneStreamHooks[index]) {
      return static_cast<int>(index);
    }
  }
  return -1;
}

bool IsFableSceneStreamObserverHook(std::string_view symbol) {
  return FableSceneStreamObserverHookIndex(symbol) >= 0;
}

int FableSceneFactoryObserverHookIndex(std::string_view symbol) {
  constexpr std::array<std::string_view, 30> kSceneFactoryHooks = {
      "sub_82A4F6A8", "sub_82A4F718", "sub_82A4F858", "sub_82A4EC60",
      "sub_82A4EDF8", "sub_8229BA10", "sub_821CEA80", "sub_822DFE08",
      "sub_8235CA90", "sub_8235CCB8", "sub_8235CED8", "sub_8235D0F8",
      "sub_8235D268", "sub_8235EF70", "sub_8235F108", "sub_8235FDC0",
      "sub_8235E5A0", "sub_82361AF8", "sub_823C1658",
      "sub_82456E60", "sub_826E4E58", "sub_826E4F68", "sub_82870B70",
      "sub_82A77C48", "sub_82A780C0", "sub_82A931E0", "sub_82AB99F8",
      "sub_82AB9CF8", "sub_82ABA800", "sub_82ABAEF0"};
  for (size_t index = 0; index < kSceneFactoryHooks.size(); ++index) {
    if (symbol == kSceneFactoryHooks[index]) {
      return static_cast<int>(index);
    }
  }
  return -1;
}

bool IsFableSceneFactoryObserverHook(std::string_view symbol) {
  return FableSceneFactoryObserverHookIndex(symbol) >= 0;
}

}  // namespace refii::renderer
