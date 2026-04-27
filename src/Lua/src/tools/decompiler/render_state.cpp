#include "tools/decompiler/render_state.h"

#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>

namespace refii::lua {
namespace {

struct LuaSingleLineWriteCache {
    const Lua51Instruction* instructions_data = nullptr;
    std::size_t instruction_count = 0;
    int register_count = 0;
    std::vector<std::vector<bool>> written_by_line;
    std::vector<bool> ready_by_line;
};

LuaSingleLineWriteCache& GetLuaSingleLineWriteCache() {
    static thread_local LuaSingleLineWriteCache cache;
    return cache;
}

bool GetLuaJoinDeclaredState(const std::vector<bool>* declared, std::size_t register_index) {
    return declared != nullptr && register_index < declared->size() && (*declared)[register_index];
}

bool AreEquivalentLuaJoinValues(const StraightLineValue& before,
                                const StraightLineValue& after,
                                const RegisterDeclarations& declarations,
                                const Lua51Function& function,
                                int line,
                                int register_count) {
    if (before.valid != after.valid || before.multiple_results != after.multiple_results) {
        return false;
    }

    if (before.text == after.text) {
        return true;
    }

    if (!before.valid || !after.valid) {
        return false;
    }

    if (before.multiple_results) {
        const int before_identity = ResolveLuaJoinIdentityRegister(
            before.text, declarations, function, line, register_count);
        const int after_identity = ResolveLuaJoinIdentityRegister(
            after.text, declarations, function, line, register_count);
        return before_identity >= 0 && before_identity == after_identity;
    }

    const std::string before_safe = NormalizeLuaJoinSafeExpression(
        before.text, declarations, function, line, register_count);
    const std::string after_safe = NormalizeLuaJoinSafeExpression(
        after.text, declarations, function, line, register_count);
    return !before_safe.empty() && before_safe == after_safe;
}

StraightLineValue CanonicalizeLuaJoinEquivalentValue(const StraightLineValue& value,
                                                     const RegisterDeclarations& declarations,
                                                     const Lua51Function& function,
                                                     int line,
                                                     int register_count) {
    if (!value.valid) {
        return value;
    }

    if (value.multiple_results) {
        const int identity = ResolveLuaJoinIdentityRegister(
            value.text, declarations, function, line, register_count);
        if (identity >= 0) {
            return StraightLineValue{
                GetStraightLineRegisterName(
                    declarations, function, identity, std::max(1, line)),
                true,
                true,
            };
        }
        return value;
    }

    const std::string safe = NormalizeLuaJoinSafeExpression(
        value.text, declarations, function, line, register_count);
    if (!safe.empty()) {
        return StraightLineValue{
            safe,
            true,
            false,
        };
    }

    return value;
}

bool HasLuaJoinRegisterIdentity(const RegisterDeclarations& declarations,
                                const Lua51Function& function,
                                int line,
                                int reg,
                                bool declared) {
    return reg < function.parameter_count ||
           declared ||
           declarations.Get(reg, std::max(1, line)) != nullptr;
}

bool RepresentsLuaJoinRegisterIdentity(const StraightLineValue& value,
                                       const RegisterDeclarations& declarations,
                                       const Lua51Function& function,
                                       int line,
                                       int register_count,
                                       int reg) {
    if (!value.valid) {
        return false;
    }

    if (value.multiple_results) {
        return ResolveLuaJoinIdentityRegister(
                   value.text, declarations, function, line, register_count) == reg;
    }

    return NormalizeLuaJoinSafeExpression(
               value.text, declarations, function, line, register_count) ==
           GetStraightLineRegisterName(
               declarations, function, reg, std::max(1, line));
}

bool AreEquivalentLuaJoinRegisterStates(const StraightLineValue& before,
                                        bool before_has_identity,
                                        const StraightLineValue& after,
                                        bool after_has_identity,
                                        const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int line,
                                        int register_count,
                                        int reg) {
    if (AreEquivalentLuaJoinValues(
            before, after, declarations, function, line, register_count)) {
        return true;
    }

    const bool before_identity =
        (!before.valid && before_has_identity) ||
        RepresentsLuaJoinRegisterIdentity(
            before, declarations, function, line, register_count, reg);
    const bool after_identity =
        (!after.valid && after_has_identity) ||
        RepresentsLuaJoinRegisterIdentity(
            after, declarations, function, line, register_count, reg);
    return before_identity && after_identity;
}

bool AreEquivalentLuaJoinSlotStates(const StraightLineValue& before,
                                    bool before_declared,
                                    const StraightLineValue& after,
                                    bool after_declared,
                                    const RegisterDeclarations& declarations,
                                    const Lua51Function& function,
                                    int line,
                                    int register_count,
                                    int reg) {
    if (before_declared != after_declared) {
        return false;
    }

    return AreEquivalentLuaJoinRegisterStates(
        before,
        HasLuaJoinRegisterIdentity(declarations, function, line, reg, before_declared),
        after,
        HasLuaJoinRegisterIdentity(declarations, function, line, reg, after_declared),
        declarations,
        function,
        line,
        register_count,
        reg);
}

StraightLineValue BuildEquivalentLuaJoinMergedValue(const StraightLineValue& original_value,
                                                    bool original_declared,
                                                    const StraightLineValue& fallback_value,
                                                    bool fallback_declared,
                                                    const RegisterDeclarations& declarations,
                                                    const Lua51Function& function,
                                                    int line,
                                                    int register_count,
                                                    int preferred_reg) {
    if (original_value.valid) {
        return CanonicalizeLuaJoinEquivalentValue(
            original_value, declarations, function, line, register_count);
    }
    if (fallback_value.valid) {
        return CanonicalizeLuaJoinEquivalentValue(
            fallback_value, declarations, function, line, register_count);
    }

    if (HasLuaJoinRegisterIdentity(
            declarations, function, line, preferred_reg, original_declared) ||
        HasLuaJoinRegisterIdentity(
            declarations, function, line, preferred_reg, fallback_declared)) {
        return StraightLineValue{
            GetStraightLineRegisterName(
                declarations, function, preferred_reg, std::max(1, line)),
            true,
            false,
        };
    }

    return StraightLineValue{};
}

StraightLineValue BuildLuaJoinLogicalOperandValue(const StraightLineValue& value,
                                                  bool declared,
                                                  const RegisterDeclarations& declarations,
                                                  const Lua51Function& function,
                                                  int line,
                                                  int register_count,
                                                  int preferred_reg) {
    if (value.valid) {
        return CanonicalizeLuaJoinEquivalentValue(
            value, declarations, function, line, register_count);
    }

    if (HasLuaJoinRegisterIdentity(
            declarations, function, line, preferred_reg, declared)) {
        return StraightLineValue{
            GetStraightLineRegisterName(
                declarations, function, preferred_reg, std::max(1, line)),
            true,
            false,
        };
    }

    return StraightLineValue{};
}

bool InstructionWritesLuaRegister(const Lua51Instruction& instruction,
                                  int reg,
                                  int register_count) {
    const auto writes_range = [&](int first, int last) {
        return reg >= first && reg <= last && reg < register_count;
    };

    switch (instruction.opcode) {
        case Lua51Opcode::kMove:
        case Lua51Opcode::kLoadK:
        case Lua51Opcode::kLoadBool:
        case Lua51Opcode::kGetUpval:
        case Lua51Opcode::kGetGlobal:
        case Lua51Opcode::kGetTable:
        case Lua51Opcode::kNewTable:
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
        case Lua51Opcode::kClosure:
            return reg == instruction.a;
        case Lua51Opcode::kLoadNil:
            return writes_range(instruction.a, instruction.b);
        case Lua51Opcode::kSelf:
            return reg == instruction.a || reg == instruction.a + 1;
        case Lua51Opcode::kCall:
            if (instruction.c == 0) {
                return writes_range(instruction.a, register_count - 1);
            }
            if (instruction.c > 1) {
                return writes_range(instruction.a, instruction.a + instruction.c - 2);
            }
            return false;
        case Lua51Opcode::kVarArg:
            if (instruction.b == 0) {
                return writes_range(instruction.a, register_count - 1);
            }
            if (instruction.b > 1) {
                return writes_range(instruction.a, instruction.a + instruction.b - 2);
            }
            return false;
        case Lua51Opcode::kForPrep:
        case Lua51Opcode::kForLoop:
            return writes_range(instruction.a, instruction.a + 3);
        case Lua51Opcode::kTForLoop:
            return writes_range(instruction.a + 3, instruction.a + 2 + instruction.c);
        default:
            return false;
    }
}

std::vector<bool> UnionLuaWrittenRegisters(const std::vector<bool>& first_written_registers,
                                           const std::vector<bool>& second_written_registers,
                                           int register_count) {
    std::vector<bool> written_registers(static_cast<std::size_t>(register_count), false);
    const std::size_t register_limit = std::min<std::size_t>(
        written_registers.size(),
        std::max(first_written_registers.size(), second_written_registers.size()));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        const bool first_written =
            register_index < first_written_registers.size() &&
            first_written_registers[register_index];
        const bool second_written =
            register_index < second_written_registers.size() &&
            second_written_registers[register_index];
        written_registers[register_index] = first_written || second_written;
    }
    return written_registers;
}

std::vector<bool> CollectLuaChangedValueRegistersFromStates(
    const LuaBranchStateSnapshot& base_state,
    const LuaBranchStateSnapshot& branch_state,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    int line,
    int register_count) {
    std::vector<bool> changed_registers(static_cast<std::size_t>(register_count), false);
    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(base_state.values.size(),
                 std::min(branch_state.values.size(),
                          std::min(base_state.declared.size(), branch_state.declared.size()))));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        const StraightLineValue& before = base_state.values[register_index];
        const StraightLineValue& after = branch_state.values[register_index];
        if (AreEquivalentLuaJoinRegisterStates(
                before,
                HasLuaJoinRegisterIdentity(declarations,
                                           function,
                                           line,
                                           static_cast<int>(register_index),
                                           base_state.declared[register_index]),
                after,
                HasLuaJoinRegisterIdentity(declarations,
                                           function,
                                           line,
                                           static_cast<int>(register_index),
                                           branch_state.declared[register_index]),
                declarations,
                function,
                line,
                register_count,
                static_cast<int>(register_index))) {
            continue;
        }

