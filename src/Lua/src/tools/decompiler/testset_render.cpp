#include "tools/decompiler/testset_render.h"

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

void AssignLuaTestSetTarget(std::vector<std::string>* lines,
                            std::vector<StraightLineValue>* values,
                            std::vector<bool>* declared,
                            const LuaTestSetFallbackContext& context,
                            const decompiler::LuaTestSetLoopControlBlock& block,
                            int line,
                            const StraightLineValue& initial_value) {
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
