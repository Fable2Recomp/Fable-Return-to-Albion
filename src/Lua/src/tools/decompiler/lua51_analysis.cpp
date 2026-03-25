#include "tools/decompiler/lua51_analysis.h"

#include "tools/decompiler/straight_line_render.h"

#include <algorithm>
#include <sstream>
#include <utility>

namespace refii::lua {
namespace {

constexpr int kLua51OpcodeCount = 38;
constexpr int kLua51MaxArgumentBX = (1 << 18) - 1;
constexpr int kLua51MaxStack = 250;

bool IsConditionalOpcode(Lua51Opcode opcode) {
    switch (opcode) {
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe:
        case Lua51Opcode::kTest:
        case Lua51Opcode::kTestSet:
            return true;
        default:
            return false;
    }
}

std::size_t ClampTargetLine(int target, std::size_t length) {
    if (target < 1) {
        return 1;
    }
    if (static_cast<std::size_t>(target) > length) {
        return length;
    }
    return static_cast<std::size_t>(target);
}

std::size_t ComputeJumpTarget(int pc, int sbx, std::size_t length) {
    return ClampTargetLine(pc + 1 + sbx, length);
}

void AppendDeclarationSummaryRecursive(const Lua51Function& function, Lua51DeclarationSummary* summary) {
    const std::vector<Lua51Declaration> declarations = InferLua51Declarations(function);
    for (const Lua51Declaration& declaration : declarations) {
        ++summary->declared_count;
        if (declaration.guessed) {
            ++summary->guessed_count;
        }
        if (declaration.temporary) {
            ++summary->temporary_register_count;
        }
    }

    for (const Lua51Function& child : function.child_functions) {
        AppendDeclarationSummaryRecursive(child, summary);
    }
}

void AppendControlFlowSummaryRecursive(const Lua51Function& function, Lua51ControlFlowSummary* summary) {
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    const std::size_t length = instructions.size();

    for (std::size_t index = 0; index < length; ++index) {
        const Lua51Instruction& instruction = instructions[index];

        if (instruction.opcode == Lua51Opcode::kForPrep) {
            ++summary->numeric_for_count;
        } else if (instruction.opcode == Lua51Opcode::kTForLoop) {
            ++summary->generic_for_count;
        } else if (instruction.opcode == Lua51Opcode::kJmp) {
            ++summary->unconditional_jump_count;
            const std::size_t pc = index + 1;
            const std::size_t target =
                ComputeJumpTarget(static_cast<int>(pc), instruction.sbx, length);
            const std::size_t span = target > pc ? target - pc : pc - target;
            summary->max_branch_span = std::max(summary->max_branch_span, span);
            if (target <= pc) {
                ++summary->backward_jump_count;
                ++summary->loop_candidate_count;
            }
        }

        if (!IsConditionalOpcode(instruction.opcode)) {
            continue;
        }

        ++summary->conditional_branch_count;
        ++summary->if_block_count;

        if (index + 1 >= length || instructions[index + 1].opcode != Lua51Opcode::kJmp) {
            continue;
        }

        const Lua51Instruction& jump = instructions[index + 1];
        const std::size_t condition_pc = index + 1;
        const std::size_t exit_target =
            ComputeJumpTarget(static_cast<int>(index + 2), jump.sbx, length);
        const std::size_t span =
            exit_target > condition_pc ? exit_target - condition_pc : condition_pc - exit_target;
        summary->max_branch_span = std::max(summary->max_branch_span, span);

        if (exit_target <= condition_pc) {
            ++summary->loop_candidate_count;
            continue;
        }

        bool has_else_candidate = false;
        for (std::size_t branch_index = index + 2;
             branch_index < exit_target && branch_index < length;
             ++branch_index) {
            const Lua51Instruction& branch_instruction = instructions[branch_index];
            if (branch_instruction.opcode != Lua51Opcode::kJmp) {
                continue;
            }

            const std::size_t branch_target = ComputeJumpTarget(
                static_cast<int>(branch_index + 1), branch_instruction.sbx, length);
            if (branch_target > exit_target) {
                has_else_candidate = true;
                break;
            }
        }

        if (has_else_candidate) {
            ++summary->if_else_block_count;
        }
    }

    for (const Lua51Function& child : function.child_functions) {
        AppendControlFlowSummaryRecursive(child, summary);
    }
}

void AppendOperationSummaryRecursive(const Lua51Function& function, Lua51OperationSummary* summary) {
    const std::vector<Lua51Operation> operations = InferLua51Operations(function);
    for (const Lua51Operation& operation : operations) {
        switch (operation.kind) {
            case Lua51OperationKind::kRegisterSet:
                ++summary->register_set_count;
                break;
            case Lua51OperationKind::kGlobalSet:
                ++summary->global_set_count;
                break;
            case Lua51OperationKind::kUpvalueSet:
                ++summary->upvalue_set_count;
                break;
            case Lua51OperationKind::kTableSet:
                ++summary->table_set_count;
                break;
            case Lua51OperationKind::kCall:
                ++summary->call_count;
                if (operation.multiple_arguments) {
                    ++summary->multi_argument_call_count;
                }
                if (operation.multiple_results) {
                    ++summary->multi_result_call_count;
                }
                break;
            case Lua51OperationKind::kTailCall:
                ++summary->tailcall_count;
                if (operation.multiple_arguments) {
                    ++summary->multi_argument_call_count;
                }
                if (operation.multiple_results) {
                    ++summary->multi_result_call_count;
                }
                break;
            case Lua51OperationKind::kReturn:
                ++summary->return_count;
                break;
            case Lua51OperationKind::kClosure:
                ++summary->closure_count;
                break;
            case Lua51OperationKind::kVarArg:
                ++summary->vararg_count;
                break;
            case Lua51OperationKind::kSetList:
                ++summary->setlist_count;
                break;
            case Lua51OperationKind::kUnknown:
                break;
        }
    }

    for (const Lua51Function& child : function.child_functions) {
        AppendOperationSummaryRecursive(child, summary);
    }
}

void AccumulateRegisterSummary(const Lua51RegisterSummary& other, Lua51RegisterSummary* summary) {
    summary->declared_slot_count += other.declared_slot_count;
    summary->new_local_count += other.new_local_count;
    summary->nil_assignment_count += other.nil_assignment_count;
    summary->boolean_assignment_count += other.boolean_assignment_count;
    summary->constant_assignment_count += other.constant_assignment_count;
    summary->global_assignment_count += other.global_assignment_count;
    summary->upvalue_assignment_count += other.upvalue_assignment_count;
    summary->table_reference_assignment_count += other.table_reference_assignment_count;
    summary->table_literal_assignment_count += other.table_literal_assignment_count;
    summary->closure_assignment_count += other.closure_assignment_count;
    summary->vararg_assignment_count += other.vararg_assignment_count;
    summary->function_call_assignment_count += other.function_call_assignment_count;
    summary->local_reference_assignment_count += other.local_reference_assignment_count;
    summary->expression_assignment_count += other.expression_assignment_count;
}

Lua51RegisterSummary SummarizeLua51RegistersSingle(const Lua51Function& function) {
    Lua51RegisterSummary summary;

    const int registers =
        static_cast<int>(std::min<std::size_t>(function.max_stack_size, kLua51MaxStack));
    const int lines = static_cast<int>(function.code.size());
    std::vector<Lua51Declaration> declarations = InferLua51Declarations(function);
    summary.declared_slot_count = declarations.size();
    if (registers <= 0 || lines <= 0) {
        return summary;
    }

    RegisterDeclarations declaration_map(registers, lines, &declarations);
    RegisterValues values(registers, lines, &declaration_map);
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);

