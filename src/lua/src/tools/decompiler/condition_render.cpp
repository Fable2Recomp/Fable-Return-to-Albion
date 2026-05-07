#include "tools/decompiler/condition_render.h"

#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/render_state.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <string>
#include <unordered_map>

namespace refii::lua {
namespace {

const LuaScriptProfile& GetLuaConditionOwnershipProfile(const Lua51Function* function_hint) {
    static const LuaScriptProfile kEmptyProfile = {};
    if (function_hint == nullptr) {
        return kEmptyProfile;
    }
    return GetLuaScriptProfileCached(*function_hint);
}

enum LuaScopedOwnershipMask : unsigned {
    kLuaScopedOwnershipNone = 0u,
    kLuaScopedOwnershipLarge = 1u << 0,
    kLuaScopedOwnershipSmall = 1u << 1,
    kLuaScopedOwnershipPriority = 1u << 2,
    kLuaScopedOwnershipAiCoreHotspot = 1u << 3,
    kLuaScopedOwnershipBehavior = 1u << 4,
};

unsigned GetLuaScopedOwnershipMask(const Lua51Function* function_hint) {
    if (function_hint == nullptr || function_hint->source_name.empty()) {
        return kLuaScopedOwnershipNone;
    }

    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local unsigned last_mask = kLuaScopedOwnershipNone;
    if (function_hint == last_function) {
        return last_mask;
    }

    static thread_local std::unordered_map<const Lua51Function*, unsigned> cached_masks;
    const auto cached = cached_masks.find(function_hint);
    if (cached != cached_masks.end()) {
        last_function = function_hint;
        last_mask = cached->second;
        return last_mask;
    }

    const LuaScriptProfile& profile = GetLuaConditionOwnershipProfile(function_hint);
    unsigned mask = kLuaScopedOwnershipNone;
    if (profile.Has(LuaScriptProfileFlag::kScopedLargeHoldout)) {
        mask |= kLuaScopedOwnershipLarge;
    }
    if (profile.Has(LuaScriptProfileFlag::kScopedSmallHoldout)) {
        mask |= kLuaScopedOwnershipSmall;
    }
    if (profile.Has(LuaScriptProfileFlag::kPriorityOwnership)) {
        mask |= kLuaScopedOwnershipPriority;
    }
    if (profile.Has(LuaScriptProfileFlag::kAiCoreHotspot)) {
        mask |= kLuaScopedOwnershipAiCoreHotspot;
    }
    if (profile.Has(LuaScriptProfileFlag::kBehavior)) {
        mask |= kLuaScopedOwnershipBehavior;
    }

    cached_masks.emplace(function_hint, mask);
    last_function = function_hint;
    last_mask = mask;
    return last_mask;
}

bool IsScopedLargeHoldoutOwnershipFunction(const Lua51Function* function_hint) {
    return (GetLuaScopedOwnershipMask(function_hint) & kLuaScopedOwnershipLarge) != 0;
}

bool IsScopedSmallHoldoutOwnershipFunction(const Lua51Function* function_hint) {
    return (GetLuaScopedOwnershipMask(function_hint) & kLuaScopedOwnershipSmall) != 0;
}

bool IsAiCoreHotspotOwnershipFunction(const Lua51Function* function_hint) {
    return (GetLuaScopedOwnershipMask(function_hint) & kLuaScopedOwnershipAiCoreHotspot) != 0;
}

bool IsBehaviorOwnershipFunction(const Lua51Function* function_hint) {
    return (GetLuaScopedOwnershipMask(function_hint) & kLuaScopedOwnershipBehavior) != 0;
}

bool ShouldEnableScopedLargeBodyOwnership(const Lua51Function* function_hint,
                                          int instruction_count) {
    if (!IsScopedLargeHoldoutOwnershipFunction(function_hint)) {
        return false;
    }
    // Keep the large-holdout lane strict and bounded to avoid speculative scans
    // outside the two allowlisted heavy scripts.
    constexpr int kScopedLargeHoldoutInstructionMin = 1200;
    constexpr int kScopedLargeHoldoutInstructionMax = 4600;
    return instruction_count >= kScopedLargeHoldoutInstructionMin &&
           instruction_count <= kScopedLargeHoldoutInstructionMax;
}

bool ShouldEnableScopedSmallBodyOwnership(const Lua51Function* function_hint,
                                          int instruction_count) {
    if (!IsScopedSmallHoldoutOwnershipFunction(function_hint)) {
        return false;
    }
    constexpr int kScopedSmallHoldoutInstructionBudget = 2048;
    if (instruction_count > kScopedSmallHoldoutInstructionBudget) {
        return false;
    }
    if (function_hint != nullptr) {
        const LuaScriptProfile& profile = GetLuaConditionOwnershipProfile(function_hint);
        if (profile.Has(LuaScriptProfileFlag::kInteractiveCutsceneActions)) {
            return instruction_count >= 400 && instruction_count <= 512;
        }
    }
    return instruction_count >= 400 && instruction_count <= 760;
}

bool ShouldEnableScopedConsumedSpanOwnership(const Lua51Function* function_hint,
                                             int instruction_count) {
    return ShouldEnableScopedSmallBodyOwnership(function_hint, instruction_count) ||
           ShouldEnableScopedLargeBodyOwnership(function_hint, instruction_count);
}

bool ShouldEnablePriorityBodyOwnership(const Lua51Function* function_hint,
                                       int instruction_count) {
    if (!IsAiCoreHotspotOwnershipFunction(function_hint)) {
        return false;
    }
    const bool quest_hotspot =
        function_hint != nullptr &&
        GetLuaConditionOwnershipProfile(function_hint).Has(LuaScriptProfileFlag::kQuestHotspot);
    constexpr int kPriorityInstructionMin = 300;
    constexpr int kPriorityInstructionMax = 2300;
    constexpr int kQuestHotspotInstructionMax = 3200;
    return instruction_count >= kPriorityInstructionMin &&
           instruction_count <=
               (quest_hotspot ? kQuestHotspotInstructionMax
                              : kPriorityInstructionMax);
}

bool ShouldEnableBehaviorBodyOwnership(const Lua51Function* function_hint,
                                       int instruction_count) {
    if (!IsBehaviorOwnershipFunction(function_hint)) {
        return false;
    }
    constexpr int kBehaviorInstructionMin = 120;
    constexpr int kBehaviorInstructionMax = 1536;
    return instruction_count >= kBehaviorInstructionMin &&
           instruction_count <= kBehaviorInstructionMax;
}

bool ShouldEnableConditionalChainOwnership(const Lua51Function* function_hint,
                                           int instruction_count) {
    if (ShouldEnableScopedConsumedSpanOwnership(function_hint, instruction_count)) {
        return true;
    }
    if (ShouldEnablePriorityBodyOwnership(function_hint, instruction_count)) {
        return true;
    }
    if (ShouldEnableBehaviorBodyOwnership(function_hint, instruction_count)) {
        return true;
    }
    // Keep generic path conservative: allow global ownership/chain resolution on
    // bounded function bodies only.
    constexpr int kGenericConditionalChainInstructionBudget = 1536;
    return instruction_count > 0 &&
           instruction_count <= kGenericConditionalChainInstructionBudget;
}

bool InstructionWritesNamedLocal(const Lua51Instruction& instruction,
                                 const RegisterDeclarations& declarations,
                                 const Lua51Function& function,
                                 int line) {
    const int register_count = static_cast<int>(function.max_stack_size);
    const auto is_named_local = [&](int reg) {
        if (reg < 0 || reg >= register_count) {
            return false;
        }
        const std::string name =
            GetStraightLineRegisterName(declarations, function, reg, line);
        return !name.empty() && !IsRenderTemporaryName(name);
    };
    const auto writes_range = [&](int first, int last) {
        for (int reg = first; reg <= last && reg < register_count; ++reg) {
            if (is_named_local(reg)) {
                return true;
            }
        }
        return false;
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
            return is_named_local(instruction.a);
        case Lua51Opcode::kLoadNil:
            return writes_range(instruction.a, instruction.b);
        case Lua51Opcode::kSelf:
            return is_named_local(instruction.a) || is_named_local(instruction.a + 1);
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

bool InstructionWritesRegister(const Lua51Instruction& instruction, int reg) {
    if (reg < 0) {
        return false;
    }

    const auto is_reg = [&](int value) {
        return value == reg;
    };
    const auto touches_range = [&](int first, int last) {
        return reg >= first && reg <= last;
    };

    switch (instruction.opcode) {
        case Lua51Opcode::kLoadK:
        case Lua51Opcode::kLoadBool:
        case Lua51Opcode::kMove:
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
            return is_reg(instruction.a);
        case Lua51Opcode::kLoadNil:
            return touches_range(instruction.a, instruction.b);
        case Lua51Opcode::kSelf:
            return is_reg(instruction.a) || is_reg(instruction.a + 1);
        case Lua51Opcode::kCall:
            if (instruction.c > 1 && touches_range(instruction.a, instruction.a + instruction.c - 2)) {
                return true;
            }
            if (instruction.c == 0) {
                return reg >= instruction.a;
            }
            return false;
        case Lua51Opcode::kVarArg:
            if (instruction.b > 1 && touches_range(instruction.a, instruction.a + instruction.b - 2)) {
                return true;
            }
            if (instruction.b == 0) {
                return reg >= instruction.a;
            }
            return false;
        case Lua51Opcode::kForPrep:
        case Lua51Opcode::kForLoop:
            return touches_range(instruction.a, instruction.a + 3);
        case Lua51Opcode::kTForLoop:
            return touches_range(instruction.a + 3, instruction.a + 2 + instruction.c);
        default:
            return false;
    }
}

int CountInstructionWrittenRegisters(const Lua51Instruction& instruction, int register_count) {
    if (register_count <= 0) {
        return 0;
    }

    const auto count_range = [&](int first, int last) {
        first = std::max(0, first);
        last = std::min(last, register_count - 1);
        return first <= last ? (last - first + 1) : 0;
    };

    switch (instruction.opcode) {
        case Lua51Opcode::kLoadK:
        case Lua51Opcode::kLoadBool:
        case Lua51Opcode::kMove:
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
            return count_range(instruction.a, instruction.a);
        case Lua51Opcode::kLoadNil:
            return count_range(instruction.a, instruction.b);
        case Lua51Opcode::kSelf:
            return count_range(instruction.a, instruction.a + 1);
        case Lua51Opcode::kCall:
            if (instruction.c == 0) {
                return count_range(instruction.a, register_count - 1);
            }
            if (instruction.c > 1) {
                return count_range(instruction.a, instruction.a + instruction.c - 2);
            }
            return 0;
        case Lua51Opcode::kVarArg:
            if (instruction.b == 0) {
                return count_range(instruction.a, register_count - 1);
            }
            if (instruction.b > 1) {
                return count_range(instruction.a, instruction.a + instruction.b - 2);
            }
            return 0;
        case Lua51Opcode::kForPrep:
        case Lua51Opcode::kForLoop:
            return count_range(instruction.a, instruction.a + 3);
        case Lua51Opcode::kTForLoop:
            return count_range(instruction.a + 3, instruction.a + 2 + instruction.c);
        default:
            return 0;
    }
}

bool ShouldStopLuaChainedConditionOnFreshTemporary(
    const std::vector<Lua51Instruction>& instructions,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    int segment_start,
    int candidate_line) {
    if (candidate_line < 1 ||
        static_cast<std::size_t>(candidate_line) > instructions.size()) {
        return false;
    }

    const Lua51Instruction& candidate =
        instructions[static_cast<std::size_t>(candidate_line - 1)];
    if (candidate.opcode != Lua51Opcode::kTest || candidate.a < 0) {
        return false;
    }

    const int register_count = static_cast<int>(function.max_stack_size);
    if (candidate.a >= register_count || segment_start > candidate_line - 1) {
        return false;
    }

    const std::string name =
        GetStraightLineRegisterName(declarations, function, candidate.a, candidate_line);
    if (!name.empty() && !IsRenderTemporaryName(name)) {
        return false;
    }

    int candidate_write_count = 0;
    for (int line = segment_start;
         line <= candidate_line - 1 &&
         static_cast<std::size_t>(line) <= instructions.size();
         ++line) {
        const Lua51Instruction& instruction =
            instructions[static_cast<std::size_t>(line - 1)];
        if (!InstructionWritesRegister(instruction, candidate.a)) {
            continue;
        }

        ++candidate_write_count;
        if (CountInstructionWrittenRegisters(instruction, register_count) != 1) {
            return true;
        }
    }

    return candidate_write_count > 1;
}

bool IsLuaConditionalRegisterReadByInstruction(const Lua51Instruction& instruction, int reg) {
    if (reg < 0) {
        return false;
    }

    switch (instruction.opcode) {
        case Lua51Opcode::kTest:
            return instruction.a == reg;
        case Lua51Opcode::kTestSet:
            return instruction.b == reg;
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe:
            return instruction.b == reg || instruction.c == reg;
        default:
            return false;
    }
}

std::uint64_t ComputeLuaCallOwnershipFingerprint(const Lua51Instruction& call_instruction,
                                                 int producer_line) {
    std::uint64_t fingerprint = static_cast<std::uint64_t>(producer_line + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(call_instruction.opcode);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(call_instruction.a + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(call_instruction.b + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(call_instruction.c + 0x9e37);
    return fingerprint;
}

struct LuaConditionalCallValueCandidate {
    bool valid = false;
    int register_index = -1;
    int producer_line = -1;
    std::uint64_t fingerprint = 0;
};

LuaConditionalCallValueCandidate FindLuaConditionalCallValueCandidate(
    const std::vector<Lua51Instruction>& instructions,
    int condition_line,
    int target_register) {
    LuaConditionalCallValueCandidate candidate;
    candidate.register_index = target_register;
    if (target_register < 0 ||
        condition_line < 2 ||
        static_cast<std::size_t>(condition_line) > instructions.size()) {
        return candidate;
    }

    const Lua51Instruction& condition_instruction =
        instructions[static_cast<std::size_t>(condition_line - 1)];
    if (!IsLuaConditionalRegisterReadByInstruction(condition_instruction, target_register)) {
        return candidate;
    }

    constexpr int kMaxCallOwnerLookback = 8;
    const int scan_floor = std::max(1, condition_line - kMaxCallOwnerLookback);
    for (int line = condition_line - 1; line >= scan_floor; --line) {
        const Lua51Instruction& probe =
            instructions[static_cast<std::size_t>(line - 1)];
        if (!InstructionWritesRegister(probe, target_register)) {
            if (probe.opcode == Lua51Opcode::kJmp ||
                decompiler::IsConditionalBranchOpcode(probe.opcode)) {
                break;
            }
            continue;
        }

        if (probe.opcode != Lua51Opcode::kCall || probe.a != target_register || probe.c != 2) {
            return candidate;
        }
        for (int between = line + 1; between < condition_line; ++between) {
            if (InstructionWritesRegister(
                    instructions[static_cast<std::size_t>(between - 1)],
                    target_register)) {
                return candidate;
            }
        }
        candidate.valid = true;
        candidate.producer_line = line;
        candidate.fingerprint = ComputeLuaCallOwnershipFingerprint(probe, line);
        return candidate;
    }

    return candidate;
}

std::uint64_t ComputeLuaConditionalChainFingerprint(const LuaConditionalChainSpan& span) {
    std::uint64_t fingerprint =
        static_cast<std::uint64_t>(span.chain_start_line + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(span.next_line + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(span.join_line + 0x9e37);
    for (const LuaConditionalChainLink& link : span.links) {
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(link.condition_line + 0x9e37);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(link.jump_line + 0x9e37);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(link.effective_target_line + 0x9e37);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(link.target_register + 0x9e37);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(link.source_register + 0x9e37);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(link.opcode);
    }
    return fingerprint;
}

int CountLuaTargetWritesInRange(const std::vector<Lua51Instruction>& instructions,
                                int start_line,
                                int end_line,
                                int target_register) {
    if (target_register < 0 || end_line < start_line) {
        return 0;
    }

    int writes = 0;
    for (int line = start_line;
         line <= end_line && static_cast<std::size_t>(line) <= instructions.size();
         ++line) {
        if (InstructionWritesRegister(instructions[static_cast<std::size_t>(line - 1)],
                                      target_register)) {
            ++writes;
        }
    }
    return writes;
}

std::uint64_t ComputeLuaJoinOwnershipMemoFingerprint(
    const std::vector<Lua51Instruction>& instructions,
    int condition_line,
    int branch_target_line,
    int join_line,
    int target_register) {
    std::uint64_t fingerprint =
        static_cast<std::uint64_t>(condition_line + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(branch_target_line + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(join_line + 0x9e37);
    fingerprint = fingerprint * 1315423911ull +
                  static_cast<std::uint64_t>(target_register + 0x9e37);
    if (condition_line >= 1 &&
        static_cast<std::size_t>(condition_line) <= instructions.size()) {
        const Lua51Instruction& condition =
            instructions[static_cast<std::size_t>(condition_line - 1)];
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(condition.opcode);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(condition.a + 0x9e37);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(condition.b + 0x9e37);
        fingerprint = fingerprint * 1315423911ull +
                      static_cast<std::uint64_t>(condition.c + 0x9e37);
    }
    return fingerprint;
}

}  // namespace

namespace {

struct LuaStatementTempOwnershipCacheKey {
    int condition_line = 0;
    int branch_target_line = 0;
    int join_line = 0;
    int target_register = -1;
    std::uint64_t chain_fingerprint = 0;

    bool operator==(const LuaStatementTempOwnershipCacheKey& other) const {
        return condition_line == other.condition_line &&
               branch_target_line == other.branch_target_line &&
               join_line == other.join_line &&
               target_register == other.target_register &&
               chain_fingerprint == other.chain_fingerprint;
    }
};

struct LuaStatementTempOwnershipCacheKeyHash {
    std::size_t operator()(const LuaStatementTempOwnershipCacheKey& key) const {
        std::size_t hash = static_cast<std::size_t>(key.condition_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.branch_target_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.join_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.target_register + 0x9e37);
        hash = hash * 1315423911u +
               static_cast<std::size_t>(key.chain_fingerprint & 0xffffffffu);
        hash = hash * 1315423911u +
               static_cast<std::size_t>((key.chain_fingerprint >> 32U) & 0xffffffffu);
        return hash;
    }
};

struct LuaStatementTempOwnershipMemo {
    const Lua51Instruction* instructions_data = nullptr;
    std::size_t instruction_count = 0;
    const decompiler::Lua51ControlFlowGraph* graph = nullptr;
    std::unordered_map<LuaStatementTempOwnershipCacheKey,
                       LuaConditionalStatementTempOwnership,
                       LuaStatementTempOwnershipCacheKeyHash>
        cache;
};

LuaStatementTempOwnershipMemo& GetLuaStatementTempOwnershipMemo() {
    static thread_local LuaStatementTempOwnershipMemo memo;
    return memo;
}

struct LuaConditionalChainSpanCacheKey {
    int chain_start_line = 0;
    int max_condition_line = 0;
    int max_target_line = 0;
    int expected_target_line = 0;
    int instruction_count = 0;
    std::uint64_t profile_flags = 0;

    bool operator==(const LuaConditionalChainSpanCacheKey& other) const {
        return chain_start_line == other.chain_start_line &&
               max_condition_line == other.max_condition_line &&
               max_target_line == other.max_target_line &&
               expected_target_line == other.expected_target_line &&
               instruction_count == other.instruction_count &&
               profile_flags == other.profile_flags;
    }
};

struct LuaConditionalChainSpanCacheKeyHash {
    std::size_t operator()(const LuaConditionalChainSpanCacheKey& key) const {
        std::size_t hash = static_cast<std::size_t>(key.chain_start_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.max_condition_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.max_target_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.expected_target_line + 0x9e37);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.instruction_count);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.profile_flags & 0xffffffffu);
        hash = hash * 1315423911u +
               static_cast<std::size_t>((key.profile_flags >> 32U) & 0xffffffffu);
        return hash;
    }
};

struct LuaConditionalChainSpanCacheValue {
    bool found = false;
    LuaConditionalChainSpan span;
};

struct LuaConditionalChainSpanMemo {
    const Lua51Instruction* instructions_data = nullptr;
    std::size_t instruction_count = 0;
    const decompiler::Lua51ControlFlowGraph* graph = nullptr;
    std::unordered_map<LuaConditionalChainSpanCacheKey,
                       LuaConditionalChainSpanCacheValue,
                       LuaConditionalChainSpanCacheKeyHash>
        cache;
};

LuaConditionalChainSpanMemo& GetLuaConditionalChainSpanMemo() {
    static thread_local LuaConditionalChainSpanMemo memo;
    return memo;
}

struct LuaJoinOwnershipCommitCacheKey {
    int condition_line = 0;
    int branch_target_line = 0;
    int join_line = 0;
    int target_register = -1;
    std::uint64_t chain_fingerprint = 0;

    bool operator==(const LuaJoinOwnershipCommitCacheKey& other) const {
        return condition_line == other.condition_line &&
               branch_target_line == other.branch_target_line &&
               join_line == other.join_line &&
               target_register == other.target_register &&
               chain_fingerprint == other.chain_fingerprint;
    }
};

struct LuaJoinOwnershipCommitCacheKeyHash {
    std::size_t operator()(const LuaJoinOwnershipCommitCacheKey& key) const {
        std::size_t hash = static_cast<std::size_t>(key.condition_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.branch_target_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.join_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.target_register + 0x9e37);
        hash = hash * 1315423911u +
               static_cast<std::size_t>(key.chain_fingerprint & 0xffffffffu);
        hash = hash * 1315423911u +
               static_cast<std::size_t>((key.chain_fingerprint >> 32U) & 0xffffffffu);
        return hash;
    }
};

struct LuaJoinOwnershipCommitMemo {
    const Lua51Instruction* instructions_data = nullptr;
    std::size_t instruction_count = 0;
    const decompiler::Lua51ControlFlowGraph* graph = nullptr;
    std::unordered_map<LuaJoinOwnershipCommitCacheKey,
                       LuaJoinOwnershipCommitDecision,
                       LuaJoinOwnershipCommitCacheKeyHash>
        cache;
};

LuaJoinOwnershipCommitMemo& GetLuaJoinOwnershipCommitMemo() {
    static thread_local LuaJoinOwnershipCommitMemo memo;
    return memo;
}

struct LuaConditionalRootCommitPacketCacheKey {
    int condition_line = 0;
    int max_condition_line = 0;
    int max_target_line = 0;
    int expected_target_line = 0;
    int register_count = 0;
    int fallback_branch_target_line = 0;
    int fallback_join_line = 0;
    int instruction_count = 0;
    std::uint64_t profile_flags = 0;

    bool operator==(const LuaConditionalRootCommitPacketCacheKey& other) const {
        return condition_line == other.condition_line &&
               max_condition_line == other.max_condition_line &&
               max_target_line == other.max_target_line &&
               expected_target_line == other.expected_target_line &&
               register_count == other.register_count &&
               fallback_branch_target_line == other.fallback_branch_target_line &&
               fallback_join_line == other.fallback_join_line &&
               instruction_count == other.instruction_count &&
               profile_flags == other.profile_flags;
    }
};

struct LuaConditionalRootCommitPacketCacheKeyHash {
    std::size_t operator()(const LuaConditionalRootCommitPacketCacheKey& key) const {
        std::size_t hash = static_cast<std::size_t>(key.condition_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.max_condition_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.max_target_line);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.expected_target_line + 0x9e37);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.register_count + 0x9e37);
        hash = hash * 1315423911u +
               static_cast<std::size_t>(key.fallback_branch_target_line + 0x9e37);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.fallback_join_line + 0x9e37);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.instruction_count);
        hash = hash * 1315423911u + static_cast<std::size_t>(key.profile_flags & 0xffffffffu);
        hash = hash * 1315423911u +
               static_cast<std::size_t>((key.profile_flags >> 32U) & 0xffffffffu);
        return hash;
    }
};

struct LuaConditionalRootCommitPacketCacheValue {
    bool found = false;
    LuaConditionalRootCommitPacket packet;
};

struct LuaConditionalRootCommitPacketMemo {
    const Lua51Instruction* instructions_data = nullptr;
    std::size_t instruction_count = 0;
    const decompiler::Lua51ControlFlowGraph* graph = nullptr;
    std::unordered_map<LuaConditionalRootCommitPacketCacheKey,
                       LuaConditionalRootCommitPacketCacheValue,
                       LuaConditionalRootCommitPacketCacheKeyHash>
        cache;
};

LuaConditionalRootCommitPacketMemo& GetLuaConditionalRootCommitPacketMemo() {
    static thread_local LuaConditionalRootCommitPacketMemo memo;
    return memo;
}

int ResolveLuaConditionalBranchTargetLine(const std::vector<Lua51Instruction>& instructions,
                                          int condition_line,
                                          int fallback_target) {
    if (condition_line < 1 ||
        condition_line + 1 > static_cast<int>(instructions.size())) {
        return fallback_target;
    }
    const Lua51Instruction& jump =
        instructions[static_cast<std::size_t>(condition_line)];
    if (jump.opcode != Lua51Opcode::kJmp) {
        return fallback_target;
    }
    return decompiler::ComputeJumpTargetLine(condition_line + 1,
                                             jump.sbx,
                                             static_cast<int>(instructions.size()));
}

int AdjustLuaSetConditionLineForTarget(const std::vector<Lua51Instruction>& instructions,
                                       int line,
                                       int target_register) {
    if (line < 1 || static_cast<std::size_t>(line) > instructions.size()) {
        return line;
    }

    int probe = line;
    while (probe >= 1 && static_cast<std::size_t>(probe) <= instructions.size()) {
        const Lua51Instruction& instruction =
            instructions[static_cast<std::size_t>(probe - 1)];
        if (instruction.opcode != Lua51Opcode::kLoadBool) {
            break;
        }
        if (target_register >= 0 && instruction.a != target_register) {
            break;
        }
        --probe;
    }
    if (probe == line) {
        return line;
    }

    const int loadbool_line = probe + 1;
    if (loadbool_line < 1 ||
        static_cast<std::size_t>(loadbool_line) > instructions.size()) {
        return line;
    }
    const Lua51Instruction& loadbool =
        instructions[static_cast<std::size_t>(loadbool_line - 1)];
    const int adjusted =
        loadbool.c != 0 ? loadbool_line + 2 : loadbool_line + 1;
    return std::clamp(adjusted, 1, static_cast<int>(instructions.size()) + 1);
}

}  // namespace

bool IsLuaScopedLargeHoldoutOwnershipPath(std::string_view source_name) {
    if (source_name.empty()) {
        return false;
    }
    return IsLuaScriptProfilePath(source_name, LuaScriptProfileFlag::kScopedLargeHoldout);
}

bool IsLuaScopedSmallHoldoutOwnershipPath(std::string_view source_name) {
    if (source_name.empty()) {
        return false;
    }
    return IsLuaScriptProfilePath(source_name, LuaScriptProfileFlag::kScopedSmallHoldout);
}

bool IsLuaAiCoreHotspotOwnershipPath(std::string_view source_name) {
    if (source_name.empty()) {
        return false;
    }
    return IsLuaScriptProfilePath(source_name, LuaScriptProfileFlag::kAiCoreHotspot);
}

bool IsLuaPriorityOwnershipPath(std::string_view source_name) {
    if (source_name.empty()) {
        return false;
    }
    return IsLuaScriptProfilePath(source_name, LuaScriptProfileFlag::kPriorityOwnership);
}

bool IsLuaInteractiveCutsceneActionsOwnershipPath(std::string_view source_name) {
    if (source_name.empty()) {
        return false;
    }
    return IsLuaScriptProfilePath(source_name, LuaScriptProfileFlag::kInteractiveCutsceneActions);
}

bool IsLuaGroupMindOwnershipPath(std::string_view source_name) {
    if (source_name.empty()) {
        return false;
    }
    return IsLuaScriptProfilePath(source_name, LuaScriptProfileFlag::kGroupMind);
}

bool IsLuaScopedLargeHoldoutOwnershipFunction(const Lua51Function& function) {
    return IsLuaScopedLargeHoldoutOwnershipPath(function.source_name);
}

bool IsLuaScopedSmallHoldoutOwnershipFunction(const Lua51Function& function) {
    return IsLuaScopedSmallHoldoutOwnershipPath(function.source_name);
}

bool IsLuaAiCoreHotspotOwnershipFunction(const Lua51Function& function) {
    return IsLuaAiCoreHotspotOwnershipPath(function.source_name);
}

bool IsLuaPriorityOwnershipFunction(const Lua51Function& function) {
    return IsLuaPriorityOwnershipPath(function.source_name);
}

bool IsLikelyLuaInteractiveCutsceneActionsF2WithoutSource(const Lua51Function& function) {
    if (!function.source_name.empty() || function.line_defined < 17 ||
        function.line_defined > 23 || function.parameter_count < 4) {
        return false;
    }

    bool saw_wait_until_complete = false;
    bool saw_scene_states = false;
    bool saw_cutscene_state_call = false;
    for (const Lua51Constant& constant : function.constants) {
        if (constant.type != Lua51ConstantType::kString) {
            continue;
        }
        const std::string& text = constant.string_value;
        if (text == "WaitUntilComplete") {
            saw_wait_until_complete = true;
        } else if (text == "SceneStates") {
            saw_scene_states = true;
        } else if (text == "GetCutsceneState") {
            saw_cutscene_state_call = true;
        }
        if (saw_wait_until_complete && saw_scene_states && saw_cutscene_state_call) {
            return true;
        }
    }
    return false;
}

bool IsLuaInteractiveCutsceneActionsOwnershipFunction(const Lua51Function& function) {
    return IsLuaInteractiveCutsceneActionsOwnershipPath(function.source_name) ||
           IsLikelyLuaInteractiveCutsceneActionsF2WithoutSource(function);
}

bool IsLuaGroupMindOwnershipFunction(const Lua51Function& function) {
    return IsLuaGroupMindOwnershipPath(function.source_name);
}

bool IsLuaInteractiveCutsceneActionsF2OwnershipFunction(const Lua51Function& function) {
    if (!IsLuaInteractiveCutsceneActionsOwnershipFunction(function)) {
        return false;
    }
    // The target function is stable around line 20, but real builds can drift by a
    // few lines after script updates/recompiles.
    return function.line_defined >= 17 && function.line_defined <= 23;
}

bool IsLuaConditionalRootPacketReusableForBranch(
    const LuaConditionalRootCommitPacket& packet,
    int condition_line,
    int branch_target_line) {
    if (!packet.ownership_proven) {
        return false;
    }
    if (packet.chain_span.chain_start_line == condition_line) {
        // Exact root identity match.
    } else {
        // Compatibility-based reuse: preview/structured callers can probe from
        // different (but equivalent) chain links after LOADBOOL/JMP normalization.
        if (condition_line <= 0 ||
            packet.chain_span.chain_start_line <= 0 ||
            packet.chain_span.next_line <= 0 ||
            condition_line < packet.chain_span.chain_start_line ||
            condition_line >= packet.chain_span.next_line) {
            return false;
        }
    }

    if (branch_target_line <= 0 || packet.branch_target_line <= 0) {
        return true;
    }
    if (packet.branch_target_line == branch_target_line) {
        return true;
    }
    if (packet.effective_join_line == branch_target_line ||
        packet.chain_span.join_line == branch_target_line) {
        return true;
    }

    // Allow reuse when the queried branch target falls inside the packet's
    // proven branch->join envelope (preview/structured can use slightly
    // different join anchors after LOADBOOL/JMP normalization).
    const int envelope_start =
        std::min(packet.branch_target_line,
                 std::min(packet.effective_join_line, packet.chain_span.join_line));
    const int envelope_end =
        std::max(packet.branch_target_line,
                 std::max(packet.effective_join_line, packet.chain_span.join_line));
    return branch_target_line >= envelope_start &&
           branch_target_line <= envelope_end;
}

bool IsLuaConditionalRootPacketJoinCompatible(
    const std::vector<Lua51Instruction>& instructions,
    const LuaConditionalRootCommitPacket& packet,
    int fallback_join_line) {
    if (fallback_join_line <= 0) {
        return true;
    }
    const int packet_join =
        packet.effective_join_line > 0 ? packet.effective_join_line : packet.chain_span.join_line;
    if (packet_join <= 0) {
        return false;
    }
    if (packet_join == fallback_join_line) {
        return true;
    }
    const int lower = std::min(packet_join, fallback_join_line);
    const int upper = std::max(packet_join, fallback_join_line);
    if (upper - lower > 6 ||
        lower < 1 ||
        static_cast<std::size_t>(upper) > instructions.size()) {
        return false;
    }
    for (int line = lower; line <= upper; ++line) {
        const Lua51Instruction& instruction =
            instructions[static_cast<std::size_t>(line - 1)];
        if (instruction.opcode != Lua51Opcode::kLoadBool &&
            instruction.opcode != Lua51Opcode::kJmp) {
            return false;
        }
    }
    return true;
}

bool IsLuaConditionalRootPacketCompatibilityMatch(
    const std::vector<Lua51Instruction>& instructions,
    const LuaConditionalRootCommitPacket& packet,
    int condition_line,
    int resolved_branch_target_line,
    int fallback_join_line,
    int target_register) {
    if (!packet.ownership_proven ||
        !IsLuaConditionalRootPacketReusableForBranch(
            packet, condition_line, resolved_branch_target_line)) {
        return false;
    }
    if (target_register >= 0 &&
        packet.carried_register >= 0 &&
        packet.carried_register != target_register) {
        return false;
    }
    if (packet.commit_kind != LuaJoinOwnershipCommitKind::kUnproven &&
        packet.ownership_commit.commit_kind != LuaJoinOwnershipCommitKind::kUnproven &&
        packet.commit_kind != packet.ownership_commit.commit_kind) {
        return false;
    }
    return IsLuaConditionalRootPacketJoinCompatible(
        instructions, packet, fallback_join_line);
}

int ResolveLuaConditionalOwnershipRegisterForInstruction(const Lua51Instruction& instruction,
                                                         int register_count) {
    switch (instruction.opcode) {
        case Lua51Opcode::kTest:
            return instruction.a;
        case Lua51Opcode::kTestSet:
            // TESTSET writes to A when the condition holds (A := B). For
            // join-local ownership and carried-value commit timing we must
            // track the destination register, not the source register.
            return instruction.a;
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe: {
            const bool lhs_is_register = instruction.b >= 0 && instruction.b < register_count;
            const bool rhs_is_register = instruction.c >= 0 && instruction.c < register_count;
            if (lhs_is_register != rhs_is_register) {
                return lhs_is_register ? instruction.b : instruction.c;
            }
            return -1;
        }
        default:
            return -1;
    }
}

int ResolveLuaConditionalOwnershipRegisterForChainLink(const LuaConditionalChainLink& link,
                                                       int register_count) {
    (void)register_count;
    switch (link.opcode) {
        case Lua51Opcode::kTest:
            return link.target_register;
        case Lua51Opcode::kTestSet:
            return link.target_register;
        default:
            return -1;
    }
}

bool TryClassifyLuaConditionalStatementTempOwnership(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int condition_line,
    int branch_target_line,
    int join_line,
    int target_register,
    int consumed_span_start,
    int consumed_span_end,
    LuaConditionalStatementTempOwnership* ownership_out) {
    if (ownership_out == nullptr) {
        return false;
    }

    LuaConditionalStatementTempOwnership ownership;
    if (control_flow_graph == nullptr) {
        ownership.proven = true;
        ownership.unambiguous = true;
        ownership.effective_join_line = join_line;
        *ownership_out = ownership;
        return true;
    }
    if (condition_line < 1 || join_line < 1 ||
        static_cast<std::size_t>(condition_line) > instructions.size()) {
        return false;
    }
    LuaStatementTempOwnershipMemo& memo = GetLuaStatementTempOwnershipMemo();
    if (memo.instructions_data != instructions.data() ||
        memo.instruction_count != instructions.size() ||
        memo.graph != control_flow_graph) {
        memo.instructions_data = instructions.data();
        memo.instruction_count = instructions.size();
        memo.graph = control_flow_graph;
        memo.cache.clear();
    }

    const int branch_target = ResolveLuaConditionalBranchTargetLine(
        instructions, condition_line, branch_target_line);
    if (branch_target < 1) {
        return false;
    }
    const bool request_consumed_span =
        consumed_span_start >= condition_line && consumed_span_end >= consumed_span_start;
    const int ownership_consumed_start = request_consumed_span
                                             ? std::max(condition_line, consumed_span_start)
                                             : -1;
    const int ownership_consumed_end = request_consumed_span
                                           ? std::min(std::max(ownership_consumed_start,
                                                               join_line - 1),
                                                      consumed_span_end)
                                           : -1;
    const bool have_consumed_span = ownership_consumed_start <= ownership_consumed_end &&
                                    ownership_consumed_start >= 1;

    std::uint64_t chain_fingerprint = 0;
    if (have_consumed_span) {
        chain_fingerprint = ComputeLuaJoinOwnershipMemoFingerprint(
            instructions,
            condition_line,
            branch_target,
            join_line,
            target_register);
        chain_fingerprint = chain_fingerprint * 1315423911ull +
                            static_cast<std::uint64_t>(ownership_consumed_start + 0x9e37);
        chain_fingerprint = chain_fingerprint * 1315423911ull +
                            static_cast<std::uint64_t>(ownership_consumed_end + 0x9e37);
    }

    const LuaStatementTempOwnershipCacheKey key{
        .condition_line = condition_line,
        .branch_target_line = branch_target,
        .join_line = join_line,
        .target_register = target_register,
        .chain_fingerprint = chain_fingerprint,
    };
    const auto cached = memo.cache.find(key);
    if (cached != memo.cache.end()) {
        *ownership_out = cached->second;
        return true;
    }
    const LuaConditionalCallValueCandidate call_value_candidate =
        FindLuaConditionalCallValueCandidate(instructions, condition_line, target_register);

    decompiler::LuaConditionalOwnership conditional_ownership;
    const bool classified_ownership =
        have_consumed_span
            ? decompiler::TryClassifyLuaConditionalOwnershipForConsumedSpan(
                  *control_flow_graph,
                  instructions,
                  condition_line,
                  condition_line + 2,
                  branch_target,
                  std::max(branch_target, join_line - 1),
                  -1,
                  -1,
                  ownership_consumed_start,
                  ownership_consumed_end,
                  &conditional_ownership)
            : decompiler::TryClassifyLuaConditionalOwnership(*control_flow_graph,
                                                             instructions,
                                                             condition_line,
                                                             condition_line + 2,
                                                             branch_target,
                                                             std::max(branch_target, join_line - 1),
                                                             -1,
                                                             -1,
                                                             &conditional_ownership);
    if (!classified_ownership ||
        !conditional_ownership.classified) {
        memo.cache.emplace(key, ownership);
        *ownership_out = ownership;
        return true;
    }

    ownership.unambiguous = conditional_ownership.unambiguous;
    ownership.effective_join_line =
        conditional_ownership.effective_join_line > 0
            ? conditional_ownership.effective_join_line
            : join_line;
    if (ownership.unambiguous) {
        const bool join_compatible =
            conditional_ownership.effective_join_line <= 0 ||
            conditional_ownership.effective_join_line == join_line ||
            conditional_ownership.effective_join_line == branch_target;
        if (!join_compatible) {
            ownership.unambiguous = false;
        }
    }

    if (ownership.unambiguous &&
        target_register >= 0 &&
        branch_target != join_line) {
        const int overwrite_scan_start =
            have_consumed_span ? ownership_consumed_end + 1 : condition_line + 2;
        for (int line = overwrite_scan_start;
             line < join_line &&
             static_cast<std::size_t>(line) <= instructions.size();
             ++line) {
            if (InstructionWritesRegister(
                    instructions[static_cast<std::size_t>(line - 1)],
                    target_register)) {
                ownership.unambiguous = false;
                break;
            }
        }
    }

    if (ownership.unambiguous && call_value_candidate.valid) {
        const int effective_join_line =
            ownership.effective_join_line > 0 ? ownership.effective_join_line : join_line;
        constexpr int kMaxConservativeCallOwnershipSpan = 96;
        if (effective_join_line - call_value_candidate.producer_line >
            kMaxConservativeCallOwnershipSpan) {
            ownership.proven = ownership.unambiguous;
            memo.cache.emplace(key, ownership);
            *ownership_out = ownership;
            return true;
        }
        bool register_stable = true;
        for (int line = call_value_candidate.producer_line + 1;
             line < effective_join_line &&
             static_cast<std::size_t>(line) <= instructions.size();
             ++line) {
            if (line == condition_line) {
                const Lua51Instruction& condition_instruction =
                    instructions[static_cast<std::size_t>(condition_line - 1)];
                if (condition_instruction.opcode == Lua51Opcode::kTestSet &&
                    condition_instruction.a == target_register &&
                    condition_instruction.b == target_register) {
                    continue;
                }
            }
            if (InstructionWritesRegister(instructions[static_cast<std::size_t>(line - 1)],
                                          target_register)) {
                register_stable = false;
                break;
            }
        }
        if (register_stable) {
            for (int line = call_value_candidate.producer_line;
                 line < effective_join_line &&
                 static_cast<std::size_t>(line) <= instructions.size();
                 ++line) {
                const Lua51Instruction& scan_instruction =
                    instructions[static_cast<std::size_t>(line - 1)];
                if (scan_instruction.opcode != Lua51Opcode::kJmp) {
                    continue;
                }
                const int jump_target = decompiler::ComputeJumpTargetLine(
                    line, scan_instruction.sbx, static_cast<int>(instructions.size()));
                if (jump_target <= line && jump_target >= call_value_candidate.producer_line) {
                    register_stable = false;
                    break;
                }
            }
        }
        if (register_stable &&
            call_value_candidate.producer_line + 1 <= effective_join_line - 1 &&
            decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                                 call_value_candidate.producer_line + 1,
                                                 effective_join_line - 1,
                                                 call_value_candidate.producer_line,
                                                 std::max(condition_line + 1,
                                                          have_consumed_span
                                                              ? ownership_consumed_end
                                                              : condition_line + 1))) {
            register_stable = false;
        }
        if (register_stable) {
            ownership.call_value_owned = true;
            ownership.call_value_register = target_register;
            ownership.call_value_producer_line = call_value_candidate.producer_line;
            ownership.call_value_fingerprint = call_value_candidate.fingerprint;
        }
    }

    ownership.proven = ownership.unambiguous;
    memo.cache.emplace(key, ownership);
    *ownership_out = ownership;
    return true;
}

bool TryBuildLuaJoinOwnershipCommitDecision(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int condition_line,
    int branch_target_line,
    int join_line,
    int target_register,
    LuaJoinOwnershipCommitDecision* decision_out,
    const Lua51Function* function_hint) {
    if (decision_out == nullptr || condition_line < 1 || join_line < 1 ||
        static_cast<std::size_t>(condition_line) > instructions.size()) {
        return false;
    }

    LuaJoinOwnershipCommitDecision decision;
    decision.target_register = target_register;
    decision.effective_join_line = join_line;
    const bool enable_ai_core_hotspot_chain_ownership =
        ShouldEnablePriorityBodyOwnership(function_hint, instruction_count);
    const bool enable_behavior_chain_ownership =
        ShouldEnableBehaviorBodyOwnership(function_hint, instruction_count);
    const bool enable_chain_ownership =
        ShouldEnableConditionalChainOwnership(function_hint, instruction_count);

    if (join_line <= condition_line + 1) {
        *decision_out = decision;
        return true;
    }

    const int branch_target = ResolveLuaConditionalBranchTargetLine(
        instructions, condition_line, branch_target_line);
    if (branch_target < 1) {
        *decision_out = decision;
        return true;
    }

    constexpr int kMaxConservativeJoinOwnershipSpan = 32;
    constexpr int kMaxAiCoreHotspotJoinOwnershipSpan = 48;
    constexpr int kMaxConservativeWideJoinOwnershipSpan = 512;
    const int span_width = join_line - condition_line;
    LuaConditionalChainSpan chain_span;
    const int join_scan_budget =
        (enable_ai_core_hotspot_chain_ownership || enable_behavior_chain_ownership)
            ? kMaxAiCoreHotspotJoinOwnershipSpan
            : kMaxConservativeJoinOwnershipSpan;
    const int max_condition_line =
        std::min(join_line - 1, condition_line + join_scan_budget);
    const Lua51Instruction& root_instruction =
        instructions[static_cast<std::size_t>(condition_line - 1)];
    const bool root_can_seed_chain =
        target_register >= 0 ||
        root_instruction.opcode == Lua51Opcode::kTest ||
        root_instruction.opcode == Lua51Opcode::kTestSet ||
        root_instruction.opcode == Lua51Opcode::kEq ||
        root_instruction.opcode == Lua51Opcode::kLt ||
        root_instruction.opcode == Lua51Opcode::kLe;
    const bool should_collect_chain = enable_chain_ownership && root_can_seed_chain;
    const bool have_chain_span =
        should_collect_chain &&
        TryCollectLuaConditionalChainSpan(control_flow_graph,
                                          instructions,
                                          instruction_count,
                                          condition_line,
                                          max_condition_line,
                                          join_line,
                                          join_line,
                                          &chain_span,
                                          function_hint);
    if (!have_chain_span && span_width > kMaxConservativeJoinOwnershipSpan) {
        *decision_out = decision;
        return true;
    }
    if (span_width > kMaxConservativeWideJoinOwnershipSpan) {
        *decision_out = decision;
        return true;
    }
    int set_condition_target_register = -1;
    if (root_instruction.opcode == Lua51Opcode::kTestSet) {
        set_condition_target_register =
            root_instruction.a >= 0 ? root_instruction.a : root_instruction.b;
    } else if (root_instruction.opcode == Lua51Opcode::kTest) {
        set_condition_target_register = root_instruction.a;
    } else if (root_instruction.opcode == Lua51Opcode::kEq ||
               root_instruction.opcode == Lua51Opcode::kLt ||
               root_instruction.opcode == Lua51Opcode::kLe) {
        // Compare-rooted set-condition stacks have no carried destination
        // register; use global LOADBOOL skip-pair normalization.
        set_condition_target_register = -1;
    }
    if (have_chain_span) {
        decision.chain_fingerprint = ComputeLuaConditionalChainFingerprint(chain_span);
        decision.effective_join_line = chain_span.join_line;
    } else {
        const int adjusted_join_line =
            AdjustLuaSetConditionLineForTarget(instructions,
                                               decision.effective_join_line,
                                               set_condition_target_register);
        if (adjusted_join_line > condition_line + 1 &&
            adjusted_join_line <= static_cast<int>(instructions.size()) + 1) {
            decision.effective_join_line = adjusted_join_line;
        }
        decision.chain_fingerprint = ComputeLuaJoinOwnershipMemoFingerprint(
            instructions,
            condition_line,
            branch_target,
            decision.effective_join_line,
            target_register);
    }
    if (have_chain_span) {
        const int adjusted_chain_join_line =
            AdjustLuaSetConditionLineForTarget(instructions,
                                               decision.effective_join_line,
                                               set_condition_target_register);
        if (adjusted_chain_join_line > condition_line + 1 &&
            adjusted_chain_join_line <= static_cast<int>(instructions.size()) + 1) {
            decision.effective_join_line = adjusted_chain_join_line;
        }
    }

    if (control_flow_graph == nullptr) {
        decision.proven = true;
        decision.unambiguous = true;
        decision.commit_kind = LuaJoinOwnershipCommitKind::kCommitAtJoin;
        *decision_out = decision;
        return true;
    }

    LuaJoinOwnershipCommitMemo& memo = GetLuaJoinOwnershipCommitMemo();
    if (memo.instructions_data != instructions.data() ||
        memo.instruction_count != instructions.size() ||
        memo.graph != control_flow_graph) {
        memo.instructions_data = instructions.data();
        memo.instruction_count = instructions.size();
        memo.graph = control_flow_graph;
        memo.cache.clear();
    }

    const LuaJoinOwnershipCommitCacheKey key{
        .condition_line = condition_line,
        .branch_target_line = branch_target,
        .join_line = decision.effective_join_line,
        .target_register = target_register,
        .chain_fingerprint = decision.chain_fingerprint,
    };
    const auto cached = memo.cache.find(key);
    if (cached != memo.cache.end()) {
        *decision_out = cached->second;
        return true;
    }

    const bool use_chain_consumed_span =
        have_chain_span && chain_span.next_line > condition_line + 2;
    const int ownership_consumed_span_start =
        use_chain_consumed_span ? chain_span.chain_start_line : -1;
    const int ownership_consumed_span_end =
        use_chain_consumed_span
            ? std::max(chain_span.chain_start_line, chain_span.next_line - 1)
            : -1;
    LuaConditionalStatementTempOwnership ownership;
    if (!TryClassifyLuaConditionalStatementTempOwnership(control_flow_graph,
                                                         instructions,
                                                         condition_line,
                                                         branch_target,
                                                         decision.effective_join_line,
                                                         target_register,
                                                         ownership_consumed_span_start,
                                                         ownership_consumed_span_end,
                                                         &ownership) ||
        !ownership.proven) {
        memo.cache.emplace(key, decision);
        *decision_out = decision;
        return true;
    }

    decision.proven = true;
    decision.unambiguous = ownership.unambiguous;
    decision.call_value_owned = ownership.call_value_owned;
    decision.effective_join_line =
        ownership.effective_join_line > 0 ? ownership.effective_join_line : decision.effective_join_line;

    const int then_start = have_chain_span && chain_span.next_line > condition_line + 1
                               ? chain_span.next_line
                               : condition_line + 2;
    const int then_end = std::max(then_start - 1, branch_target - 1);
    const int else_start = branch_target;
    const int else_end = std::max(else_start - 1, decision.effective_join_line - 1);
    constexpr int kMaxCommitWriteScanSpan = 256;
    const bool bounded_then_scan = then_end - then_start + 1 <= kMaxCommitWriteScanSpan;
    const bool bounded_else_scan = else_end - else_start + 1 <= kMaxCommitWriteScanSpan;
    const int writes_in_then = bounded_then_scan
                                   ? CountLuaTargetWritesInRange(
                                         instructions, then_start, then_end, target_register)
                                   : 0;
    const int writes_in_else = bounded_else_scan
                                   ? CountLuaTargetWritesInRange(
                                         instructions, else_start, else_end, target_register)
                                   : 0;
    if (writes_in_then > 0 && writes_in_else == 0) {
        decision.commit_kind = LuaJoinOwnershipCommitKind::kCommitInTrueBranch;
    } else if (writes_in_else > 0 && writes_in_then == 0) {
        decision.commit_kind = LuaJoinOwnershipCommitKind::kCommitInFalseBranch;
    } else {
        decision.commit_kind = LuaJoinOwnershipCommitKind::kCommitAtJoin;
    }

    const int ignore_source_end = std::max(condition_line, branch_target - 1);
    if (decision.commit_kind == LuaJoinOwnershipCommitKind::kCommitInTrueBranch &&
        then_start <= then_end &&
        decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                             then_start,
                                             then_end,
                                             condition_line,
                                             ignore_source_end)) {
        decision.proven = false;
        decision.unambiguous = false;
        decision.commit_kind = LuaJoinOwnershipCommitKind::kUnproven;
    }
    if (decision.commit_kind == LuaJoinOwnershipCommitKind::kCommitInFalseBranch &&
        else_start <= else_end &&
        decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                             else_start,
                                             else_end,
                                             condition_line,
                                             ignore_source_end)) {
        decision.proven = false;
        decision.unambiguous = false;
        decision.commit_kind = LuaJoinOwnershipCommitKind::kUnproven;
    }
    if (decision.proven && have_chain_span &&
        decision.effective_join_line >= condition_line &&
        decision.effective_join_line <= static_cast<int>(instructions.size()) + 1 &&
        decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                             decision.effective_join_line,
                                             decision.effective_join_line,
                                             condition_line,
                                             std::max(condition_line,
                                                      decision.effective_join_line - 1))) {
        decision.proven = false;
        decision.unambiguous = false;
        decision.commit_kind = LuaJoinOwnershipCommitKind::kUnproven;
    }

