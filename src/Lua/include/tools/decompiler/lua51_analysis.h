#pragma once

#include "tools/native_lua_chunk.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

enum class Lua51Opcode : uint8_t {
    kMove = 0,
    kLoadK,
    kLoadBool,
    kLoadNil,
    kGetUpval,
    kGetGlobal,
    kGetTable,
    kSetGlobal,
    kSetUpval,
    kSetTable,
    kNewTable,
    kSelf,
    kAdd,
    kSub,
    kMul,
    kDiv,
    kMod,
    kPow,
    kUnm,
    kNot,
    kLen,
    kConcat,
    kJmp,
    kEq,
    kLt,
    kLe,
    kTest,
    kTestSet,
    kCall,
    kTailCall,
    kReturn,
    kForLoop,
    kForPrep,
    kTForLoop,
    kSetList,
    kClose,
    kClosure,
    kVarArg,
    kUnknown = 0xFF,
};

struct Lua51Instruction {
    Lua51Opcode opcode = Lua51Opcode::kUnknown;
    uint32_t raw = 0;
    uint32_t line = 0;
    int a = 0;
    int b = 0;
    int c = 0;
    int bx = 0;
    int sbx = 0;
};

struct Lua51Declaration {
    std::string name;
    uint32_t begin = 0;
    uint32_t end = 0;
    uint32_t reg = 0;
    bool guessed = false;
    bool temporary = false;
};

struct Lua51DeclarationSummary {
    std::size_t declared_count = 0;
    std::size_t guessed_count = 0;
    std::size_t temporary_register_count = 0;
    std::size_t local_register_count = 0;
};

struct Lua51ControlFlowSummary {
    std::size_t conditional_branch_count = 0;
    std::size_t unconditional_jump_count = 0;
    std::size_t backward_jump_count = 0;
    std::size_t loop_candidate_count = 0;
    std::size_t numeric_for_count = 0;
    std::size_t generic_for_count = 0;
    std::size_t if_block_count = 0;
    std::size_t if_else_block_count = 0;
    std::size_t max_branch_span = 0;
};

enum class Lua51OperationKind : uint8_t {
    kRegisterSet,
    kGlobalSet,
    kUpvalueSet,
    kTableSet,
    kCall,
    kTailCall,
    kReturn,
    kClosure,
    kVarArg,
    kSetList,
    kUnknown,
};

struct Lua51Operation {
    Lua51OperationKind kind = Lua51OperationKind::kUnknown;
    uint32_t line = 0;
    int a = 0;
    int b = 0;
    int c = 0;
    int bx = 0;
    int sbx = 0;
    int child_function = -1;
    bool multiple_arguments = false;
    bool multiple_results = false;
};

struct Lua51OperationSummary {
    std::size_t register_set_count = 0;
    std::size_t global_set_count = 0;
    std::size_t upvalue_set_count = 0;
    std::size_t table_set_count = 0;
    std::size_t call_count = 0;
    std::size_t tailcall_count = 0;
    std::size_t multi_result_call_count = 0;
    std::size_t multi_argument_call_count = 0;
    std::size_t closure_count = 0;
    std::size_t vararg_count = 0;
    std::size_t setlist_count = 0;
    std::size_t return_count = 0;
};

enum class Lua51RegisterValueKind : uint8_t {
    kUnknown,
    kNil,
    kBoolean,
    kConstant,
    kGlobal,
    kUpvalue,
    kTableReference,
    kTableLiteral,
    kClosure,
    kVarArg,
    kFunctionCall,
    kLocalReference,
    kExpression,
};

struct Lua51RegisterSummary {
    std::size_t declared_slot_count = 0;
    std::size_t new_local_count = 0;
    std::size_t nil_assignment_count = 0;
    std::size_t boolean_assignment_count = 0;
    std::size_t constant_assignment_count = 0;
    std::size_t global_assignment_count = 0;
    std::size_t upvalue_assignment_count = 0;
    std::size_t table_reference_assignment_count = 0;
    std::size_t table_literal_assignment_count = 0;
    std::size_t closure_assignment_count = 0;
    std::size_t vararg_assignment_count = 0;
    std::size_t function_call_assignment_count = 0;
    std::size_t local_reference_assignment_count = 0;
    std::size_t expression_assignment_count = 0;
};

Lua51Instruction DecodeLua51Instruction(uint32_t raw, uint32_t line = 0);

std::string_view DescribeLua51Opcode(Lua51Opcode opcode);

std::vector<Lua51Instruction> DecodeLua51Instructions(const Lua51Function& function);

std::vector<Lua51Declaration> InferLua51Declarations(const Lua51Function& function);

Lua51DeclarationSummary SummarizeLua51Declarations(const Lua51Function& function);

std::string DescribeLua51DeclarationSummary(const Lua51DeclarationSummary& summary);

Lua51ControlFlowSummary SummarizeLua51ControlFlow(const Lua51Function& function);

std::string DescribeLua51ControlFlowSummary(const Lua51ControlFlowSummary& summary);

std::vector<Lua51Operation> InferLua51Operations(const Lua51Function& function);

Lua51OperationSummary SummarizeLua51Operations(const Lua51Function& function);

std::string DescribeLua51OperationSummary(const Lua51OperationSummary& summary);

Lua51RegisterSummary SummarizeLua51Registers(const Lua51Function& function);

std::string DescribeLua51RegisterSummary(const Lua51RegisterSummary& summary);

}  // namespace refii::lua
