#pragma once

#include <string_view>

namespace refii::renderer {

bool IsFableSceneStreamBoundaryHook(std::string_view symbol);
bool IsFableSceneFactoryBoundaryHook(std::string_view symbol);

int FableSceneStreamObserverHookIndex(std::string_view symbol);
bool IsFableSceneStreamObserverHook(std::string_view symbol);
int FableSceneFactoryObserverHookIndex(std::string_view symbol);
bool IsFableSceneFactoryObserverHook(std::string_view symbol);

}  // namespace refii::renderer