    memo.cache.emplace(key, decision);
    *decision_out = decision;
    return true;
}

bool TryResolveLuaConditionalChainCommit(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    int register_count,
    LuaConditionalChainCommitResolution* resolution_out,
    const Lua51Function* function_hint) {
    if (resolution_out == nullptr ||
        condition_line < 1 ||
        condition_line > instruction_count ||
        max_condition_line < condition_line ||
        max_target_line < condition_line) {
        return false;
    }

    if (function_hint != nullptr &&
        IsScopedLargeHoldoutOwnershipFunction(function_hint)) {
        constexpr int kMaxLargeHoldoutResolverSpan = 160;
        if (max_target_line - condition_line > kMaxLargeHoldoutResolverSpan) {
            return false;
        }
    }

    LuaConditionalChainSpan chain_span;
    if (!TryCollectLuaConditionalChainSpan(control_flow_graph,
                                           instructions,
                                           instruction_count,
                                           condition_line,
                                           max_condition_line,
                                           max_target_line,
                                           expected_target_line,
                                           &chain_span,
                                           function_hint)) {
        return false;
    }

    const int branch_target = ResolveLuaConditionalBranchTargetLine(
        instructions, condition_line, chain_span.join_line);
    const int ownership_target_register =
        ResolveLuaConditionalOwnershipRegisterForInstruction(
            instructions[static_cast<std::size_t>(condition_line - 1)], register_count);
    LuaJoinOwnershipCommitDecision ownership_commit;
    if (!TryBuildLuaJoinOwnershipCommitDecision(control_flow_graph,
                                                instructions,
                                                instruction_count,
                                                condition_line,
                                                branch_target,
                                                chain_span.join_line,
                                                ownership_target_register,
                                                &ownership_commit,
                                                function_hint) ||
        !ownership_commit.proven) {
        return false;
    }

    resolution_out->chain_span = std::move(chain_span);
    resolution_out->ownership_commit = ownership_commit;
    resolution_out->branch_target_line = branch_target;
    resolution_out->proven = true;
    return true;
}

