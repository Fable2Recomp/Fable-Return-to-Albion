#include "tools/decompiler/loop_statement_render.h"

namespace refii::lua {

bool PrepareLuaGenericForStatement(const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int register_count,
                                   const decompiler::LuaGenericForLoopBlock& block,
                                   std::string_view call_expression,
                                   std::vector<StraightLineValue>* values,
                                   std::vector<bool>* declared,
                                   LuaLoopBindingState* binding,
                                   std::string* statement,
                                   std::string* error) {
    if (values == nullptr || declared == nullptr || binding == nullptr || statement == nullptr) {
        if (error != nullptr) {
            *error = "generic_for_statement_output_missing";
        }
        return false;
    }

    binding->saved_values = *values;
    binding->saved_declared = *declared;
    std::vector<std::string> loop_names;
    const int loop_name_line = block.body_start;
    for (int reg = block.first_loop_register; reg <= block.last_loop_register && reg < register_count;
         ++reg) {
        const std::string name =
            GetStraightLineRegisterName(declarations, function, reg, loop_name_line);
        loop_names.push_back(name);
        (*declared)[static_cast<std::size_t>(reg)] = true;
        (*values)[static_cast<std::size_t>(reg)] = StraightLineValue{name, true, false};
    }
    *statement = RenderGenericForStatementHeader(loop_names, call_expression);
    return true;
}

bool PrepareLuaNumericForStatement(const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int register_count,
                                   const decompiler::LuaNumericForLoopBlock& block,
                                   std::string_view initial,
                                   std::string_view limit,
                                   std::string_view step,
                                   std::vector<StraightLineValue>* values,
                                   std::vector<bool>* declared,
                                   LuaLoopBindingState* binding,
                                   std::string* statement,
                                   std::string* error) {
    if (values == nullptr || declared == nullptr || binding == nullptr || statement == nullptr) {
        if (error != nullptr) {
            *error = "numeric_for_statement_output_missing";
        }
        return false;
    }

    const int loop_var_reg = block.loop_var_register;
    if (loop_var_reg < 0 || loop_var_reg >= register_count) {
        if (error != nullptr) {
            *error = "unsupported_forloop_register";
        }
        return false;
    }

    binding->saved_values = *values;
    binding->saved_declared = *declared;
    const std::string loop_var =
        GetStraightLineRegisterName(declarations, function, loop_var_reg, block.body_start);
    *statement = RenderNumericForStatementHeader(loop_var, initial, limit, step);

    (*declared)[static_cast<std::size_t>(loop_var_reg)] = true;
    (*values)[static_cast<std::size_t>(loop_var_reg)] = StraightLineValue{loop_var, true, false};
    return true;
}

void RestoreLuaLoopBindingState(std::vector<StraightLineValue>* values,
                                std::vector<bool>* declared,
                                LuaLoopBindingState* binding) {
    if (values == nullptr || declared == nullptr || binding == nullptr) {
        return;
    }
    *values = std::move(binding->saved_values);
    *declared = std::move(binding->saved_declared);
}

std::string BuildLuaRepeatFooter(std::string_view condition_text) {
    return RenderRepeatFooter(condition_text);
}

}  // namespace refii::lua
