#pragma once

#include "renderer_hook_targets.h"

#include <string_view>

namespace refii::renderer {

enum class RendererHookKind;

using RendererPhysicalWrapperRequiredPredicate =
    bool (*)(std::string_view symbol, RendererHookKind kind);

const RendererHookTarget* ResolveRendererHookTarget(std::string_view symbol);
bool RendererHookAllowedBySurface(const RendererHookTarget &target);
bool RendererHookAllowedBySurface(const RendererHookTarget *target);
bool RendererHookAllowedInCurrentMode(
    std::string_view symbol, RendererHookKind kind,
    RendererPhysicalWrapperRequiredPredicate physical_wrapper_required =
        nullptr);

} // namespace refii::renderer