    const auto assign = [&](int reg, int line, RegisterValue value) {
        values.SetValue(reg, line, value);
        CountRegisterAssignment(value, &summary);
    };

    for (int line = 1; line <= lines; ++line) {
        values.StartLine(line);
        for (int reg = 0; reg < registers; ++reg) {
            if (declaration_map.IsNewLocal(reg, line)) {
                ++summary.new_local_count;
            }
        }

        const Lua51Instruction& instruction = instructions[static_cast<std::size_t>(line - 1)];
        switch (instruction.opcode) {
            case Lua51Opcode::kMove:
                assign(instruction.a, line, values.GetExpression(instruction.b, line));
                break;
            case Lua51Opcode::kLoadK:
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kConstant));
                break;
            case Lua51Opcode::kLoadBool:
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kBoolean));
                break;
            case Lua51Opcode::kLoadNil:
                for (int reg = instruction.a; reg <= instruction.b && reg < registers; ++reg) {
                    assign(reg, line, MakeValue(Lua51RegisterValueKind::kNil));
                }
                break;
            case Lua51Opcode::kGetUpval:
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kUpvalue));
                break;
            case Lua51Opcode::kGetGlobal:
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kGlobal));
                break;
            case Lua51Opcode::kGetTable:
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kTableReference));
                break;
            case Lua51Opcode::kNewTable:
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kTableLiteral));
                break;
            case Lua51Opcode::kSelf:
                if (instruction.a + 1 < registers) {
                    assign(instruction.a + 1, line, values.GetExpression(instruction.b, line));
                }
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kTableReference));
                break;
            case Lua51Opcode::kAdd:
            case Lua51Opcode::kSub:
            case Lua51Opcode::kMul:
            case Lua51Opcode::kDiv:
            case Lua51Opcode::kMod:
            case Lua51Opcode::kPow:
            case Lua51Opcode::kUnm:
            case Lua51Opcode::kNot:
            case Lua51Opcode::kLen:
            case Lua51Opcode::kConcat:
                assign(instruction.a, line, MakeValue(Lua51RegisterValueKind::kExpression));
                break;
            case Lua51Opcode::kTestSet:
                assign(instruction.a, line, values.GetExpression(instruction.b, line));
                break;
            case Lua51Opcode::kCall: {
                int c = instruction.c;
                if (c == 0) {
                    c = registers - instruction.a + 1;
                }
                if (c != 1) {
                    RegisterValue call = MakeValue(Lua51RegisterValueKind::kFunctionCall);
                    call.source_register = instruction.a;
                    call.multiple_results = instruction.c == 0 || instruction.c > 2;
                    const int result_count = std::max(0, c - 1);
                    for (int reg = instruction.a;
                         reg <= instruction.a + result_count - 1 && reg < registers;
                         ++reg) {
                        assign(reg, line, call);
                    }
                }
                break;
            }
            case Lua51Opcode::kVarArg: {
                int b = instruction.b;
                if (b == 0) {
                    b = registers - instruction.a + 1;
                }
                RegisterValue value = MakeValue(Lua51RegisterValueKind::kVarArg);
                value.multiple_results = instruction.b == 0 || instruction.b > 2;
                const int result_count = std::max(0, b - 1);
                for (int reg = instruction.a;
                     reg <= instruction.a + result_count - 1 && reg < registers;
                     ++reg) {
                    assign(reg, line, value);
                }
                break;
            }
            case Lua51Opcode::kClosure: {
                RegisterValue value = MakeValue(Lua51RegisterValueKind::kClosure);
                value.child_function = instruction.bx;
                assign(instruction.a, line, value);
                break;
            }
            default:
                break;
        }
    }

    return summary;
}

}  // namespace