        changed_registers[register_index] = true;
    }
    return changed_registers;
}

std::vector<bool> CollectLuaChangedDeclaredRegistersFromStates(
    const LuaBranchStateSnapshot& base_state,
    const LuaBranchStateSnapshot& branch_state,
    int register_count) {
    std::vector<bool> changed_registers(static_cast<std::size_t>(register_count), false);
    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(base_state.declared.size(), branch_state.declared.size()));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        if (base_state.declared[register_index] != branch_state.declared[register_index]) {
            changed_registers[register_index] = true;
        }
    }
    return changed_registers;
}

}  // namespace

void EmitLuaNewLocalDeclarations(std::vector<std::string>* lines,
                                 std::vector<StraightLineValue>* values,
                                 std::vector<bool>* declared,
                                 const LuaRenderStateContext& context,
                                 int pc) {
    if (lines == nullptr || values == nullptr || declared == nullptr) {
        return;
    }

    const int lookup_line = std::max(1, pc);
    const Lua51Instruction& current_instruction =
        context.instructions[static_cast<std::size_t>(lookup_line - 1)];

    for (int reg = context.function.parameter_count; reg < context.register_count; ++reg) {
        if ((*declared)[static_cast<std::size_t>(reg)]) {
            continue;
        }
        const Lua51Declaration* declaration = context.declarations.Get(reg, lookup_line);
        if (declaration == nullptr || declaration->temporary || declaration->guessed) {
            continue;
        }
        if (!context.declarations.IsNewLocal(reg, lookup_line) ||
            InstructionWritesLuaRegister(current_instruction, reg, context.register_count) ||
            !IsValidLuaIdentifier(declaration->name)) {
            continue;
        }

        AppendIndentedStatement(lines, "  ", RenderLocalDeclarationStatement(declaration->name));
        (*declared)[static_cast<std::size_t>(reg)] = true;
    }
}