bool TryResolveLuaConditionalRootCommitPacket(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    int register_count,
    int fallback_branch_target_line,
    int fallback_join_line,
    LuaConditionalRootCommitPacket* packet_out,
    const Lua51Function* function_hint) {
    if (packet_out == nullptr ||
        condition_line < 1 ||
        condition_line > instruction_count ||
        static_cast<std::size_t>(condition_line) > instructions.size()) {
        return false;
    }

    LuaConditionalRootCommitPacketMemo& memo = GetLuaConditionalRootCommitPacketMemo();
    if (memo.instructions_data != instructions.data() ||
        memo.instruction_count != instructions.size() ||
        memo.graph != control_flow_graph) {
        memo.instructions_data = instructions.data();
        memo.instruction_count = instructions.size();
        memo.graph = control_flow_graph;
        memo.cache.clear();
    }

    const LuaScriptPolicy policy =
        function_hint != nullptr
            ? BuildLuaScriptPolicy(*function_hint, instruction_count)
            : BuildLuaScriptPolicy(std::string_view{}, 0, instruction_count);
    const std::uint64_t profile_flags = policy.profile.flags;
    const LuaConditionalRootCommitPacketCacheKey cache_key{
        .condition_line = condition_line,
        .max_condition_line = max_condition_line,
        .max_target_line = max_target_line,
        .expected_target_line = expected_target_line,
        .register_count = register_count,
        .fallback_branch_target_line = fallback_branch_target_line,
        .fallback_join_line = fallback_join_line,
        .instruction_count = instruction_count,
        .profile_flags = profile_flags,
    };
    const auto cached = memo.cache.find(cache_key);
    if (cached != memo.cache.end()) {
        if (!cached->second.found) {
            return false;
        }
        *packet_out = cached->second.packet;
        return true;
    }
    const int resolved_branch_target_hint = ResolveLuaConditionalBranchTargetLine(
        instructions,
        condition_line,
        fallback_branch_target_line);
    const int target_register_hint = ResolveLuaConditionalOwnershipRegisterForInstruction(
        instructions[static_cast<std::size_t>(condition_line - 1)],
        register_count);
    constexpr std::size_t kCompatibilityReuseScanLimitDefault = 96;
    constexpr std::size_t kCompatibilityReuseScanLimitBehavior = 128;
    const bool allow_compatibility_cache_scan =
        IsLuaPolicyPreviewCompatibilityReuseEnabled(
            policy, static_cast<std::size_t>(std::max(instruction_count, 0)));
    const std::size_t compatibility_reuse_scan_limit =
        policy.profile.Has(LuaScriptProfileFlag::kBehavior)
            ? kCompatibilityReuseScanLimitBehavior
            : kCompatibilityReuseScanLimitDefault;
    if (allow_compatibility_cache_scan) {
        std::size_t scanned = 0;
        for (const auto& cached_entry : memo.cache) {
            if (scanned++ >= compatibility_reuse_scan_limit) {
                break;
            }
            const LuaConditionalRootCommitPacketCacheValue& cached_value = cached_entry.second;
            if (!cached_value.found) {
                continue;
            }
            if (!IsLuaConditionalRootPacketCompatibilityMatch(instructions,
                                                              cached_value.packet,
                                                              condition_line,
                                                              resolved_branch_target_hint,
                                                              fallback_join_line,
                                                              target_register_hint)) {
                continue;
            }
            memo.cache.emplace(cache_key,
                               LuaConditionalRootCommitPacketCacheValue{
                                   .found = true,
                                   .packet = cached_value.packet,
                               });
            *packet_out = cached_value.packet;
            return true;
        }
    }

    LuaConditionalRootCommitPacket packet;
    packet.branch_target_line = fallback_branch_target_line;
    packet.effective_join_line = fallback_join_line;

    LuaConditionalChainCommitResolution chain_resolution;
    const bool resolved_chain_commit =
        TryResolveLuaConditionalChainCommit(control_flow_graph,
                                            instructions,
                                            instruction_count,
                                            condition_line,
                                            max_condition_line,
                                            max_target_line,
                                            expected_target_line,
                                            register_count,
                                            &chain_resolution,
                                            function_hint) &&
        chain_resolution.proven &&
        chain_resolution.ownership_commit.proven;
    if (resolved_chain_commit) {
        packet.chain_span = std::move(chain_resolution.chain_span);
        packet.chain_proven = true;
        packet.ownership_commit = chain_resolution.ownership_commit;
        packet.branch_target_line =
            chain_resolution.branch_target_line > 0
                ? chain_resolution.branch_target_line
                : fallback_branch_target_line;
        packet.effective_join_line =
            packet.ownership_commit.effective_join_line > 0
                ? packet.ownership_commit.effective_join_line
                : (packet.chain_span.join_line > 0 ? packet.chain_span.join_line
                                                   : fallback_join_line);
    } else {
        const int resolved_branch_target = ResolveLuaConditionalBranchTargetLine(
            instructions,
            condition_line,
            fallback_branch_target_line);
        const int target_register = ResolveLuaConditionalOwnershipRegisterForInstruction(
            instructions[static_cast<std::size_t>(condition_line - 1)],
            register_count);
        LuaJoinOwnershipCommitDecision ownership_commit;
        if (!TryBuildLuaJoinOwnershipCommitDecision(control_flow_graph,
                                                    instructions,
                                                    instruction_count,
                                                    condition_line,
                                                    resolved_branch_target,
                                                    fallback_join_line,
                                                    target_register,
                                                    &ownership_commit,
                                                    function_hint)) {
            memo.cache.emplace(cache_key, LuaConditionalRootCommitPacketCacheValue{});
            return false;
        }
        packet.ownership_commit = ownership_commit;
        packet.branch_target_line =
            resolved_branch_target > 0 ? resolved_branch_target : fallback_branch_target_line;
        packet.effective_join_line =
            ownership_commit.effective_join_line > 0
                ? ownership_commit.effective_join_line
                : fallback_join_line;
    }

    packet.ownership_proven = packet.ownership_commit.proven;
    packet.call_value_owned = packet.ownership_commit.call_value_owned;
    packet.carried_register = packet.ownership_commit.target_register;
    packet.commit_kind = packet.ownership_commit.commit_kind;
    if (packet.effective_join_line <= 0) {
        packet.effective_join_line = fallback_join_line;
    }
    memo.cache.emplace(cache_key,
                       LuaConditionalRootCommitPacketCacheValue{
                           .found = true,
                           .packet = packet,
                       });
    *packet_out = std::move(packet);
    return true;
}