Lua51Instruction DecodeLua51Instruction(uint32_t raw, uint32_t line) {
    Lua51Instruction instruction;
    instruction.raw = raw;
    instruction.line = line;
    instruction.a = static_cast<int>((raw >> 6) & 0xFF);
    instruction.c = static_cast<int>((raw >> 14) & 0x1FF);
    instruction.b = static_cast<int>((raw >> 23) & 0x1FF);
    instruction.bx = static_cast<int>((raw >> 14) & 0x3FFFF);
    instruction.sbx = instruction.bx - kLua51MaxArgumentBX / 2;

    const uint8_t op = static_cast<uint8_t>(raw & 0x3F);
    if (op < kLua51OpcodeCount) {
        instruction.opcode = static_cast<Lua51Opcode>(op);
    }
    return instruction;
}

std::string_view DescribeLua51Opcode(Lua51Opcode opcode) {
    switch (opcode) {
        case Lua51Opcode::kMove:
            return "MOVE";
        case Lua51Opcode::kLoadK:
            return "LOADK";
        case Lua51Opcode::kLoadBool:
            return "LOADBOOL";
        case Lua51Opcode::kLoadNil:
            return "LOADNIL";
        case Lua51Opcode::kGetUpval:
            return "GETUPVAL";
        case Lua51Opcode::kGetGlobal:
            return "GETGLOBAL";
        case Lua51Opcode::kGetTable:
            return "GETTABLE";
        case Lua51Opcode::kSetGlobal:
            return "SETGLOBAL";
        case Lua51Opcode::kSetUpval:
            return "SETUPVAL";
        case Lua51Opcode::kSetTable:
            return "SETTABLE";
        case Lua51Opcode::kNewTable:
            return "NEWTABLE";
        case Lua51Opcode::kSelf:
            return "SELF";
        case Lua51Opcode::kAdd:
            return "ADD";
        case Lua51Opcode::kSub:
            return "SUB";
        case Lua51Opcode::kMul:
            return "MUL";
        case Lua51Opcode::kDiv:
            return "DIV";
        case Lua51Opcode::kMod:
            return "MOD";
        case Lua51Opcode::kPow:
            return "POW";
        case Lua51Opcode::kUnm:
            return "UNM";
        case Lua51Opcode::kNot:
            return "NOT";
        case Lua51Opcode::kLen:
            return "LEN";
        case Lua51Opcode::kConcat:
            return "CONCAT";
        case Lua51Opcode::kJmp:
            return "JMP";
        case Lua51Opcode::kEq:
            return "EQ";
        case Lua51Opcode::kLt:
            return "LT";
        case Lua51Opcode::kLe:
            return "LE";
        case Lua51Opcode::kTest:
            return "TEST";
        case Lua51Opcode::kTestSet:
            return "TESTSET";
        case Lua51Opcode::kCall:
            return "CALL";
        case Lua51Opcode::kTailCall:
            return "TAILCALL";
        case Lua51Opcode::kReturn:
            return "RETURN";
        case Lua51Opcode::kForLoop:
            return "FORLOOP";
        case Lua51Opcode::kForPrep:
            return "FORPREP";
        case Lua51Opcode::kTForLoop:
            return "TFORLOOP";
        case Lua51Opcode::kSetList:
            return "SETLIST";
        case Lua51Opcode::kClose:
            return "CLOSE";
        case Lua51Opcode::kClosure:
            return "CLOSURE";
        case Lua51Opcode::kVarArg:
            return "VARARG";
        case Lua51Opcode::kUnknown:
            return "UNKNOWN";
    }
    return "UNKNOWN";
}