void InvalidateLuaRenderValues(std::vector<StraightLineValue>* values,
                               const std::vector<bool>& declared,
                               const LuaRenderStateContext& context,
                               int line) {
    if (values == nullptr) {
        return;
    }

    const int lookup_line = std::max(1, line);
    for (int reg = 0; reg < context.register_count; ++reg) {
        StraightLineValue& value = (*values)[static_cast<std::size_t>(reg)];
        const bool preserve_name =
            reg < context.function.parameter_count ||
            declared[static_cast<std::size_t>(reg)] ||
            context.declarations.Get(reg, lookup_line) != nullptr;
        if (preserve_name) {
            value.text =
                GetStraightLineRegisterName(context.declarations, context.function, reg, lookup_line);
            value.valid = true;
            value.multiple_results = false;
        } else {
            value.valid = false;
            value.multiple_results = false;
            value.text.clear();
        }
    }
}

std::vector<bool> CollectWrittenLuaRegistersInRange(const std::vector<Lua51Instruction>& instructions,
                                                    int register_count,
                                                    int start_line,
                                                    int end_line) {
    if (start_line > end_line) {
        return std::vector<bool>(static_cast<std::size_t>(register_count), false);
    }

    if (start_line == end_line &&
        start_line >= 1 &&
        static_cast<std::size_t>(start_line) <= instructions.size()) {
        LuaSingleLineWriteCache& cache = GetLuaSingleLineWriteCache();
        if (cache.instructions_data != instructions.data() ||
            cache.instruction_count != instructions.size() ||
            cache.register_count != register_count) {
            cache.instructions_data = instructions.data();
            cache.instruction_count = instructions.size();
            cache.register_count = register_count;
            cache.written_by_line.clear();
            cache.ready_by_line.clear();
        }

        const std::size_t cache_index = static_cast<std::size_t>(start_line);
        if (cache.ready_by_line.size() <= cache_index) {
            cache.ready_by_line.resize(cache_index + 1, false);
        }
        if (cache.written_by_line.size() <= cache_index) {
            cache.written_by_line.resize(cache_index + 1);
        }
        if (cache.ready_by_line[cache_index]) {
            return cache.written_by_line[cache_index];
        }
    }

    std::vector<bool> written(static_cast<std::size_t>(register_count), false);

    const auto mark = [&](int reg) {
        if (reg >= 0 && reg < register_count) {
            written[static_cast<std::size_t>(reg)] = true;
        }
    };
    const auto mark_range = [&](int first, int last) {
        for (int reg = first; reg <= last && reg < register_count; ++reg) {
            mark(reg);
        }
    };

    for (int pc = std::max(1, start_line);
         pc <= end_line && static_cast<std::size_t>(pc) <= instructions.size();
         ++pc) {
        const Lua51Instruction& candidate = instructions[static_cast<std::size_t>(pc - 1)];
        switch (candidate.opcode) {
            case Lua51Opcode::kMove:
            case Lua51Opcode::kLoadK:
            case Lua51Opcode::kLoadBool:
            case Lua51Opcode::kGetUpval:
            case Lua51Opcode::kGetGlobal:
            case Lua51Opcode::kGetTable:
            case Lua51Opcode::kNewTable:
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
            case Lua51Opcode::kClosure:
                mark(candidate.a);
                break;
            case Lua51Opcode::kLoadNil:
                mark_range(candidate.a, candidate.b);
                break;
            case Lua51Opcode::kSelf:
                mark(candidate.a);
                mark(candidate.a + 1);
                break;
            case Lua51Opcode::kCall:
                if (candidate.c == 0) {
                    mark_range(candidate.a, register_count - 1);
                } else if (candidate.c > 1) {
                    mark_range(candidate.a, candidate.a + candidate.c - 2);
                }
                break;
            case Lua51Opcode::kVarArg:
                if (candidate.b == 0) {
                    mark_range(candidate.a, register_count - 1);
                } else if (candidate.b > 1) {
                    mark_range(candidate.a, candidate.a + candidate.b - 2);
                }
                break;
            case Lua51Opcode::kForPrep:
            case Lua51Opcode::kForLoop:
                mark_range(candidate.a, candidate.a + 3);
                break;
            case Lua51Opcode::kTForLoop:
                mark_range(candidate.a + 3, candidate.a + 2 + candidate.c);
                break;
            default:
                break;
        }
    }

    if (start_line == end_line &&
        start_line >= 1 &&
        static_cast<std::size_t>(start_line) <= instructions.size()) {
        LuaSingleLineWriteCache& cache = GetLuaSingleLineWriteCache();
        const std::size_t cache_index = static_cast<std::size_t>(start_line);
        if (cache.ready_by_line.size() <= cache_index) {
            cache.ready_by_line.resize(cache_index + 1, false);
        }
        if (cache.written_by_line.size() <= cache_index) {
            cache.written_by_line.resize(cache_index + 1);
        }
        cache.written_by_line[cache_index] = written;
        cache.ready_by_line[cache_index] = true;
    }

    return written;
}