bool TryBuildLuaConditionalChainLink(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int chain_start_line,
    int condition_line,
    int max_target_line,
    int expected_target_line,
    LuaConditionalChainLink* link_out,
    const Lua51Function* function_hint) {
    if (link_out == nullptr ||
        condition_line < 1 ||
        chain_start_line < 1 ||
        chain_start_line > condition_line ||
        static_cast<std::size_t>(condition_line + 1) > instructions.size()) {
        return false;
    }

    const Lua51Instruction& candidate =
        instructions[static_cast<std::size_t>(condition_line - 1)];
    if (!decompiler::IsConditionalBranchOpcode(candidate.opcode)) {
        return false;
    }
    const Lua51Instruction& candidate_jump =
        instructions[static_cast<std::size_t>(condition_line)];
    if (candidate_jump.opcode != Lua51Opcode::kJmp) {
        return false;
    }

    const int jump_target =
        decompiler::ComputeJumpTargetLine(condition_line + 1,
                                          candidate_jump.sbx,
                                          instruction_count);
    const int resolved_jump_target =
        decompiler::ResolveTerminalJumpTarget(instructions, jump_target);
    if (expected_target_line > 0) {
        if (jump_target != expected_target_line &&
            resolved_jump_target != expected_target_line) {
            return false;
        }
    }
    int effective_target =
        (resolved_jump_target > condition_line + 1 &&
         resolved_jump_target <= max_target_line)
            ? resolved_jump_target
            : jump_target;
    int set_condition_target_register = -1;
    if (candidate.opcode == Lua51Opcode::kTestSet) {
        set_condition_target_register =
            candidate.a >= 0 ? candidate.a : candidate.b;
    } else if (candidate.opcode == Lua51Opcode::kTest) {
        set_condition_target_register = candidate.a;
    } else if (candidate.opcode == Lua51Opcode::kEq ||
               candidate.opcode == Lua51Opcode::kLt ||
               candidate.opcode == Lua51Opcode::kLe) {
        set_condition_target_register = -1;
    }
    const int adjusted_effective_target =
        AdjustLuaSetConditionLineForTarget(instructions,
                                           effective_target,
                                           set_condition_target_register);
    if (adjusted_effective_target > condition_line + 1 &&
        adjusted_effective_target <= max_target_line) {
        effective_target = adjusted_effective_target;
    }
    if (effective_target <= condition_line + 1 || effective_target > max_target_line) {
        return false;
    }

    for (int line = chain_start_line;
         line <= condition_line && static_cast<std::size_t>(line) <= instructions.size();
         ++line) {
        const Lua51Instruction& scan_instruction =
            instructions[static_cast<std::size_t>(line - 1)];
        if (scan_instruction.opcode != Lua51Opcode::kJmp) {
            continue;
        }
        const int target = decompiler::ComputeJumpTargetLine(
            line,
            scan_instruction.sbx,
            instruction_count);
        if (target <= line) {
            return false;
        }
    }

    if (control_flow_graph != nullptr) {
        const bool enable_consumed_span_chain_ownership =
            ShouldEnableConditionalChainOwnership(function_hint, instruction_count);
        if (enable_consumed_span_chain_ownership) {
            const int consumed_span_start = chain_start_line;
            const int consumed_span_end = condition_line + 1;
            if (consumed_span_start <= consumed_span_end &&
                decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                                     consumed_span_start,
                                                     consumed_span_end,
                                                     chain_start_line,
                                                     std::max(chain_start_line,
                                                              effective_target - 1))) {
                return false;
            }
            if (effective_target >= 1 &&
                effective_target <= static_cast<int>(instructions.size()) + 1 &&
                decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                                     effective_target,
                                                     effective_target,
                                                     chain_start_line,
                                                     std::max(chain_start_line,
                                                              effective_target - 1))) {
                return false;
            }
        } else if (effective_target - 1 >= chain_start_line &&
                   decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                                        chain_start_line,
                                                        effective_target - 1,
                                                        chain_start_line,
                                                        effective_target - 1)) {
            return false;
        }
    }

    if (candidate.opcode == Lua51Opcode::kTestSet) {
        if (candidate.a < 0 || candidate.b < 0) {
            return false;
        }
        if (candidate.a != candidate.b) {
            constexpr int kMaxConservativeCarriedTestSetSpan = 64;
            if (effective_target - (condition_line + 2) > kMaxConservativeCarriedTestSetSpan) {
                return false;
            }
            // Conservative TESTSET chain support: allow carried target registers only when
            // the carried register is not overwritten before this chain segment rejoins.
            for (int line = condition_line + 2;
                 line < effective_target &&
                 static_cast<std::size_t>(line) <= instructions.size();
                 ++line) {
                if (InstructionWritesRegister(
                        instructions[static_cast<std::size_t>(line - 1)], candidate.a)) {
                    return false;
                }
            }
        }
    }

    link_out->condition_line = condition_line;
    link_out->jump_line = condition_line + 1;
    link_out->jump_target_line = jump_target;
    link_out->resolved_jump_target_line = resolved_jump_target;
    link_out->effective_target_line = effective_target;
    link_out->opcode = candidate.opcode;
    link_out->target_register = candidate.a;
    link_out->source_register = candidate.b;
    return true;
}