std::vector<Lua51Instruction> DecodeLua51Instructions(const Lua51Function& function) {
    std::vector<Lua51Instruction> instructions;
    instructions.reserve(function.code.size());
    for (std::size_t i = 0; i < function.code.size(); ++i) {
        instructions.push_back(DecodeLua51Instruction(function.code[i], static_cast<uint32_t>(i + 1)));
    }
    return instructions;
}

std::vector<Lua51Declaration> InferLua51Declarations(const Lua51Function& function) {
    std::vector<Lua51Declaration> declarations;
    if (!function.locals.empty()) {
        declarations.reserve(function.locals.size());
        for (uint32_t i = 0; i < function.locals.size(); ++i) {
            Lua51Declaration declaration;
            declaration.name = function.locals[i].name;
            declaration.begin = function.locals[i].start_pc;
            declaration.end = function.locals[i].end_pc;
            declaration.guessed = false;
            declarations.push_back(std::move(declaration));
        }
        RegisterDeclarations declaration_map(
            static_cast<int>(std::min<std::size_t>(function.max_stack_size, kLua51MaxStack)),
            static_cast<int>(function.code.size()),
            &declarations);
        (void)declaration_map;
        return declarations;
    }

    const int registers = static_cast<int>(std::min<std::size_t>(function.max_stack_size, kLua51MaxStack));
    const int lines = static_cast<int>(function.code.size());
    if (registers <= 0 || lines <= 0) {
        return declarations;
    }

    RegisterStates states(registers, lines);
    std::vector<bool> skip(static_cast<std::size_t>(lines), false);
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);

    for (int line = 1; line <= lines; ++line) {
        if (skip[static_cast<std::size_t>(line - 1)]) {
            continue;
        }

        const Lua51Instruction& instruction = instructions[static_cast<std::size_t>(line - 1)];
        switch (instruction.opcode) {
            case Lua51Opcode::kMove:
                states.Get(instruction.a, line).written = true;
                states.Get(instruction.b, line).read = true;
                states.SetLocal(std::min(instruction.a, instruction.b), line);
                break;
            case Lua51Opcode::kLoadK:
            case Lua51Opcode::kLoadBool:
            case Lua51Opcode::kGetUpval:
            case Lua51Opcode::kGetGlobal:
            case Lua51Opcode::kNewTable:
                states.Get(instruction.a, line).written = true;
                break;
            case Lua51Opcode::kLoadNil:
                for (int reg = instruction.a; reg <= instruction.b && reg < registers; ++reg) {
                    states.Get(reg, line).written = true;
                }
                break;
            case Lua51Opcode::kGetTable:
                states.Get(instruction.a, line).written = true;
                if (!IsConstantReference(instruction.b) && instruction.b < registers) {
                    states.Get(instruction.b, line).read = true;
                }
                if (!IsConstantReference(instruction.c) && instruction.c < registers) {
                    states.Get(instruction.c, line).read = true;
                }
                break;
            case Lua51Opcode::kSetGlobal:
            case Lua51Opcode::kSetUpval:
                if (instruction.a < registers) {
                    states.Get(instruction.a, line).read = true;
                }
                break;
            case Lua51Opcode::kSetTable:
            case Lua51Opcode::kAdd:
            case Lua51Opcode::kSub:
            case Lua51Opcode::kMul:
            case Lua51Opcode::kDiv:
            case Lua51Opcode::kMod:
            case Lua51Opcode::kPow:
                if (instruction.a < registers) {
                    states.Get(instruction.a, line).read = true;
                }
                if (!IsConstantReference(instruction.b) && instruction.b < registers) {
                    states.Get(instruction.b, line).read = true;
                }
                if (!IsConstantReference(instruction.c) && instruction.c < registers) {
                    states.Get(instruction.c, line).read = true;
                }
                break;
            case Lua51Opcode::kSelf:
                if (instruction.a < registers) {
                    states.Get(instruction.a, line).written = true;
                }
                if (instruction.a + 1 < registers) {
                    states.Get(instruction.a + 1, line).written = true;
                }
                if (instruction.b < registers) {
                    states.Get(instruction.b, line).read = true;
                }
                if (!IsConstantReference(instruction.c) && instruction.c < registers) {
                    states.Get(instruction.c, line).read = true;
                }
                break;
            case Lua51Opcode::kUnm:
            case Lua51Opcode::kNot:
            case Lua51Opcode::kLen:
                if (instruction.a < registers) {
                    states.Get(instruction.a, line).written = true;
                }
                if (instruction.b < registers) {
                    states.Get(instruction.b, line).read = true;
                }
                break;
            case Lua51Opcode::kConcat:
                if (instruction.a < registers) {
                    states.Get(instruction.a, line).written = true;
                }
                for (int reg = instruction.b; reg <= instruction.c && reg < registers; ++reg) {
                    states.Get(reg, line).read = true;
                    states.SetTemporary(reg, line);
                }
                break;
            case Lua51Opcode::kSetList:
                if (instruction.a + 1 < registers) {
                    states.SetTemporary(instruction.a + 1, line);
                }
                break;
            case Lua51Opcode::kEq:
            case Lua51Opcode::kLt:
            case Lua51Opcode::kLe:
                if (!IsConstantReference(instruction.b) && instruction.b < registers) {
                    states.Get(instruction.b, line).read = true;
                }
                if (!IsConstantReference(instruction.c) && instruction.c < registers) {
                    states.Get(instruction.c, line).read = true;
                }
                break;
            case Lua51Opcode::kTest:
                if (instruction.a < registers) {
                    states.Get(instruction.a, line).read = true;
                }
                break;
            case Lua51Opcode::kTestSet:
                if (instruction.a < registers) {
                    states.Get(instruction.a, line).written = true;
                }
                if (instruction.b < registers) {
                    states.Get(instruction.b, line).read = true;
                }
                break;
            case Lua51Opcode::kClosure:
                for (const Lua51Function& child : function.child_functions) {
                    for (const std::string& upvalue_name : child.upvalue_names) {
                        (void)upvalue_name;
                    }
                }
                break;
            case Lua51Opcode::kCall:
            case Lua51Opcode::kTailCall: {
                int b = instruction.b;
                int c = instruction.c;
                if (instruction.opcode != Lua51Opcode::kTailCall) {
                    if (c >= 2) {
                        for (int reg = instruction.a;
                             reg <= instruction.a + c - 2 && reg < registers;
                             ++reg) {
                            states.Get(reg, line).written = true;
                        }
                    }
                }
                for (int reg = instruction.a;
                     reg <= instruction.a + b - 1 && reg < registers;
                     ++reg) {
                    (void)reg;
                    states.Get(instruction.a, line).read = true;
                    states.SetTemporary(instruction.a, line);
                }
                if (c >= 2) {
                    int next_line = line + 1;
                    int reg = instruction.a + c - 2;
                    while (reg >= instruction.a && next_line <= lines) {
                        const Lua51Instruction& next_instruction =
                            instructions[static_cast<std::size_t>(next_line - 1)];
                        if (next_instruction.opcode == Lua51Opcode::kMove &&
                            next_instruction.b == reg) {
                            states.Get(next_instruction.a, next_line).written = true;
                            states.Get(next_instruction.b, next_line).read = true;
                            states.SetLocal(next_instruction.a, next_line);
                            skip[static_cast<std::size_t>(next_line - 1)] = true;
                        }
                        --reg;
                        ++next_line;
                    }
                }
                break;
            }
            default:
                break;
        }
    }

    uint32_t synthetic_index = 0;
    for (int reg = 0; reg < registers; ++reg) {
        bool local = reg < function.parameter_count;
        bool temporary = false;
        int read_count = 0;
        int write_count = 0;

        if (!local) {
            for (int line = 1; line <= lines; ++line) {
                const RegisterState& state = states.Get(reg, line);
                local = local || state.local;
                temporary = temporary || state.temporary;
                read_count += state.read ? 1 : 0;
                write_count += state.written ? 1 : 0;
            }
        }

        if (!local && !temporary && (read_count >= 2 || read_count == 0)) {
            local = true;
        }

        if (!local) {
            continue;
        }

        Lua51Declaration declaration;
        declaration.name = MakeDeclarationName(reg < function.parameter_count ? 'A' : 'L',
                                              static_cast<uint32_t>(reg),
                                              synthetic_index++);
        declaration.begin = 0;
        declaration.end = static_cast<uint32_t>(function.code.size());
        declaration.reg = static_cast<uint32_t>(reg);
        declaration.guessed = true;
        declaration.temporary = temporary;
        declarations.push_back(std::move(declaration));
    }

    return declarations;
}

