#include "tools/decompiler/preview_fallback_render.h"

#include "tools/decompiler/preview_render.h"
#include "tools/decompiler/render_state.h"

namespace refii::lua {

bool TryMergeLuaPreviewRangeFallback(const LuaPreviewFallbackContext& context,
                                     int fallback_start,
                                     int fallback_end,
                                     int line,
                                     const std::vector<StraightLineValue>& values,
                                     int preferred_reg,
                                     bool require_temporary_declaration,
                                     StraightLineValue* merged_value,
                                     const StraightLineValue* initial_target_value) {
    if (merged_value == nullptr || !context.evaluate_inline_range ||
        fallback_start > fallback_end) {
        return false;
    }

    std::vector<StraightLineValue> original_values_for_merge = values;
    if (initial_target_value != nullptr && preferred_reg >= 0 &&
        static_cast<std::size_t>(preferred_reg) < original_values_for_merge.size()) {
        original_values_for_merge[static_cast<std::size_t>(preferred_reg)] =
            *initial_target_value;
    }

    std::vector<StraightLineValue> fallback_values = original_values_for_merge;
    std::vector<bool> original_declared_for_merge;
    std::vector<bool> fallback_declared;
    std::string preview_error;
    if (context.preview_context != nullptr && context.initial_declared != nullptr) {
        original_declared_for_merge = *context.initial_declared;
        fallback_declared = original_declared_for_merge;
        if (!EvaluateLuaInlineRange(*context.preview_context,
                                    fallback_start,
                                    fallback_end,
                                    &fallback_values,
                                    &fallback_declared,
                                    &preview_error)) {
            return false;
        }

        const std::vector<bool> written_registers = CollectWrittenLuaRegistersInRange(
            context.preview_context->instructions,
            context.register_count,
            fallback_start,
            fallback_end);
        FinalizeLuaJoinCandidateState(&fallback_values,
                                      &fallback_declared,
                                      original_values_for_merge,
                                      original_declared_for_merge,
                                      written_registers,
                                      context.declarations,
                                      context.function,
                                      line,
                                      context.register_count);
    } else {
        if (!context.evaluate_inline_range(
                fallback_start, fallback_end, &fallback_values, &preview_error)) {
            return false;
        }
    }

    return TryMergeLuaPreviewRegisterFallback(original_values_for_merge,
                                              original_declared_for_merge.empty()
                                                  ? nullptr
                                                  : &original_declared_for_merge,
                                              fallback_values,
                                              fallback_declared.empty()
                                                  ? nullptr
                                                  : &fallback_declared,
                                              context.declarations,
                                              context.function,
                                              line,
                                              context.register_count,
                                              preferred_reg,
                                              require_temporary_declaration,
                                              merged_value);
}

}  // namespace refii::lua