bool TryCollectLuaConditionalChainSpan(
    const decompiler::Lua51ControlFlowGraph* control_flow_graph,
    const std::vector<Lua51Instruction>& instructions,
    int instruction_count,
    int chain_start_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    LuaConditionalChainSpan* span_out,
    const Lua51Function* function_hint) {
    if (span_out == nullptr ||
        chain_start_line < 1 ||
        max_condition_line < chain_start_line ||
        max_target_line < chain_start_line ||
        static_cast<std::size_t>(chain_start_line) > instructions.size()) {
        return false;
    }

    const bool enable_ai_core_hotspot_body_ownership =
        ShouldEnablePriorityBodyOwnership(function_hint, instruction_count);
    const bool enable_behavior_body_ownership =
        ShouldEnableBehaviorBodyOwnership(function_hint, instruction_count);
    const bool enable_chain_ownership =
        ShouldEnableConditionalChainOwnership(function_hint, instruction_count);
    LuaConditionalChainSpanMemo& memo = GetLuaConditionalChainSpanMemo();
    if (memo.instructions_data != instructions.data() ||
        memo.instruction_count != instructions.size() ||
        memo.graph != control_flow_graph) {
        memo.instructions_data = instructions.data();
        memo.instruction_count = instructions.size();
        memo.graph = control_flow_graph;
        memo.cache.clear();
    }

    const std::uint64_t profile_flags =
        function_hint != nullptr ? ClassifyLuaScriptProfile(*function_hint, instruction_count).flags
                                 : 0;
    const LuaConditionalChainSpanCacheKey cache_key{
        .chain_start_line = chain_start_line,
        .max_condition_line = max_condition_line,
        .max_target_line = max_target_line,
        .expected_target_line = expected_target_line,
        .instruction_count = instruction_count,
        .profile_flags = profile_flags,
    };
    const auto cached = memo.cache.find(cache_key);
    if (cached != memo.cache.end()) {
        if (!cached->second.found) {
            return false;
        }
        *span_out = cached->second.span;
        return true;
    }

    const auto cache_failure = [&]() {
        memo.cache.emplace(cache_key, LuaConditionalChainSpanCacheValue{});
        return false;
    };

    const bool use_ai_core_chain_budgets =
        enable_ai_core_hotspot_body_ownership || enable_behavior_body_ownership;
    const std::size_t max_chain_links =
        use_ai_core_chain_budgets ? std::size_t{6} : std::size_t{4};
    constexpr int kGenericMaxConsumedSpanLines = 32;
    constexpr int kAiCoreHotspotMaxConsumedSpanLines = 48;

    LuaConditionalChainSpan span;
    span.chain_start_line = chain_start_line;
    int scan_line = chain_start_line;
    int join_line = expected_target_line > 0 ? expected_target_line : -1;
    int consumed_span_end = chain_start_line;
    while (scan_line <= max_condition_line &&
           static_cast<std::size_t>(scan_line) <= instructions.size()) {
        const Lua51Instruction& candidate =
            instructions[static_cast<std::size_t>(scan_line - 1)];
        if (!decompiler::IsConditionalBranchOpcode(candidate.opcode)) {
            break;
        }

        LuaConditionalChainLink chain_link;
        const int link_expected_target = join_line > 0 ? join_line : expected_target_line;
        if (!TryBuildLuaConditionalChainLink(control_flow_graph,
                                             instructions,
                                             instruction_count,
                                             chain_start_line,
                                             scan_line,
                                             max_target_line,
                                             link_expected_target,
                                             &chain_link,
                                             function_hint)) {
            break;
        }

        if (join_line <= 0) {
            join_line = chain_link.effective_target_line;
        } else if (chain_link.effective_target_line != join_line) {
            break;
        }
        const int prospective_consumed_span_end =
            std::max(consumed_span_end, chain_link.jump_line);
        const int max_consumed_span_lines =
            use_ai_core_chain_budgets
                ? kAiCoreHotspotMaxConsumedSpanLines
                : kGenericMaxConsumedSpanLines;
        if (prospective_consumed_span_end - chain_start_line >
            max_consumed_span_lines) {
            break;
        }

        span.links.push_back(std::move(chain_link));
        if (span.links.size() >= max_chain_links) {
            break;
        }
        scan_line = span.links.back().jump_line + 1;
        consumed_span_end = prospective_consumed_span_end;
    }

    if (span.links.empty() || join_line <= 0) {
        return cache_failure();
    }
    if (control_flow_graph != nullptr) {
        const LuaConditionalChainLink& root_link = span.links.front();
        const int root_branch_target = root_link.jump_target_line > 0
                                           ? root_link.jump_target_line
                                           : join_line;
        const Lua51Instruction& root_instruction =
            instructions[static_cast<std::size_t>(root_link.condition_line - 1)];
        int ownership_target_register =
            ResolveLuaConditionalOwnershipRegisterForInstruction(
                root_instruction, instruction_count);
        if (ownership_target_register < 0) {
            ownership_target_register =
                ResolveLuaConditionalOwnershipRegisterForChainLink(
                    root_link, instruction_count);
        }
        LuaConditionalStatementTempOwnership ownership;
        const int ownership_consumed_start =
            enable_chain_ownership ? chain_start_line : -1;
        const int ownership_consumed_end =
            enable_chain_ownership
                ? std::max(chain_start_line, consumed_span_end)
                : -1;
        if (!TryClassifyLuaConditionalStatementTempOwnership(
                control_flow_graph,
                instructions,
                chain_start_line,
                root_branch_target,
                join_line,
                ownership_target_register,
                ownership_consumed_start,
                ownership_consumed_end,
                &ownership) ||
            !ownership.proven) {
            return cache_failure();
        }

        if (enable_chain_ownership) {
            const int consumed_span_final_end =
                std::max(chain_start_line, consumed_span_end);
            const int ignore_source_start = std::max(1, chain_start_line - 1);
            if (consumed_span_final_end >= chain_start_line &&
                decompiler::HasIncomingEdgeIntoRange(*control_flow_graph,
                                                     chain_start_line,
                                                     consumed_span_final_end,
                                                     ignore_source_start,
                                                     consumed_span_final_end)) {
                return cache_failure();
            }

            for (int line = chain_start_line;
                 line <= consumed_span_final_end &&
                 static_cast<std::size_t>(line) <= instructions.size();
                 ++line) {
                const Lua51Instruction& candidate =
                    instructions[static_cast<std::size_t>(line - 1)];
                if (candidate.opcode != Lua51Opcode::kJmp) {
                    continue;
                }
                const int jump_target = decompiler::ComputeJumpTargetLine(
                    line, candidate.sbx, instruction_count);
                if (jump_target <= chain_start_line) {
                    return cache_failure();
                }
            }

            for (const LuaConditionalChainLink& chain_link : span.links) {
                if (chain_link.opcode != Lua51Opcode::kTestSet ||
                    chain_link.target_register < 0 ||
                    chain_link.source_register < 0 ||
                    chain_link.target_register == chain_link.source_register) {
                    continue;
                }

                for (int line = chain_link.jump_line + 1;
                     line < join_line &&
                     static_cast<std::size_t>(line) <= instructions.size();
                     ++line) {
                    bool is_chain_condition_line = false;
                    for (const LuaConditionalChainLink& existing_link : span.links) {
                        if (existing_link.condition_line == line) {
                            is_chain_condition_line = true;
                            break;
                        }
                    }
                    if (is_chain_condition_line) {
                        continue;
                    }

                    if (InstructionWritesRegister(
                            instructions[static_cast<std::size_t>(line - 1)],
                            chain_link.target_register)) {
                        return cache_failure();
                    }
                }
            }
        }
    }

    span.next_line = scan_line;
    span.join_line = join_line;
    memo.cache.emplace(cache_key,
                       LuaConditionalChainSpanCacheValue{
                           .found = true,
                           .span = span,
                       });
    *span_out = std::move(span);
    return true;
}