std::vector<Lua51Operation> InferLua51Operations(const Lua51Function& function) {
    std::vector<Lua51Operation> operations;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    operations.reserve(instructions.size());

    for (const Lua51Instruction& instruction : instructions) {
        Lua51Operation operation;
        operation.line = instruction.line;
        operation.a = instruction.a;
        operation.b = instruction.b;
        operation.c = instruction.c;
        operation.bx = instruction.bx;
        operation.sbx = instruction.sbx;

        switch (instruction.opcode) {
            case Lua51Opcode::kMove:
            case Lua51Opcode::kLoadK:
            case Lua51Opcode::kLoadBool:
            case Lua51Opcode::kLoadNil:
            case Lua51Opcode::kGetUpval:
            case Lua51Opcode::kGetGlobal:
            case Lua51Opcode::kGetTable:
            case Lua51Opcode::kNewTable:
            case Lua51Opcode::kSelf:
            case Lua51Opcode::kAdd:
            case Lua51Opcode::kSub:
            case Lua51Opcode::kMul:
            case Lua51Opcode::kDiv:
            case Lua51Opcode::kMod:
            case Lua51Opcode::kPow:
            case Lua51Opcode::kUnm:
            case Lua51Opcode::kNot:
            case Lua51Opcode::kLen:
            case Lua51Opcode::kConcat:
            case Lua51Opcode::kTestSet:
                operation.kind = Lua51OperationKind::kRegisterSet;
                break;
            case Lua51Opcode::kSetGlobal:
                operation.kind = Lua51OperationKind::kGlobalSet;
                break;
            case Lua51Opcode::kSetUpval:
                operation.kind = Lua51OperationKind::kUpvalueSet;
                break;
            case Lua51Opcode::kSetTable:
                operation.kind = Lua51OperationKind::kTableSet;
                break;
            case Lua51Opcode::kCall:
                operation.kind = Lua51OperationKind::kCall;
                operation.multiple_arguments = instruction.b == 0 || instruction.b > 2;
                operation.multiple_results = instruction.c == 0 || instruction.c > 2;
                break;
            case Lua51Opcode::kTailCall:
                operation.kind = Lua51OperationKind::kTailCall;
                operation.multiple_arguments = instruction.b == 0 || instruction.b > 2;
                operation.multiple_results = true;
                break;
            case Lua51Opcode::kReturn:
                operation.kind = Lua51OperationKind::kReturn;
                operation.multiple_results = instruction.b == 0 || instruction.b > 2;
                break;
            case Lua51Opcode::kClosure:
                operation.kind = Lua51OperationKind::kClosure;
                operation.child_function = instruction.bx;
                break;
            case Lua51Opcode::kVarArg:
                operation.kind = Lua51OperationKind::kVarArg;
                operation.multiple_results = instruction.b == 0 || instruction.b > 2;
                break;
            case Lua51Opcode::kSetList:
                operation.kind = Lua51OperationKind::kSetList;
                break;
            default:
                operation.kind = Lua51OperationKind::kUnknown;
                break;
        }

        if (operation.kind != Lua51OperationKind::kUnknown) {
            operations.push_back(std::move(operation));
        }
    }

    return operations;
}