void ClearLuaWrittenJoinState(std::vector<StraightLineValue>* values,
                              std::vector<bool>* declared,
                              const std::vector<bool>& written_registers,
                              int register_count) {
    if (values == nullptr || declared == nullptr) {
        return;
    }

    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(values->size(), std::min(declared->size(), written_registers.size())));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        if (!written_registers[register_index]) {
            continue;
        }

        (*values)[register_index] = StraightLineValue{};
        (*declared)[register_index] = false;
    }
}

bool TryMergeLuaPreviewRegisterFallback(const std::vector<StraightLineValue>& original_values,
                                        const std::vector<bool>* original_declared,
                                        const std::vector<StraightLineValue>& fallback_values,
                                        const std::vector<bool>* fallback_declared,
                                        const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int line,
                                        int register_count,
                                        int preferred_reg,
                                        bool require_temporary_declaration,
                                        StraightLineValue* merged_value) {
    if (merged_value == nullptr || preferred_reg < 0 ||
        static_cast<std::size_t>(preferred_reg) >= original_values.size() ||
        static_cast<std::size_t>(preferred_reg) >= fallback_values.size()) {
        return false;
    }

    const std::size_t register_limit = std::min(original_values.size(), fallback_values.size());
    int changed_register = -1;
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        const StraightLineValue& before = original_values[register_index];
        const StraightLineValue& after = fallback_values[register_index];
        const bool before_declared = GetLuaJoinDeclaredState(original_declared, register_index);
        const bool after_declared = GetLuaJoinDeclaredState(fallback_declared, register_index);
        if (AreEquivalentLuaJoinSlotStates(before,
                                           before_declared,
                                           after,
                                           after_declared,
                                           declarations,
                                           function,
                                           line,
                                           register_count,
                                           static_cast<int>(register_index))) {
            continue;
        }
        if (changed_register != -1 ||
            static_cast<int>(register_index) != preferred_reg) {
            return false;
        }
        changed_register = static_cast<int>(register_index);
    }

    if (changed_register != preferred_reg) {
        return false;
    }

    const Lua51Declaration* declaration = declarations.Get(preferred_reg, line);
    if (require_temporary_declaration &&
        declaration != nullptr &&
        !declaration->temporary &&
        !declaration->guessed) {
        return false;
    }

    const StraightLineValue& original_value =
        original_values[static_cast<std::size_t>(preferred_reg)];
    const bool original_is_declared =
        GetLuaJoinDeclaredState(original_declared, static_cast<std::size_t>(preferred_reg));
    const StraightLineValue& fallback_value =
        fallback_values[static_cast<std::size_t>(preferred_reg)];
    const bool fallback_is_declared =
        GetLuaJoinDeclaredState(fallback_declared, static_cast<std::size_t>(preferred_reg));

    if (AreEquivalentLuaJoinSlotStates(
            original_value,
            original_is_declared,
            fallback_value,
            fallback_is_declared,
            declarations,
            function,
            line,
            register_count,
            preferred_reg)) {
        *merged_value = BuildEquivalentLuaJoinMergedValue(original_value,
                                                         original_is_declared,
                                                         fallback_value,
                                                         fallback_is_declared,
                                                         declarations,
                                                         function,
                                                         line,
                                                         register_count,
                                                         preferred_reg);
        return merged_value->valid;
    }

    const StraightLineValue original_operand = BuildLuaJoinLogicalOperandValue(
        original_value,
        original_is_declared,
        declarations,
        function,
        line,
        register_count,
        preferred_reg);
    const StraightLineValue fallback_operand = BuildLuaJoinLogicalOperandValue(
        fallback_value,
        fallback_is_declared,
        declarations,
        function,
        line,
        register_count,
        preferred_reg);
    if (!original_operand.valid || !fallback_operand.valid) {
        return false;
    }

    *merged_value = StraightLineValue{
        RenderLogicalExpression(original_operand.text, "or", fallback_operand.text),
        true,
        false,
    };
    return true;
}