std::string RenderLuaConditionWithValuesInternal(
    const std::vector<StraightLineValue>& state_values,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    const Lua51Instruction& instruction,
    int line,
    bool prefer_function_boundary_parity) {
    const bool is_actions_scope =
        IsLuaInteractiveCutsceneActionsOwnershipFunction(function);
    const bool is_ai_core_hotspot_scope =
        IsLuaAiCoreHotspotOwnershipFunction(function);
    const auto resolve_condition_instruction_pc = [&]() {
        const int instruction_count = static_cast<int>(function.code.size());
        if (instruction_count <= 0) {
            return 0;
        }

        const auto matches_instruction = [&](int candidate_pc) {
            if (candidate_pc < 1 || candidate_pc > instruction_count) {
                return false;
            }
            const Lua51Instruction candidate = DecodeLua51Instruction(
                function.code[static_cast<std::size_t>(candidate_pc - 1)],
                static_cast<uint32_t>(candidate_pc));
            return candidate.raw == instruction.raw &&
                   candidate.opcode == instruction.opcode;
        };

        // Runtime-first: for non-actions paths, trust incoming line indexes and
        // avoid expensive opcode scanning.
        if (!is_actions_scope) {
            return matches_instruction(line) ? line : 0;
        }

        if (matches_instruction(line)) {
            return line;
        }

        if (line >= 1 && line <= instruction_count) {
            const int scan_begin = std::max(1, line - 16);
            const int scan_end = std::min(instruction_count, line + 16);
            for (int candidate_pc = scan_begin; candidate_pc <= scan_end; ++candidate_pc) {
                if (matches_instruction(candidate_pc)) {
                    return candidate_pc;
                }
            }
        }

        for (int candidate_pc = 1; candidate_pc <= instruction_count; ++candidate_pc) {
            if (matches_instruction(candidate_pc)) {
                return candidate_pc;
            }
        }
        return 0;
    };
    const auto is_register_operand = [](int operand) {
        return !IsConstantReference(operand);
    };
    const auto trim_ascii = [](std::string_view text) -> std::string_view {
        std::size_t begin = 0;
        while (begin < text.size() &&
               std::isspace(static_cast<unsigned char>(text[begin])) != 0) {
            ++begin;
        }
        std::size_t end = text.size();
        while (end > begin &&
               std::isspace(static_cast<unsigned char>(text[end - 1])) != 0) {
            --end;
        }
        return text.substr(begin, end - begin);
    };
    const auto strip_outer_parens = [&](std::string_view text) {
        std::string_view trimmed = trim_ascii(text);
        while (trimmed.size() >= 2 &&
               trimmed.front() == '(' && trimmed.back() == ')') {
            int depth = 0;
            bool encloses_whole = true;
            for (std::size_t index = 0; index < trimmed.size(); ++index) {
                const char ch = trimmed[index];
                if (ch == '(') {
                    ++depth;
                } else if (ch == ')') {
                    --depth;
                    if (depth == 0 && index + 1 < trimmed.size()) {
                        encloses_whole = false;
                        break;
                    }
                }
                if (depth < 0) {
                    encloses_whole = false;
                    break;
                }
            }
            if (!encloses_whole || depth != 0) {
                break;
            }
            trimmed = trim_ascii(trimmed.substr(1, trimmed.size() - 2));
        }
        return std::string(trimmed);
    };
    const int condition_pc = resolve_condition_instruction_pc();
    const auto does_call_touch_register = [](const Lua51Instruction& call_instruction,
                                             int reg,
                                             int register_count) {
        if (reg < 0 || reg < call_instruction.a) {
            return false;
        }

        if (call_instruction.c == 0) {
            return reg < register_count;
        }
        if (call_instruction.c > 1 &&
            reg <= call_instruction.a + call_instruction.c - 2) {
            return true;
        }
        if (call_instruction.b == 0) {
            return reg < register_count;
        }
        return reg < call_instruction.a + call_instruction.b;
    };
    const auto has_recent_call_activity_for_compare = [&](int lhs_reg, int rhs_reg) {
        if (lhs_reg < 0 || rhs_reg < 0 || lhs_reg == rhs_reg ||
            condition_pc <= 1 || function.code.empty()) {
            return false;
        }

        constexpr int kMaxLookback = 10;
        const int instruction_count = static_cast<int>(function.code.size());
        const int scan_end = std::min(condition_pc - 1, instruction_count);
        const int scan_begin = std::max(1, scan_end - kMaxLookback + 1);
        const int register_count = static_cast<int>(function.max_stack_size);
        for (int probe_line = scan_end; probe_line >= scan_begin; --probe_line) {
            const Lua51Instruction probe_instruction = DecodeLua51Instruction(
                function.code[static_cast<std::size_t>(probe_line - 1)],
                static_cast<uint32_t>(probe_line));
            if (probe_instruction.opcode != Lua51Opcode::kCall &&
                probe_instruction.opcode != Lua51Opcode::kTailCall) {
                continue;
            }
            if (does_call_touch_register(probe_instruction, lhs_reg, register_count) ||
                does_call_touch_register(probe_instruction, rhs_reg, register_count)) {
                return true;
            }
        }
        return false;
    };
    const auto resolve_scoped_compare_operand_text = [&](int reg) {
        if (reg < 0 || static_cast<std::size_t>(reg) >= state_values.size()) {
            return std::string();
        }

        const StraightLineValue& preview_value = state_values[static_cast<std::size_t>(reg)];
        if (!preview_value.text.empty()) {
            if (preview_value.valid) {
                return preview_value.text;
            }
            const int identity = ResolveLuaJoinIdentityRegister(
                preview_value.text,
                declarations,
                function,
                line,
                static_cast<int>(state_values.size()));
            if (identity == reg) {
                return preview_value.text;
            }
        }

        const std::string assignment_name =
            GetStraightLineAssignmentRegisterName(declarations, function, reg, line);
        if (!assignment_name.empty()) {
            return assignment_name;
        }
        return GetStraightLineRegisterName(declarations, function, reg, line);
    };
    const auto resolve_scoped_identity_compare_operand_text = [&](int reg) {
        if (reg < 0 || static_cast<std::size_t>(reg) >= state_values.size()) {
            return std::string();
        }
        const StraightLineValue& preview_value = state_values[static_cast<std::size_t>(reg)];
        if (preview_value.text.empty()) {
            return std::string();
        }
        if (preview_value.valid) {
            return preview_value.text;
        }
        const int identity = ResolveLuaJoinIdentityRegister(preview_value.text,
                                                            declarations,
                                                            function,
                                                            line,
                                                            static_cast<int>(state_values.size()));
        return identity == reg ? preview_value.text : std::string();
    };
    const auto try_get_constant_identifier = [&](int rk, std::string* identifier_out) {
        if (identifier_out == nullptr || !IsConstantReference(rk)) {
            return false;
        }
        const int constant_index = rk & 0xFF;
        if (constant_index < 0 ||
            static_cast<std::size_t>(constant_index) >= function.constants.size()) {
            return false;
        }
        const Lua51Constant& constant = function.constants[static_cast<std::size_t>(constant_index)];
        if (constant.type != Lua51ConstantType::kString ||
            !IsValidLuaIdentifier(constant.string_value)) {
            return false;
        }
        *identifier_out = constant.string_value;
        return true;
    };
    const auto try_build_recent_call_result_expression_for_register = [&](int reg) {
        if (reg < 0 || condition_pc < 3 ||
            static_cast<std::size_t>(condition_pc - 1) > function.code.size()) {
            return std::string();
        }

        constexpr int kMaxLookback = 8;
        const int scan_begin = std::max(2, condition_pc - kMaxLookback);
        for (int producer_pc = condition_pc - 1; producer_pc >= scan_begin; --producer_pc) {
            const Lua51Instruction producer_candidate = DecodeLua51Instruction(
                function.code[static_cast<std::size_t>(producer_pc - 1)],
                static_cast<uint32_t>(producer_pc));
            if (!InstructionWritesRegister(producer_candidate, reg)) {
                continue;
            }

            if (producer_candidate.opcode != Lua51Opcode::kCall ||
                producer_candidate.a != reg || producer_candidate.c != 2 ||
                producer_pc <= 1) {
                break;
            }

            bool overwritten_after_call = false;
            for (int probe_pc = producer_pc + 1; probe_pc < condition_pc; ++probe_pc) {
                const Lua51Instruction probe_instruction = DecodeLua51Instruction(
                    function.code[static_cast<std::size_t>(probe_pc - 1)],
                    static_cast<uint32_t>(probe_pc));
                if (InstructionWritesRegister(probe_instruction, reg)) {
                    overwritten_after_call = true;
                    break;
                }
            }
            if (overwritten_after_call) {
                break;
            }

            const Lua51Instruction producer_self = DecodeLua51Instruction(
                function.code[static_cast<std::size_t>(producer_pc - 2)],
                static_cast<uint32_t>(producer_pc - 1));
            if (producer_self.opcode != Lua51Opcode::kSelf ||
                producer_self.a != reg || producer_self.b < 0) {
                break;
            }

            std::string method_name;
            if (!try_get_constant_identifier(producer_self.c, &method_name)) {
                break;
            }

            std::string receiver_expression;
            if (producer_pc >= 3) {
                const Lua51Instruction producer_gettable = DecodeLua51Instruction(
                    function.code[static_cast<std::size_t>(producer_pc - 3)],
                    static_cast<uint32_t>(producer_pc - 2));
                if (producer_gettable.opcode == Lua51Opcode::kGetTable &&
                    producer_gettable.a == producer_self.b &&
                    producer_gettable.b >= 0) {
                    std::string table_field_name;
                    if (try_get_constant_identifier(producer_gettable.c, &table_field_name)) {
                        const StraightLineValue table_owner = GetRegisterValue(
                            state_values, declarations, function, producer_gettable.b, line);
                        if (!table_owner.text.empty()) {
                            receiver_expression = table_owner.text + "." + table_field_name;
                        }
                    }
                }
            }

            if (receiver_expression.empty()) {
                const StraightLineValue receiver = GetRegisterValue(
                    state_values, declarations, function, producer_self.b, line);
                receiver_expression = receiver.text;
            }
            if (receiver_expression.empty()) {
                break;
            }
            return receiver_expression + ":" + method_name + "()";
        }
        return std::string();
    };
    const auto stabilize_scoped_compare_operands = [&](int lhs_operand,
                                                       int rhs_operand,
                                                       std::string* lhs_text,
                                                       std::string* rhs_text) {
        const auto normalized_equal = [&]() {
            return strip_outer_parens(*lhs_text) == strip_outer_parens(*rhs_text);
        };
        if (lhs_text == nullptr || rhs_text == nullptr ||
            !normalized_equal() ||
            !is_register_operand(lhs_operand) ||
            !is_register_operand(rhs_operand) ||
            lhs_operand == rhs_operand) {
            return true;
        }
        const bool recent_call_activity =
            has_recent_call_activity_for_compare(lhs_operand, rhs_operand);

        const std::string lhs_identity_candidate =
            resolve_scoped_identity_compare_operand_text(lhs_operand);
        const std::string rhs_identity_candidate =
            resolve_scoped_identity_compare_operand_text(rhs_operand);
        if (!lhs_identity_candidate.empty()) {
            *lhs_text = lhs_identity_candidate;
        }
        if (!rhs_identity_candidate.empty()) {
            *rhs_text = rhs_identity_candidate;
        }
        if (normalized_equal()) {
            const std::string lhs_candidate = resolve_scoped_compare_operand_text(lhs_operand);
            const std::string rhs_candidate = resolve_scoped_compare_operand_text(rhs_operand);
            if (!lhs_candidate.empty()) {
                *lhs_text = lhs_candidate;
            }
            if (!rhs_candidate.empty()) {
                *rhs_text = rhs_candidate;
            }
        }
        if (normalized_equal() && recent_call_activity) {
            const std::string lhs_call_expression =
                try_build_recent_call_result_expression_for_register(lhs_operand);
            if (!lhs_call_expression.empty() &&
                strip_outer_parens(lhs_call_expression) != strip_outer_parens(*rhs_text)) {
                *lhs_text = lhs_call_expression;
            }
            const std::string rhs_call_expression =
                try_build_recent_call_result_expression_for_register(rhs_operand);
            if (!rhs_call_expression.empty() &&
                strip_outer_parens(rhs_call_expression) != strip_outer_parens(*lhs_text)) {
                *rhs_text = rhs_call_expression;
            }
        }
        if (!normalized_equal()) {
            return true;
        }
        if (normalized_equal() && recent_call_activity) {
            // Avoid stale declaration-alias reuse across call barriers.
            // If distinct registers still collapse after identity recovery,
            // bail out and let fallback render this root conservatively.
            return false;
        }

        const int previous_line = std::max(1, line - 1);
        const std::string lhs_previous_name =
            GetStraightLineRegisterName(declarations, function, lhs_operand, previous_line);
        const std::string rhs_previous_name =
            GetStraightLineRegisterName(declarations, function, rhs_operand, previous_line);
        if (!lhs_previous_name.empty() &&
            !rhs_previous_name.empty() &&
            lhs_previous_name != rhs_previous_name) {
            *lhs_text = lhs_previous_name;
            *rhs_text = rhs_previous_name;
        }
        // If distinct registers still collapse to the same operand text, abort
        // this chain rendering and let fallback handle it.
        return !normalized_equal();
    };
    const auto promote_scoped_call_result_operand = [&](int operand, std::string* text) {
        if (text == nullptr || !is_register_operand(operand)) {
            return;
        }
        const std::string call_expression =
            try_build_recent_call_result_expression_for_register(operand);
        if (call_expression.empty()) {
            return;
        }
        const int identity = ResolveLuaJoinIdentityRegister(
            *text,
            declarations,
            function,
            line,
            static_cast<int>(state_values.size()));
        if (identity == operand) {
            return;
        }
        if (strip_outer_parens(call_expression) != strip_outer_parens(*text)) {
            *text = call_expression;
        }
    };
    const auto try_reuse_local_call_carry_operand = [&](int operand,
                                                        std::string* text) {
        if (text == nullptr || !is_register_operand(operand) ||
            operand < 0 || condition_pc < 2 ||
            static_cast<std::size_t>(operand) >= state_values.size()) {
            return;
        }
        const std::string normalized_text = strip_outer_parens(*text);
        if (normalized_text.empty() || normalized_text.find('(') == std::string::npos ||
            normalized_text.find(')') == std::string::npos) {
            return;
        }
        const std::string local_name = GetStraightLineAssignmentRegisterName(
            declarations, function, operand, line);
        if (local_name.empty() || IsRenderTemporaryName(local_name)) {
            return;
        }
        if (strip_outer_parens(local_name) == normalized_text) {
            return;
        }

        constexpr int kMaxLookback = 8;
        const int instruction_count = static_cast<int>(function.code.size());
        const int scan_end = std::min(condition_pc - 1, instruction_count);
        const int scan_begin = std::max(1, scan_end - kMaxLookback + 1);
        for (int producer_pc = scan_end; producer_pc >= scan_begin; --producer_pc) {
            const Lua51Instruction producer = DecodeLua51Instruction(
                function.code[static_cast<std::size_t>(producer_pc - 1)],
                static_cast<uint32_t>(producer_pc));
            if (!InstructionWritesRegister(producer, operand)) {
                continue;
            }
            if (producer.opcode != Lua51Opcode::kCall ||
                producer.a != operand ||
                producer.c != 2) {
                break;
            }

            bool overwritten_or_ambiguous = false;
            for (int probe_pc = producer_pc + 1; probe_pc < condition_pc; ++probe_pc) {
                const Lua51Instruction probe = DecodeLua51Instruction(
                    function.code[static_cast<std::size_t>(probe_pc - 1)],
                    static_cast<uint32_t>(probe_pc));
                if (InstructionWritesRegister(probe, operand)) {
                    overwritten_or_ambiguous = true;
                    break;
                }
                if (decompiler::IsConditionalBranchOpcode(probe.opcode) ||
                    probe.opcode == Lua51Opcode::kJmp ||
                    probe.opcode == Lua51Opcode::kForPrep ||
                    probe.opcode == Lua51Opcode::kForLoop ||
                    probe.opcode == Lua51Opcode::kTForLoop) {
                    overwritten_or_ambiguous = true;
                    break;
                }
            }
            if (overwritten_or_ambiguous) {
                break;
            }

            *text = local_name;
            return;
        }
    };

    std::string expression;
    switch (instruction.opcode) {
        case Lua51Opcode::kEq: {
            const StraightLineValue lhs =
                GetRKValue(state_values, declarations, function, instruction.b, line);
            const StraightLineValue rhs =
                GetRKValue(state_values, declarations, function, instruction.c, line);
            std::string lhs_text = lhs.text;
            std::string rhs_text = rhs.text;
            try_reuse_local_call_carry_operand(instruction.b, &lhs_text);
            try_reuse_local_call_carry_operand(instruction.c, &rhs_text);
            promote_scoped_call_result_operand(instruction.b, &lhs_text);
            promote_scoped_call_result_operand(instruction.c, &rhs_text);
            if (!stabilize_scoped_compare_operands(
                    instruction.b, instruction.c, &lhs_text, &rhs_text)) {
                return {};
            }
            expression =
                RenderBinaryExpression(lhs_text, instruction.a != 0 ? "~=" : "==", rhs_text);
            break;
        }
        case Lua51Opcode::kLt: {
            const StraightLineValue lhs =
                GetRKValue(state_values, declarations, function, instruction.b, line);
            const StraightLineValue rhs =
                GetRKValue(state_values, declarations, function, instruction.c, line);
            std::string lhs_text = lhs.text;
            std::string rhs_text = rhs.text;
            try_reuse_local_call_carry_operand(instruction.b, &lhs_text);
            try_reuse_local_call_carry_operand(instruction.c, &rhs_text);
            promote_scoped_call_result_operand(instruction.b, &lhs_text);
            promote_scoped_call_result_operand(instruction.c, &rhs_text);
            if (!stabilize_scoped_compare_operands(
                    instruction.b, instruction.c, &lhs_text, &rhs_text)) {
                return {};
            }
            if (instruction.a != 0 && prefer_function_boundary_parity) {
                expression = RenderBinaryExpression(rhs_text, "<=", lhs_text);
            } else {
                expression = RenderBinaryExpression(lhs_text, "<", rhs_text);
                if (instruction.a != 0) {
                    expression = RenderUnaryExpression("not", expression);
                }
            }
            break;
        }
        case Lua51Opcode::kLe: {
            const StraightLineValue lhs =
                GetRKValue(state_values, declarations, function, instruction.b, line);
            const StraightLineValue rhs =
                GetRKValue(state_values, declarations, function, instruction.c, line);
            std::string lhs_text = lhs.text;
            std::string rhs_text = rhs.text;
            try_reuse_local_call_carry_operand(instruction.b, &lhs_text);
            try_reuse_local_call_carry_operand(instruction.c, &rhs_text);
            promote_scoped_call_result_operand(instruction.b, &lhs_text);
            promote_scoped_call_result_operand(instruction.c, &rhs_text);
            if (!stabilize_scoped_compare_operands(
                    instruction.b, instruction.c, &lhs_text, &rhs_text)) {
                return {};
            }
            if (instruction.a != 0 && prefer_function_boundary_parity) {
                expression = RenderBinaryExpression(rhs_text, "<", lhs_text);
            } else {
                expression = RenderBinaryExpression(lhs_text, "<=", rhs_text);
                if (instruction.a != 0) {
                    expression = RenderUnaryExpression("not", expression);
                }
            }
            break;
        }
        case Lua51Opcode::kTest: {
            const StraightLineValue value =
                GetRegisterValue(state_values, declarations, function, instruction.a, line);
            std::string value_text = value.text;
            try_reuse_local_call_carry_operand(instruction.a, &value_text);
            expression = std::move(value_text);
            if (instruction.c != 0) {
                expression = RenderUnaryExpression("not", expression);
            }
            break;
        }
        case Lua51Opcode::kTestSet: {
            const StraightLineValue value =
                GetRegisterValue(state_values, declarations, function, instruction.b, line);
            std::string value_text = value.text;
            try_reuse_local_call_carry_operand(instruction.b, &value_text);
            expression = std::move(value_text);
            if (instruction.c == 0) {
                expression = RenderUnaryExpression("not", expression);
            }
            break;
        }
        default:
            break;
    }
    if (prefer_function_boundary_parity &&
        (instruction.opcode == Lua51Opcode::kTest ||
         instruction.opcode == Lua51Opcode::kTestSet)) {
        const std::string normalized = std::string(TrimLuaWhitespace(expression));
        if (normalized.starts_with("not not ")) {
            const std::string_view operand = TrimLuaWhitespace(normalized.substr(8));
            if (!operand.empty()) {
                expression = std::string(operand);
            }
        }
    }
    return expression;
}