Lua51DeclarationSummary SummarizeLua51Declarations(const Lua51Function& function) {
    Lua51DeclarationSummary summary;
    AppendDeclarationSummaryRecursive(function, &summary);
    summary.local_register_count = summary.declared_count - summary.temporary_register_count;
    return summary;
}

std::string DescribeLua51DeclarationSummary(const Lua51DeclarationSummary& summary) {
    std::ostringstream stream;
    stream << "chunk_declared_variables=" << summary.declared_count
           << ";chunk_guessed_variables=" << summary.guessed_count
           << ";chunk_temporary_variables=" << summary.temporary_register_count
           << ";chunk_non_temporary_variables=" << summary.local_register_count;
    return stream.str();
}

Lua51ControlFlowSummary SummarizeLua51ControlFlow(const Lua51Function& function) {
    Lua51ControlFlowSummary summary;
    AppendControlFlowSummaryRecursive(function, &summary);
    return summary;
}

std::string DescribeLua51ControlFlowSummary(const Lua51ControlFlowSummary& summary) {
    std::ostringstream stream;
    stream << "chunk_conditional_branches=" << summary.conditional_branch_count
           << ";chunk_unconditional_jumps=" << summary.unconditional_jump_count
           << ";chunk_backward_jumps=" << summary.backward_jump_count
           << ";chunk_loop_candidates=" << summary.loop_candidate_count
           << ";chunk_numeric_for_loops=" << summary.numeric_for_count
           << ";chunk_generic_for_loops=" << summary.generic_for_count
           << ";chunk_if_blocks=" << summary.if_block_count
           << ";chunk_if_else_blocks=" << summary.if_else_block_count
           << ";chunk_max_branch_span=" << summary.max_branch_span;
    return stream.str();
}

