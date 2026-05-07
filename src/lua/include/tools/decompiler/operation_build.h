#pragma once

#include "tools/decompiler/operation_render.h"
#include "tools/decompiler/statement_render.h"

#include <cstddef>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

using LuaOperationCallArgumentsRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaOperationReturnValuesRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaOperationSetListValuesRenderer =
    std::function<std::vector<StraightLineValue>(const std::vector<StraightLineValue>&,
                                                 int,
                                                 int,
                                                 int)>;
using LuaOperationChildRenderer = std::function<bool(int, std::string*)>;
using LuaOperationSetListBlockResolver =
    std::function<int(int, const Lua51Instruction&, int*, std::string*)>;
using LuaOperationTrace = std::function<void(std::string_view)>;

struct LuaOperationBuildContext {
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    std::vector<StraightLineValue>* values = nullptr;
    const std::vector<bool>* declared = nullptr;
    LuaOperationCallArgumentsRenderer call_arguments_for;
    LuaOperationReturnValuesRenderer return_values_for;
    LuaOperationSetListValuesRenderer setlist_values_for;
    LuaOperationChildRenderer render_child;
    LuaOperationSetListBlockResolver resolve_setlist_block;
    bool trace_render = false;
    LuaOperationTrace trace;
    bool allow_opaque_preview_closures = false;
    bool allow_cross_line_shortcuts = true;
    bool allow_opaque_nested_closure_fallback = false;
};

bool TryBuildLuaNewTableShortcutOperation(const LuaOperationBuildContext& context,
                                          int current,
                                          int line,
                                          LuaAssignmentOperation* assignment,
                                          int* next_current);

bool TryBuildLuaAssignmentOperation(const LuaOperationBuildContext& context,
                                    const Lua51Instruction& instruction,
                                    int line,
                                    std::string* error,
                                    LuaAssignmentOperation* assignment);

bool TryBuildLuaValueOperation(const LuaOperationBuildContext& context,
                               const Lua51Instruction& instruction,
                               int line,
                               LuaValueOperation* value_operation);

bool TryBuildLuaSelfOperation(const LuaOperationBuildContext& context,
                              const Lua51Instruction& instruction,
                              int line,
                              LuaSelfOperation* self_operation);

bool TryBuildLuaCallOperation(const LuaOperationBuildContext& context,
                              int current,
                              int line,
                              LuaCallOperation* call_operation);

bool TryBuildLuaReturnOperation(const LuaOperationBuildContext& context,
                                int start_line,
                                int end_line,
                                int current,
                                int line,
                                LuaReturnOperation* return_operation,
                                bool* suppress_return);

bool TryBuildLuaGlobalSetOperation(const LuaOperationBuildContext& context,
                                   const Lua51Instruction& instruction,
                                   int line,
                                   std::string* error,
                                   LuaGlobalSetOperation* operation);

bool TryBuildLuaUpvalueSetOperation(const LuaOperationBuildContext& context,
                                    const Lua51Instruction& instruction,
                                    int line,
                                    LuaUpvalueSetOperation* operation);

bool TryBuildLuaTableSetOperation(const LuaOperationBuildContext& context,
                                  const Lua51Instruction& instruction,
                                  int line,
                                  LuaTableSetOperation* operation);

bool TryBuildLuaSetListOperation(const LuaOperationBuildContext& context,
                                 int current,
                                 int line,
                                 std::string* error,
                                 LuaSetListOperation* operation,
                                 int* next_current,
                                 bool* emit_whole_table_assignment,
                                 std::string* whole_table_assignment);

bool TryBuildLuaBuiltOperation(const LuaOperationBuildContext& context,
                               int start_line,
                               int end_line,
                               int current,
                               int line,
                               std::string* error,
                               LuaBuiltOperation* operation);

void ResetLuaNativeTableConstructorCounters();
std::size_t ConsumeLuaNativeTableConstructorCommitCounter();
std::size_t ConsumeLuaNativeSetListConstructorLinkCounter();
std::size_t ConsumeLuaNativeTableConstructorFallbackCounter();

}  // namespace refii::lua