std::string RenderLuaConditionWithValues(const std::vector<StraightLineValue>& state_values,
                                         const RegisterDeclarations& declarations,
                                         const Lua51Function& function,
                                         const Lua51Instruction& instruction,
                                         int line) {
    return RenderLuaConditionWithValuesInternal(state_values,
                                                declarations,
                                                function,
                                                instruction,
                                                line,
                                                false);
}

std::string RenderLuaConditionWithFunctionBoundaryParity(
    const std::vector<StraightLineValue>& state_values,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    const Lua51Instruction& instruction,
    int line) {
    return RenderLuaConditionWithValuesInternal(state_values,
                                                declarations,
                                                function,
                                                instruction,
                                                line,
                                                true);
}

void ApplyLuaConditionalSideEffectsToPreviewValues(
    const Lua51Instruction& instruction,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    int line,
    std::vector<StraightLineValue>* preview_values) {
    if (preview_values == nullptr) {
        return;
    }

    if (instruction.opcode != Lua51Opcode::kTestSet ||
        instruction.a < 0 || instruction.b < 0 ||
        instruction.a == instruction.b) {
        return;
    }

    const std::size_t destination = static_cast<std::size_t>(instruction.a);
    if (destination >= preview_values->size()) {
        return;
    }

    // unluac-style carried TESTSET ownership: once this guarded leg is proven in
    // the chained condition span, propagate the carried register value for later links.
    const StraightLineValue carried_value =
        GetRegisterValue(*preview_values, declarations, function, instruction.b, line);
    (*preview_values)[destination] = carried_value;
}