Lua51OperationSummary SummarizeLua51Operations(const Lua51Function& function) {
    Lua51OperationSummary summary;
    AppendOperationSummaryRecursive(function, &summary);
    return summary;
}

std::string DescribeLua51OperationSummary(const Lua51OperationSummary& summary) {
    std::ostringstream stream;
    stream << "chunk_register_sets=" << summary.register_set_count
           << ";chunk_global_sets=" << summary.global_set_count
           << ";chunk_upvalue_sets=" << summary.upvalue_set_count
           << ";chunk_table_sets=" << summary.table_set_count
           << ";chunk_calls=" << summary.call_count
           << ";chunk_tailcalls=" << summary.tailcall_count
           << ";chunk_multi_result_calls=" << summary.multi_result_call_count
           << ";chunk_multi_argument_calls=" << summary.multi_argument_call_count
           << ";chunk_closures=" << summary.closure_count
           << ";chunk_varargs=" << summary.vararg_count
           << ";chunk_setlists=" << summary.setlist_count
           << ";chunk_returns=" << summary.return_count;
    return stream.str();
}

Lua51RegisterSummary SummarizeLua51Registers(const Lua51Function& function) {
    Lua51RegisterSummary summary;
    const auto append =
        [&](const auto& self, const Lua51Function& current) -> void {
            AccumulateRegisterSummary(SummarizeLua51RegistersSingle(current), &summary);
            for (const Lua51Function& child : current.child_functions) {
                self(self, child);
            }
        };
    append(append, function);
    return summary;
}