void MergeLuaJoinDeclaredState(std::vector<bool>* declared,
                               const std::vector<bool>& first_branch_declared,
                               const std::vector<bool>& second_branch_declared,
                               const std::vector<bool>& candidate_registers,
                               int register_count) {
    if (declared == nullptr) {
        return;
    }

    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(candidate_registers.size(),
                 std::min(first_branch_declared.size(), second_branch_declared.size())));

    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        if (!candidate_registers[register_index]) {
            continue;
        }
        if (first_branch_declared[register_index] != second_branch_declared[register_index]) {
            continue;
        }

        (*declared)[register_index] = first_branch_declared[register_index];
    }
}

void MergeLuaJoinSafeValues(std::vector<StraightLineValue>* values,
                            std::vector<bool>* declared,
                            const std::vector<StraightLineValue>& first_branch_values,
                            const std::vector<bool>& first_branch_declared,
                            const std::vector<StraightLineValue>& second_branch_values,
                            const std::vector<bool>& second_branch_declared,
                            const RegisterDeclarations& declarations,
                            const Lua51Function& function,
                            int line,
                            const std::vector<bool>& candidate_registers,
                            int register_count) {
    if (values == nullptr || declared == nullptr) {
        return;
    }

    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(first_branch_values.size(),
                 std::min(second_branch_values.size(),
                          std::min(first_branch_declared.size(),
                                   std::min(second_branch_declared.size(),
                                            candidate_registers.size())))));

    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        if (!candidate_registers[register_index]) {
            continue;
        }

        const StraightLineValue& first_value = first_branch_values[register_index];
        const StraightLineValue& second_value = second_branch_values[register_index];
        if (first_branch_declared[register_index] != second_branch_declared[register_index]) {
            continue;
        }

        const StraightLineValue equivalent_merged_value =
            BuildEquivalentLuaJoinMergedValue(first_value,
                                             first_branch_declared[register_index],
                                             second_value,
                                             second_branch_declared[register_index],
                                             declarations,
                                             function,
                                             line,
                                             register_count,
                                             static_cast<int>(register_index));
        if (AreEquivalentLuaJoinSlotStates(first_value,
                                           first_branch_declared[register_index],
                                           second_value,
                                           second_branch_declared[register_index],
                                           declarations,
                                           function,
                                           line,
                                           register_count,
                                           static_cast<int>(register_index)) &&
            equivalent_merged_value.valid &&
            (equivalent_merged_value.multiple_results ||
             IsSafeLuaJoinPreservation(equivalent_merged_value.text))) {
            (*values)[register_index] = equivalent_merged_value;
            (*declared)[register_index] = first_branch_declared[register_index];
            continue;
        }

        if (!first_value.valid || !second_value.valid) {
            continue;
        }
    }
}