void ApplyLuaConditionalChainTestSetCarryEffects(
    const std::vector<Lua51Instruction>& instructions,
    const LuaConditionalChainSpan& chain_span,
    const RegisterDeclarations& declarations,
    const Lua51Function& function,
    std::vector<StraightLineValue>* preview_values,
    int carried_register_filter,
    bool apply_once) {
    if (preview_values == nullptr || instructions.empty()) {
        return;
    }

    for (const LuaConditionalChainLink& chain_link : chain_span.links) {
        if (chain_link.condition_line < 1 ||
            static_cast<std::size_t>(chain_link.condition_line) > instructions.size()) {
            continue;
        }
        const Lua51Instruction& condition_instruction =
            instructions[static_cast<std::size_t>(chain_link.condition_line - 1)];
        if (carried_register_filter >= 0 &&
            condition_instruction.opcode == Lua51Opcode::kTestSet &&
            condition_instruction.a != carried_register_filter) {
            continue;
        }
        const int condition_render_line = static_cast<int>(
            condition_instruction.line == 0 ? chain_link.condition_line : condition_instruction.line);
        ApplyLuaConditionalSideEffectsToPreviewValues(condition_instruction,
                                                      declarations,
                                                      function,
                                                      condition_render_line,
                                                      preview_values);
        if (condition_instruction.opcode == Lua51Opcode::kTestSet) {
            if (apply_once) {
                break;
            }
        }
    }
}

void AppendLuaChainedAndConditions(std::string* combined_condition,
                                   int* combined_then_start,
                                   int branch_target,
                                   const decompiler::Lua51ControlFlowGraph* control_flow_graph,
                                   const std::vector<Lua51Instruction>& instructions,
                                   std::vector<StraightLineValue>* preview_values,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   const LuaInlineRangeEvaluator& evaluate_inline_range) {
    if (combined_condition == nullptr || combined_then_start == nullptr || preview_values == nullptr) {
        return;
    }

    const bool use_chain_ownership =
        ShouldEnableConditionalChainOwnership(&function,
                                              static_cast<int>(instructions.size()));
    int scan_line = *combined_then_start;
    while (scan_line <= branch_target - 2 &&
           scan_line <= static_cast<int>(instructions.size())) {
        const Lua51Instruction& candidate =
            instructions[static_cast<std::size_t>(scan_line - 1)];
        if (decompiler::IsConditionalBranchOpcode(candidate.opcode)) {
            if (!use_chain_ownership) {
                if (scan_line + 1 > static_cast<int>(instructions.size())) {
                    break;
                }

                const Lua51Instruction& candidate_jump =
                    instructions[static_cast<std::size_t>(scan_line)];
                if (candidate_jump.opcode != Lua51Opcode::kJmp) {
                    break;
                }

                const int candidate_target = decompiler::ComputeJumpTargetLine(
                    scan_line + 1,
                    candidate_jump.sbx,
                    static_cast<int>(instructions.size()));
                const int resolved_candidate_target =
                    decompiler::ResolveTerminalJumpTarget(instructions, candidate_target);
                if (candidate_target != branch_target &&
                    resolved_candidate_target != branch_target) {
                    break;
                }

                if (candidate.opcode == Lua51Opcode::kTestSet) {
                    break;
                }

                const int candidate_render_line =
                    static_cast<int>(candidate.line == 0 ? scan_line : candidate.line);
                const std::string chained_condition = RenderLuaConditionWithValues(
                    *preview_values,
                    declarations,
                    function,
                    candidate,
                    candidate_render_line);
                if (chained_condition.empty()) {
                    break;
                }

                *combined_condition = RenderLogicalExpression(
                    *combined_condition, "and", chained_condition);
                scan_line += 2;
                *combined_then_start = scan_line;
                continue;
            }

            LuaConditionalChainSpan chain_span;
            if (!TryCollectLuaConditionalChainSpan(control_flow_graph,
                                                   instructions,
                                                   static_cast<int>(instructions.size()),
                                                   *combined_then_start,
                                                   branch_target - 2,
                                                   branch_target,
                                                   branch_target,
                                                   &chain_span,
                                                   &function)) {
                break;
            }

            bool appended_any = false;
            bool stop_chaining = false;
            for (const LuaConditionalChainLink& chain_link : chain_span.links) {
                if (ShouldStopLuaChainedConditionOnFreshTemporary(instructions,
                                                                  declarations,
                                                                  function,
                                                                  *combined_then_start,
                                                                  chain_link.condition_line)) {
                    stop_chaining = true;
                    break;
                }

                const Lua51Instruction& chained_instruction =
                    instructions[static_cast<std::size_t>(chain_link.condition_line - 1)];
                const int candidate_render_line =
                    static_cast<int>(chained_instruction.line == 0
                                         ? chain_link.condition_line
                                         : chained_instruction.line);
                const std::string chained_condition = RenderLuaConditionWithValues(
                    *preview_values,
                    declarations,
                    function,
                    chained_instruction,
                    candidate_render_line);
                if (chained_condition.empty()) {
                    stop_chaining = true;
                    break;
                }

                *combined_condition = RenderLogicalExpression(
                    *combined_condition, "and", chained_condition);
                ApplyLuaConditionalSideEffectsToPreviewValues(chained_instruction,
                                                              declarations,
                                                              function,
                                                              candidate_render_line,
                                                              preview_values);
                *combined_then_start = chain_link.jump_line + 1;
                appended_any = true;
            }

            if (stop_chaining || !appended_any) {
                break;
            }

            scan_line = *combined_then_start;
            continue;
        }

        if (InstructionWritesNamedLocal(candidate, declarations, function, scan_line)) {
            break;
        }

        std::string preview_error;
        if (!evaluate_inline_range(scan_line, scan_line, preview_values, &preview_error)) {
            break;
        }
        ++scan_line;
    }
}

}  // namespace refii::lua
