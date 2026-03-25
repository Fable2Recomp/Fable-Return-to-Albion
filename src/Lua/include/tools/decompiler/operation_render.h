#pragma once

#include "tools/decompiler/straight_line_render.h"

#include <string>
#include <vector>
#include <variant>

namespace refii::lua {

struct LuaOperationRenderContext {
    std::vector<std::string>* lines = nullptr;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
};

struct LuaAssignmentOperation {
    int first_reg = -1;
    int count = 1;
    int line = 0;
    std::string expression;
    bool multiple_results = false;
};

struct LuaValueOperation {
    int reg = -1;
    std::string expression;
    bool multiple_results = false;
};

struct LuaStatementOperation {
    std::string statement;
};

struct LuaSelfOperation {
    int method_reg = -1;
    int object_reg = -1;
    std::string method_expression;
    std::string object_expression;
};

struct LuaGlobalSetOperation {
    std::string global_name;
    std::string expression;
    int source_reg = -1;
    std::string source_expression_override;
    bool source_multiple_results_override = false;
};

struct LuaUpvalueSetOperation {
    std::string upvalue_name;
    std::string expression;
    int source_reg = -1;
    std::string source_expression_override;
    bool source_multiple_results_override = false;
};

struct LuaTableSetOperation {
    std::string table_expression;
    std::string key_expression;
    std::string expression;
    std::string fallback_name;
    bool declare_local = false;
    int target_reg = -1;
    int source_reg = -1;
    std::string source_expression_override;
    bool source_multiple_results_override = false;
};

struct LuaSetListOperation {
    std::string table_expression;
    int base_index = 1;
    std::vector<StraightLineValue> entries;
    bool try_inline_initializer = false;
};

enum class LuaCallResultMode {
    kDiscard,
    kStoreMultiple,
    kStoreSingle,
    kStoreFixed,
};

struct LuaCallOperation {
    int target_reg = -1;
    int fixed_result_count = 0;
    int line = 0;
    std::string expression;
    LuaCallResultMode result_mode = LuaCallResultMode::kDiscard;
    bool tail_return = false;
    int source_reg = -1;
    std::string source_expression_override;
    bool source_multiple_results_override = false;
};

struct LuaReturnOperation {
    std::string expression;
    bool has_values = false;
};

using LuaBuiltOperationPayload =
    std::variant<std::monostate,
                 LuaAssignmentOperation,
                 LuaValueOperation,
                 LuaStatementOperation,
                 LuaSelfOperation,
                 LuaGlobalSetOperation,
                 LuaUpvalueSetOperation,
                 LuaTableSetOperation,
                 LuaSetListOperation,
                 LuaCallOperation,
                 LuaReturnOperation>;

struct LuaBuiltOperation {
    int next_current = -1;
    bool stop_render = false;
    std::vector<LuaValueOperation> post_apply_values;
    LuaBuiltOperationPayload payload;
};

void ApplyLuaAssignmentOperation(const LuaOperationRenderContext& context,
                                 const LuaAssignmentOperation& operation);
void ApplyLuaValueOperation(const LuaOperationRenderContext& context,
                            const LuaValueOperation& operation);
void ApplyLuaStatementOperation(const LuaOperationRenderContext& context,
                                const LuaStatementOperation& operation);
void ApplyLuaSelfOperation(const LuaOperationRenderContext& context,
                           const LuaSelfOperation& operation);
void ApplyLuaGlobalSetOperation(const LuaOperationRenderContext& context,
                                const LuaGlobalSetOperation& operation);
void ApplyLuaUpvalueSetOperation(const LuaOperationRenderContext& context,
                                 const LuaUpvalueSetOperation& operation);
void ApplyLuaTableSetOperation(const LuaOperationRenderContext& context,
                               const LuaTableSetOperation& operation);
void ApplyLuaSetListOperation(const LuaOperationRenderContext& context,
                              const LuaSetListOperation& operation);
bool ApplyLuaCallOperation(const LuaOperationRenderContext& context,
                           const LuaCallOperation& operation);
void ApplyLuaReturnOperation(const LuaOperationRenderContext& context,
                             const LuaReturnOperation& operation);
bool IsLuaBuiltOperationSideEffectFree(const LuaBuiltOperation& operation);
bool TryRenderLuaBuiltOperationStatement(const LuaOperationRenderContext& context,
                                         const LuaBuiltOperation& operation,
                                         std::string* statement);
bool ApplyLuaBuiltOperation(const LuaOperationRenderContext& context,
                            const LuaBuiltOperation& operation);

}  // namespace refii::lua