void CanonicalizeLuaJoinCandidateValues(std::vector<StraightLineValue>* values,
                                        const std::vector<bool>& candidate_registers,
                                        const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int line,
                                        int register_count) {
    if (values == nullptr) {
        return;
    }

    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count), std::min(values->size(), candidate_registers.size()));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        if (!candidate_registers[register_index] || !(*values)[register_index].valid) {
            continue;
        }
        (*values)[register_index] = CanonicalizeLuaJoinEquivalentValue(
            (*values)[register_index], declarations, function, line, register_count);
    }
}

void DropLuaJoinDeadBranchLocals(std::vector<StraightLineValue>* values,
                                 std::vector<bool>* declared,
                                 const std::vector<StraightLineValue>& saved_values,
                                 const std::vector<bool>& saved_declared,
                                 const std::vector<bool>& written_registers,
                                 const RegisterDeclarations& declarations,
                                 const Lua51Function& function,
                                 int line,
                                 int register_count) {
    if (values == nullptr || declared == nullptr) {
        return;
    }

    const int lookup_line = std::max(1, line);
    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(values->size(),
                 std::min(declared->size(),
                          std::min(saved_values.size(),
                                   std::min(saved_declared.size(), written_registers.size())))));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        const int reg = static_cast<int>(register_index);
        if (reg < function.parameter_count || !written_registers[register_index] ||
            saved_declared[register_index] ||
            declarations.Get(reg, lookup_line) != nullptr) {
            continue;
        }

        (*declared)[register_index] = false;
        if (!saved_values[register_index].valid) {
            (*values)[register_index].valid = false;
            (*values)[register_index].multiple_results = false;
            (*values)[register_index].text.clear();
        } else {
            (*values)[register_index] = saved_values[register_index];
        }
    }
}