std::string DescribeLua51RegisterSummary(const Lua51RegisterSummary& summary) {
    std::ostringstream stream;
    stream << "chunk_register_declared_slots=" << summary.declared_slot_count
           << ";chunk_register_new_locals=" << summary.new_local_count
           << ";chunk_register_nil_assignments=" << summary.nil_assignment_count
           << ";chunk_register_boolean_assignments=" << summary.boolean_assignment_count
           << ";chunk_register_constant_assignments=" << summary.constant_assignment_count
           << ";chunk_register_global_assignments=" << summary.global_assignment_count
           << ";chunk_register_upvalue_assignments=" << summary.upvalue_assignment_count
           << ";chunk_register_table_reference_assignments="
           << summary.table_reference_assignment_count
           << ";chunk_register_table_literal_assignments="
           << summary.table_literal_assignment_count
           << ";chunk_register_closure_assignments=" << summary.closure_assignment_count
           << ";chunk_register_vararg_assignments=" << summary.vararg_assignment_count
           << ";chunk_register_call_assignments=" << summary.function_call_assignment_count
           << ";chunk_register_local_reference_assignments="
           << summary.local_reference_assignment_count
           << ";chunk_register_expression_assignments=" << summary.expression_assignment_count;
    return stream.str();
}

}  // namespace refii::lua
