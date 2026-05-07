#include "tools/decompiler/testset_render.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/render_state.h"

namespace refii::lua {

StraightLineValue GetLuaTestSetInitialValue(const std::vector<StraightLineValue>& values,
                                            const LuaTestSetFallbackContext& context,
                                            const decompiler::LuaTestSetLoopControlBlock& block,
                                            int line) {
    return GetRegisterValue(values,
                            context.preview_fallback.declarations,
                            context.preview_fallback.function,
                            block.source_register,
                            line);
}

bool TryMergeLuaTestSetFallbackValues(const LuaTestSetFallbackContext& context,
                                      const decompiler::LuaTestSetLoopControlBlock& block,
                                      int line,
                                      const std::vector<StraightLineValue>& values,
                                      StraightLineValue* merged_value) {
    const StraightLineValue initial_value =
        GetLuaTestSetInitialValue(values, context, block, line);
    return TryMergeLuaPreviewRangeFallback(context.preview_fallback,
                                           block.fallback_start,
                                           block.fallback_end,
                                           line,
                                           values,
                                           block.target_register,
                                           false,
                                           merged_value,
                                           &initial_value);
}

bool TryMergeLuaTestSetBranchPathFallbackValues(
    const LuaTestSetFallbackContext& context,
    const decompiler::LuaTestSetLoopControlBlock& block,
    int line,
    const std::vector<StraightLineValue>& values,
    StraightLineValue* merged_value) {
    const StraightLineValue initial_value =
        GetLuaTestSetInitialValue(values, context, block, line);
    return TryMergeLuaPreviewBranchPathFallback(context.preview_fallback,
                                                block.fallback_start,
                                                block.branch_target,
                                                line,
                                                values,
                                                block.target_register,
                                                false,
                                                merged_value,
                                                &initial_value);
}

void AssignLuaTestSetTarget(std::vector<std::string>* lines,
                            std::vector<StraightLineValue>* values,
                            std::vector<bool>* declared,
                            const LuaTestSetFallbackContext& context,
                            const decompiler::LuaTestSetLoopControlBlock& block,
                            int line,
                            const StraightLineValue& initial_value,
                            bool force_materialize) {
    if (force_materialize && lines != nullptr && values != nullptr && declared != nullptr &&
        block.target_register >= 0 &&
        static_cast<std::size_t>(block.target_register) < values->size()) {
        const std::string name = GetStraightLineAssignmentRegisterName(
            context.preview_fallback.declarations,
            context.preview_fallback.function,
            block.target_register,
            line);
        const bool emit_local =
            !(*declared)[static_cast<std::size_t>(block.target_register)] &&
            block.target_register >= context.preview_fallback.function.parameter_count;
        AppendIndentedStatement(lines,
                                "  ",
                                RenderAssignmentStatement(name, initial_value.text, emit_local));
        if (emit_local) {
            (*declared)[static_cast<std::size_t>(block.target_register)] = true;
        }
        (*values)[static_cast<std::size_t>(block.target_register)] =
            StraightLineValue{name, true, initial_value.multiple_results};
        return;
    }

    AssignRegister(lines,
                   values,
                   declared,
                   context.preview_fallback.declarations,
                   context.preview_fallback.function,
                   block.target_register,
                   line,
                   initial_value.text,
                   initial_value.multiple_results);
}

std::string BuildLuaTestSetFallbackCondition(const RegisterDeclarations& declarations,
                                             const Lua51Function& function,
                                             int target_register,
                                             int line,
                                             bool invert_condition) {
    const std::string target_name =
        GetStraightLineRegisterName(declarations, function, target_register, line);
    return invert_condition ? RenderUnaryExpression("not", target_name) : target_name;
}

}  // namespace refii::lua