void FinalizeLuaJoinCandidateState(std::vector<StraightLineValue>* values,
                                   std::vector<bool>* declared,
                                   const std::vector<StraightLineValue>& saved_values,
                                   const std::vector<bool>& saved_declared,
                                   const std::vector<bool>& written_registers,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int line,
                                   int register_count) {
    if (values == nullptr || declared == nullptr) {
        return;
    }

    RestoreUnmodifiedSafeLuaValues(values,
                                   declared,
                                   saved_values,
                                   saved_declared,
                                   written_registers,
                                   declarations,
                                   function,
                                   line,
                                   register_count);
    RestoreLuaJoinRegisterIdentities(values,
                                     *declared,
                                     written_registers,
                                     declarations,
                                     function,
                                     line,
                                     register_count);
    CanonicalizeLuaJoinCandidateValues(values,
                                       written_registers,
                                       declarations,
                                       function,
                                       line,
                                       register_count);
    DropLuaJoinDeadBranchLocals(values,
                                declared,
                                saved_values,
                                saved_declared,
                                written_registers,
                                declarations,
                                function,
                                line,
                                register_count);
}

void RestoreLuaJoinRegisterIdentities(std::vector<StraightLineValue>* values,
                                      const std::vector<bool>& declared,
                                      const std::vector<bool>& written_registers,
                                      const RegisterDeclarations& declarations,
                                      const Lua51Function& function,
                                      int line,
                                      int register_count) {
    if (values == nullptr) {
        return;
    }

    const int lookup_line = std::max(1, line);
    const std::size_t register_limit = std::min<std::size_t>(
        static_cast<std::size_t>(register_count),
        std::min(values->size(), std::min(declared.size(), written_registers.size())));
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        if (!written_registers[register_index] || (*values)[register_index].valid) {
            continue;
        }

        const int reg = static_cast<int>(register_index);
        const bool has_identity =
            reg < function.parameter_count || declared[register_index] ||
            declarations.Get(reg, lookup_line) != nullptr;
        if (!has_identity) {
            continue;
        }

        (*values)[register_index] = StraightLineValue{
            GetStraightLineRegisterName(declarations, function, reg, lookup_line),
            true,
            false,
        };
    }
}

LuaMergedBranchState BuildLuaMergedBranchState(
    const LuaBranchStateSnapshot& base_state,
    const LuaBranchStateSnapshot& first_branch_state,
    const std::vector<bool>& first_written_registers,
    const LuaBranchStateSnapshot& second_branch_state,
    const std::vector<bool>& second_written_registers,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    int line,
    int register_count) {
    const std::vector<bool> first_effective_written_registers = UnionLuaWrittenRegisters(
        first_written_registers,
        CollectLuaChangedValueRegistersFromStates(base_state,
                                                 first_branch_state,
                                                 declarations,
                                                 function,
                                                 line,
                                                 register_count),
        register_count);
    const std::vector<bool> second_effective_written_registers = UnionLuaWrittenRegisters(
        second_written_registers,
        CollectLuaChangedValueRegistersFromStates(base_state,
                                                 second_branch_state,
                                                 declarations,
                                                 function,
                                                 line,
                                                 register_count),
        register_count);
    const std::vector<bool> declaration_candidate_registers = UnionLuaWrittenRegisters(
        UnionLuaWrittenRegisters(first_effective_written_registers,
                                 second_effective_written_registers,
                                 register_count),
        UnionLuaWrittenRegisters(
            CollectLuaChangedDeclaredRegistersFromStates(base_state,
                                                         first_branch_state,
                                                         register_count),
            CollectLuaChangedDeclaredRegistersFromStates(base_state,
                                                         second_branch_state,
                                                         register_count),
            register_count),
        register_count);
    LuaMergedBranchState merged_state{
        .values = base_state.values,
        .declared = base_state.declared,
        .written_registers = UnionLuaWrittenRegisters(
            first_effective_written_registers, second_effective_written_registers, register_count),
    };

    ClearLuaWrittenJoinState(&merged_state.values,
                             &merged_state.declared,
                             merged_state.written_registers,
                             register_count);
    FinalizeLuaJoinCandidateState(&merged_state.values,
                                  &merged_state.declared,
                                  base_state.values,
                                  base_state.declared,
                                  merged_state.written_registers,
                                  declarations,
                                  function,
                                  line,
                                  register_count);
    MergeLuaJoinDeclaredState(&merged_state.declared,
                              first_branch_state.declared,
                              second_branch_state.declared,
                              declaration_candidate_registers,
                              register_count);
    MergeLuaJoinSafeValues(&merged_state.values,
                           &merged_state.declared,
                           first_branch_state.values,
                           first_branch_state.declared,
                           second_branch_state.values,
                           second_branch_state.declared,
                           declarations,
                           function,
                           line,
                           merged_state.written_registers,
                           register_count);
    FinalizeLuaJoinCandidateState(&merged_state.values,
                                  &merged_state.declared,
                                  merged_state.values,
                                  merged_state.declared,
                                  merged_state.written_registers,
                                  declarations,
                                  function,
                                  line,
                                  register_count);
    return merged_state;
}

void RestoreUnmodifiedSafeLuaValues(std::vector<StraightLineValue>* values,
                                    std::vector<bool>* declared,
                                    const std::vector<StraightLineValue>& saved_values,
                                    const std::vector<bool>& saved_declared,
                                    const std::vector<bool>& written_registers,
                                    const RegisterDeclarations& declarations,
                                    const Lua51Function& function,
                                    int line,
                                    int register_count) {
    if (values == nullptr || declared == nullptr) {
        return;
    }

    const int lookup_line = std::max(1, line);
    for (int reg = 0; reg < register_count; ++reg) {
        if (written_registers[static_cast<std::size_t>(reg)]) {
            continue;
        }
        const bool has_identity =
            reg < function.parameter_count ||
            saved_declared[static_cast<std::size_t>(reg)] ||
            declarations.Get(reg, lookup_line) != nullptr;
        const StraightLineValue& saved_value = saved_values[static_cast<std::size_t>(reg)];
        if (!saved_value.valid) {
            if (!has_identity) {
                continue;
            }

            (*values)[static_cast<std::size_t>(reg)] = StraightLineValue{
                GetStraightLineRegisterName(
                    declarations, function, reg, lookup_line),
                true,
                false,
            };
            (*declared)[static_cast<std::size_t>(reg)] =
                saved_declared[static_cast<std::size_t>(reg)];
            continue;
        }

        if (saved_value.multiple_results) {
            const int identity = ResolveLuaJoinIdentityRegister(
                saved_value.text, declarations, function, line, register_count);
            if (identity >= 0) {
                (*values)[static_cast<std::size_t>(reg)] = StraightLineValue{
                    GetStraightLineRegisterName(
                        declarations, function, identity, std::max(1, line)),
                    true,
                    true,
                };
                (*declared)[static_cast<std::size_t>(reg)] =
                    saved_declared[static_cast<std::size_t>(reg)];
                continue;
            }
            if (!has_identity) {
                continue;
            }

            (*values)[static_cast<std::size_t>(reg)] = StraightLineValue{
                GetStraightLineRegisterName(
                    declarations, function, reg, lookup_line),
                true,
                true,
            };
            (*declared)[static_cast<std::size_t>(reg)] =
                saved_declared[static_cast<std::size_t>(reg)];
            continue;
        }

        const std::string canonical_saved = NormalizeLuaJoinSafeExpression(
            saved_value.text, declarations, function, line, register_count);
        if (canonical_saved.empty()) {
            if (!has_identity) {
                continue;
            }

            (*values)[static_cast<std::size_t>(reg)] = StraightLineValue{
                GetStraightLineRegisterName(
                    declarations, function, reg, lookup_line),
                true,
                false,
            };
            (*declared)[static_cast<std::size_t>(reg)] =
                saved_declared[static_cast<std::size_t>(reg)];
            continue;
        }

        (*values)[static_cast<std::size_t>(reg)] = StraightLineValue{
            canonical_saved,
            true,
            false,
        };
        (*declared)[static_cast<std::size_t>(reg)] = saved_declared[static_cast<std::size_t>(reg)];
    }
}

bool LastRenderedStatementIsTerminal(const std::vector<std::string>& lines,
                                     std::size_t start_line_count) {
    if (start_line_count >= lines.size()) {
        return false;
    }
    for (std::size_t line_index = lines.size(); line_index > start_line_count; --line_index) {
        const std::string_view trimmed = TrimLuaWhitespace(lines[line_index - 1]);
        if (trimmed.empty()) {
            continue;
        }
        return trimmed == "break" || trimmed == "return" || trimmed.starts_with("return ");
    }
    return false;
}

}  // namespace refii::lua
