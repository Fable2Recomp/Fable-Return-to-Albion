#include "tools/decompiler/preview_render.h"

#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/control_flow_blocks.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/operation_build.h"
#include "tools/decompiler/operation_render.h"
#include "tools/decompiler/preview_operation_render.h"
#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <string>
#include <unordered_set>

namespace refii::lua {
namespace {

const LuaScriptPolicy& GetLuaPreviewPolicy(const LuaPreviewRenderContext& context) {
    if (context.cache != nullptr && context.cache->profile_ready) {
        return context.cache->profile_policy;
    }
    if (context.cache != nullptr) {
        context.cache->profile_policy = BuildLuaScriptPolicy(
            context.function, static_cast<int>(context.instructions.size()));
        context.cache->profile_ready = true;
        return context.cache->profile_policy;
    }

    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local std::size_t last_instruction_count = 0;
    static thread_local LuaScriptPolicy last_policy;
    if (&context.function == last_function &&
        context.instructions.size() == last_instruction_count) {
        return last_policy;
    }
    last_policy = BuildLuaScriptPolicy(
        context.function, static_cast<int>(context.instructions.size()));
    last_function = &context.function;
    last_instruction_count = context.instructions.size();
    return last_policy;
}

std::uint64_t MakeLuaPreviewCacheKey(int reg, int line) {
    return (static_cast<std::uint64_t>(static_cast<std::uint32_t>(reg)) << 32U) |
           static_cast<std::uint32_t>(line);
}

std::uint64_t ComputeLuaPreviewFunctionFingerprint(const Lua51Function& function) {
    std::uint64_t hash = 1469598103934665603ull;
    const auto mix = [&](std::uint64_t value) {
        hash ^= value;
        hash *= 1099511628211ull;
    };
    for (const char ch : function.source_name) {
        mix(static_cast<unsigned char>(ch));
    }
    mix(static_cast<std::uint64_t>(static_cast<std::uint32_t>(function.line_defined)));
    mix(static_cast<std::uint64_t>(static_cast<std::uint32_t>(function.last_line_defined)));
    mix(static_cast<std::uint64_t>(static_cast<std::uint32_t>(function.parameter_count)));
    mix(static_cast<std::uint64_t>(function.max_stack_size));
    return hash;
}

std::uint64_t MakeLuaPreviewConditionalResolutionCacheKey(int condition_line,
                                                          int max_condition_line,
                                                          int max_target_line,
                                                          int expected_target_line,
                                                          int instruction_count,
                                                          std::uint64_t function_fingerprint) {
    std::uint64_t key = static_cast<std::uint32_t>(condition_line);
    key = key * 1315423911ull + static_cast<std::uint32_t>(max_condition_line);
    key = key * 1315423911ull + static_cast<std::uint32_t>(max_target_line);
    key = key * 1315423911ull +
          static_cast<std::uint32_t>(expected_target_line + 0x9e37);
    key = key * 1315423911ull + static_cast<std::uint32_t>(instruction_count);
    key = key * 1315423911ull + (function_fingerprint & 0xffffffffull);
    key = key * 1315423911ull + ((function_fingerprint >> 32U) & 0xffffffffull);
    return key;
}

std::uint64_t MakeLuaPreviewConditionalRootPacketCacheKey(
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    int fallback_branch_target_line,
    int fallback_join_line,
    int instruction_count,
    std::uint64_t function_fingerprint) {
    std::uint64_t key = MakeLuaPreviewConditionalResolutionCacheKey(condition_line,
                                                                    max_condition_line,
                                                                    max_target_line,
                                                                    expected_target_line,
                                                                    instruction_count,
                                                                    function_fingerprint);
    key = key * 1315423911ull +
          static_cast<std::uint32_t>(fallback_branch_target_line + 0x9e37);
    key = key * 1315423911ull +
          static_cast<std::uint32_t>(fallback_join_line + 0x9e37);
    return key;
}

int ResolveLuaPreviewConditionalBranchTargetLine(
    const std::vector<Lua51Instruction>& instructions,
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

bool IsLuaPreviewScopedSmallHoldoutFastPathEnabled(
    const LuaPreviewRenderContext& context) {
    const LuaScriptPolicy policy = GetLuaPreviewPolicy(context);
    return IsLuaPolicyScopedSmallHoldoutFastPathEnabled(policy, context.instructions.size());
}

bool ShouldUseLuaPreviewCanonicalLoadBoolSkip(const LuaPreviewRenderContext& context) {
    (void)context;
    // LOADBOOL with C != 0 always skips the next instruction in Lua 5.1.
    return true;
}

bool IsLuaPreviewScopedLargeHoldoutFastPathEnabled(
    const LuaPreviewRenderContext& context) {
    const LuaScriptPolicy policy = GetLuaPreviewPolicy(context);
    return IsLuaPolicyScopedLargeHoldoutFastPathEnabled(policy, context.instructions.size());
}

bool IsLuaPreviewPriorityOwnershipFunction(const LuaPreviewRenderContext& context) {
    const LuaScriptPolicy policy = GetLuaPreviewPolicy(context);
    return IsLuaPolicyPriorityOwnershipFastPathEnabled(policy, context.instructions.size());
}

bool IsLuaPreviewGenericBehaviorJoinLocalFunction(
    const LuaPreviewRenderContext& context) {
    const LuaScriptPolicy policy = GetLuaPreviewPolicy(context);
    return IsLuaPolicyGenericBehaviorFastPathEnabled(policy, context.instructions.size());
}

int GetLuaPreviewScopedChainScanLimit(const LuaPreviewRenderContext& context) {
    const LuaScriptPolicy policy = GetLuaPreviewPolicy(context);
    return policy.preview_chain_scan_limit;
}

bool IsLuaPreviewRootCommitPacketFastPathEnabled(const LuaPreviewRenderContext& context) {
    (void)context;
    return true;
}

bool IsLuaPreviewCheapConditionalRootShape(const LuaPreviewRenderContext& context,
                                           int condition_line,
                                           int join_line) {
    if (condition_line < 1 ||
        static_cast<std::size_t>(condition_line + 1) > context.instructions.size()) {
        return false;
    }
    const Lua51Instruction& root =
        context.instructions[static_cast<std::size_t>(condition_line - 1)];
    if (!decompiler::IsConditionalBranchOpcode(root.opcode)) {
        return false;
    }
    if (!IsLuaPreviewRootCommitPacketFastPathEnabled(context)) {
        return false;
    }
    const Lua51Instruction& jump =
        context.instructions[static_cast<std::size_t>(condition_line)];
    if (jump.opcode != Lua51Opcode::kJmp) {
        return false;
    }
    const LuaScriptPolicy policy = GetLuaPreviewPolicy(context);
    const bool scoped_fast_path =
        IsLuaPreviewScopedSmallHoldoutFastPathEnabled(context);
    const bool hotspot_fast_path =
        IsLuaPreviewPriorityOwnershipFunction(context);
    const bool generic_behavior_fast_path = IsLuaPreviewGenericBehaviorJoinLocalFunction(context);
    const auto is_supported_root_opcode = [&](Lua51Opcode opcode) {
        return opcode == Lua51Opcode::kTest ||
               opcode == Lua51Opcode::kTestSet ||
               opcode == Lua51Opcode::kEq ||
               opcode == Lua51Opcode::kLt ||
               opcode == Lua51Opcode::kLe;
    };
    if (!scoped_fast_path && !hotspot_fast_path &&
        !generic_behavior_fast_path) {
        // Keep global packet resolution conservative to protect runtime and avoid broad drift.
        if (!is_supported_root_opcode(root.opcode)) {
            return false;
        }
        if (join_line <= condition_line || join_line - condition_line > policy.preview_root_shape_span) {
            return false;
        }
    } else if (hotspot_fast_path || generic_behavior_fast_path) {
        if (!is_supported_root_opcode(root.opcode)) {
            return false;
        }
        if (join_line <= condition_line || join_line - condition_line > policy.preview_root_shape_span) {
            return false;
        }
    }
    if (join_line > condition_line) {
        const int max_span = GetLuaPreviewScopedChainScanLimit(context);
        if (join_line - condition_line > max_span) {
            return false;
        }
    }
    return true;
}

bool IsLuaPreviewRootPacketJoinCompatible(const LuaPreviewRenderContext& context,
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
        static_cast<std::size_t>(upper) > context.instructions.size()) {
        return false;
    }
    for (int line = lower; line <= upper; ++line) {
        const Lua51Instruction& instruction =
            context.instructions[static_cast<std::size_t>(line - 1)];
        if (instruction.opcode != Lua51Opcode::kLoadBool &&
            instruction.opcode != Lua51Opcode::kJmp) {
            return false;
        }
    }
    return true;
}

bool IsLuaPreviewRootPacketCompatibilityMatch(const LuaPreviewRenderContext& context,
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
    return IsLuaPreviewRootPacketJoinCompatible(context, packet, fallback_join_line);
}

}  // namespace

bool TryResolveLuaPreviewConditionalCommitForRoot(
    const LuaPreviewRenderContext& context,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    LuaConditionalChainCommitResolution* resolution_out) {
    if (resolution_out == nullptr ||
        condition_line < 1 ||
        max_condition_line < condition_line ||
        max_target_line < condition_line) {
        return false;
    }

    const int chain_scan_limit = GetLuaPreviewScopedChainScanLimit(context);
    if (max_target_line - condition_line > chain_scan_limit) {
        return false;
    }

    const std::uint64_t cache_key = MakeLuaPreviewConditionalResolutionCacheKey(
        condition_line,
        max_condition_line,
        max_target_line,
        expected_target_line,
        static_cast<int>(context.instructions.size()),
        ComputeLuaPreviewFunctionFingerprint(context.function));
    if (context.cache != nullptr) {
        const auto ready = context.cache->conditional_commit_resolution_ready.find(cache_key);
        if (ready != context.cache->conditional_commit_resolution_ready.end()) {
            if (!ready->second) {
                return false;
            }
            const auto cached =
                context.cache->conditional_commit_resolution_cache.find(cache_key);
            if (cached == context.cache->conditional_commit_resolution_cache.end()) {
                return false;
            }
            *resolution_out = cached->second;
            return true;
        }
    }

    LuaConditionalChainCommitResolution resolution;
    const bool resolved = TryResolveLuaConditionalChainCommit(
        context.control_flow_graph,
        context.instructions,
        static_cast<int>(context.instructions.size()),
        condition_line,
        max_condition_line,
        max_target_line,
        expected_target_line,
        context.register_count,
        &resolution,
        &context.function);
    if (context.cache != nullptr) {
        context.cache->conditional_commit_resolution_ready[cache_key] = resolved;
        if (resolved) {
            context.cache->conditional_commit_resolution_cache[cache_key] = resolution;
        }
    }
    if (!resolved) {
        return false;
    }

    *resolution_out = std::move(resolution);
    return true;
}

bool TryResolveLuaPreviewConditionalRootCommitPacket(
    const LuaPreviewRenderContext& context,
    int condition_line,
    int max_condition_line,
    int max_target_line,
    int expected_target_line,
    int fallback_branch_target_line,
    int fallback_join_line,
    LuaConditionalRootCommitPacket* packet_out) {
    if (packet_out == nullptr ||
        condition_line < 1 ||
        static_cast<std::size_t>(condition_line) > context.instructions.size()) {
        return false;
    }
    if (!IsLuaPreviewRootCommitPacketFastPathEnabled(context)) {
        return false;
    }
    if (!IsLuaPreviewCheapConditionalRootShape(context, condition_line, fallback_join_line)) {
        return false;
    }

    const std::uint64_t cache_key = MakeLuaPreviewConditionalRootPacketCacheKey(
        condition_line,
        max_condition_line,
        max_target_line,
        expected_target_line,
        fallback_branch_target_line,
        fallback_join_line,
        static_cast<int>(context.instructions.size()),
        ComputeLuaPreviewFunctionFingerprint(context.function));
    if (context.cache != nullptr) {
        const auto ready =
            context.cache->conditional_root_commit_packet_ready.find(cache_key);
        if (ready != context.cache->conditional_root_commit_packet_ready.end()) {
            if (!ready->second) {
                return false;
            }
            const auto cached =
                context.cache->conditional_root_commit_packet_cache.find(cache_key);
            if (cached == context.cache->conditional_root_commit_packet_cache.end()) {
                return false;
            }
            *packet_out = cached->second;
            return true;
        }
        const int resolved_branch_target_hint = ResolveLuaPreviewConditionalBranchTargetLine(
            context.instructions,
            condition_line,
            fallback_branch_target_line);
        const int target_register_hint = ResolveLuaConditionalOwnershipRegisterForInstruction(
            context.instructions[static_cast<std::size_t>(condition_line - 1)],
            context.register_count);
        const LuaScriptPolicy policy = GetLuaPreviewPolicy(context);
        constexpr std::size_t kCompatibilityReuseScanLimitDefault = 96;
        constexpr std::size_t kCompatibilityReuseScanLimitBehavior = 128;
        const bool allow_compatibility_cache_scan =
            IsLuaPolicyPreviewCompatibilityReuseEnabled(policy, context.instructions.size());
        const std::size_t compatibility_reuse_scan_limit =
            policy.profile.Has(LuaScriptProfileFlag::kBehavior)
                ? kCompatibilityReuseScanLimitBehavior
                : kCompatibilityReuseScanLimitDefault;
        if (allow_compatibility_cache_scan) {
            std::size_t scanned = 0;
            for (const auto& cached_packet_entry :
                 context.cache->conditional_root_commit_packet_cache) {
                if (scanned++ >= compatibility_reuse_scan_limit) {
                    break;
                }
                const LuaConditionalRootCommitPacket& cached_packet = cached_packet_entry.second;
                if (!IsLuaPreviewRootPacketCompatibilityMatch(context,
                                                              cached_packet,
                                                              condition_line,
                                                              resolved_branch_target_hint,
                                                              fallback_join_line,
                                                              target_register_hint)) {
                    continue;
                }
                context.cache->conditional_root_commit_packet_ready[cache_key] = true;
                context.cache->conditional_root_commit_packet_cache[cache_key] = cached_packet;
                *packet_out = cached_packet;
                return true;
            }
        }
    }

    LuaConditionalRootCommitPacket packet;
    packet.branch_target_line = fallback_branch_target_line;
    packet.effective_join_line = fallback_join_line;

    LuaConditionalChainCommitResolution chain_resolution;
    const bool resolved_chain =
        TryResolveLuaPreviewConditionalCommitForRoot(context,
                                                     condition_line,
                                                     max_condition_line,
                                                     max_target_line,
                                                     expected_target_line,
                                                     &chain_resolution) &&
        chain_resolution.proven &&
        chain_resolution.ownership_commit.proven;
    const bool scoped_fast_path =
        IsLuaPreviewScopedSmallHoldoutFastPathEnabled(context) ||
        IsLuaPreviewScopedLargeHoldoutFastPathEnabled(context);
    if (resolved_chain) {
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
        // Near-exact global lane: allow conservative fallback commit packet
        // resolution for cheap-shape roots, even outside scoped holdouts.
        // Budgets remain bounded by IsLuaPreviewCheapConditionalRootShape and
        // GetLuaPreviewScopedChainScanLimit checks above.
        (void)scoped_fast_path;
        const int instruction_count = static_cast<int>(context.instructions.size());
        const int resolved_branch_target = ResolveLuaPreviewConditionalBranchTargetLine(
            context.instructions,
            condition_line,
            fallback_branch_target_line);
        const int target_register = ResolveLuaConditionalOwnershipRegisterForInstruction(
            context.instructions[static_cast<std::size_t>(condition_line - 1)],
            context.register_count);
        LuaJoinOwnershipCommitDecision ownership_commit;
        if (!TryBuildLuaJoinOwnershipCommitDecision(context.control_flow_graph,
                                                    context.instructions,
                                                    instruction_count,
                                                    condition_line,
                                                    resolved_branch_target,
                                                    fallback_join_line,
                                                    target_register,
                                                    &ownership_commit,
                                                    &context.function)) {
            if (context.cache != nullptr) {
                context.cache->conditional_root_commit_packet_ready[cache_key] = false;
            }
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
    if (context.cache != nullptr) {
        context.cache->conditional_root_commit_packet_ready[cache_key] = true;
        context.cache->conditional_root_commit_packet_cache[cache_key] = packet;
    }
    *packet_out = std::move(packet);
    return true;
}

std::vector<bool> GetLuaPreviewWrittenRegistersForLine(const LuaPreviewRenderContext& context,
                                                       int line) {
    if (line < 1 || static_cast<std::size_t>(line) > context.instructions.size()) {
        return {};
    }

    LuaPreviewRenderCache* const cache = context.cache;
    if (cache == nullptr) {
        return CollectWrittenLuaRegistersInRange(
            context.instructions, context.register_count, line, line);
    }

    const std::size_t cache_index = static_cast<std::size_t>(line);
    if (cache->written_registers_ready_for_line.size() <= cache_index) {
        cache->written_registers_ready_for_line.resize(cache_index + 1, false);
    }
    if (cache->written_registers_for_line.size() <= cache_index) {
        cache->written_registers_for_line.resize(cache_index + 1);
    }
    if (!cache->written_registers_ready_for_line[cache_index]) {
        cache->written_registers_for_line[cache_index] = CollectWrittenLuaRegistersInRange(
            context.instructions, context.register_count, line, line);
        cache->written_registers_ready_for_line[cache_index] = true;
    }
    return cache->written_registers_for_line[cache_index];
}

bool LuaPreviewWritesRegisterAtLine(const LuaPreviewRenderContext& context, int reg, int line) {
    if (reg < 0) {
        return false;
    }

    const std::vector<bool> written = GetLuaPreviewWrittenRegistersForLine(context, line);
    return static_cast<std::size_t>(reg) < written.size() &&
           written[static_cast<std::size_t>(reg)];
}

std::string GetLuaPreviewFallbackMaterializationName(const LuaPreviewRenderContext& context,
                                                     int reg,
                                                     int line) {
    if (reg < 0) {
        return {};
    }

    LuaPreviewRenderCache* const cache = context.cache;
    const std::uint64_t cache_key = MakeLuaPreviewCacheKey(reg, line);
    if (cache != nullptr) {
        const auto found = cache->materialization_names.find(cache_key);
        if (found != cache->materialization_names.end()) {
            return found->second;
        }
    }

    const std::string assignment_name =
        GetStraightLineAssignmentRegisterName(context.declarations, context.function, reg, line);
    if (!assignment_name.empty() && !IsRenderTemporaryName(assignment_name)) {
        if (cache != nullptr) {
            cache->materialization_names.emplace(cache_key, assignment_name);
        }
        return assignment_name;
    }

    const std::string register_name =
        GetStraightLineRegisterName(context.declarations, context.function, reg, line);
    if (!register_name.empty() && !IsRenderTemporaryName(register_name)) {
        if (cache != nullptr) {
            cache->materialization_names.emplace(cache_key, register_name);
        }
        return register_name;
    }

    if (cache != nullptr) {
        cache->materialization_names.emplace(cache_key, std::string());
    }
    return {};
}

int FindLuaPreviewFallbackMaterializationLine(const LuaPreviewRenderContext& context,
                                              int reg,
                                              int assignment_line) {
    if (reg < 0) {
        return assignment_line;
    }

    LuaPreviewRenderCache* const cache = context.cache;
    const std::uint64_t cache_key = MakeLuaPreviewCacheKey(reg, assignment_line);
    if (cache != nullptr) {
        const auto found = cache->materialization_lines.find(cache_key);
        if (found != cache->materialization_lines.end()) {
            return found->second;
        }
    }

    const auto has_meaningful_name_at = [&](int line) {
        return !GetLuaPreviewFallbackMaterializationName(context, reg, line).empty();
    };

    int materialization_line = assignment_line;
    if (!has_meaningful_name_at(assignment_line)) {
        const int instruction_count = static_cast<int>(context.instructions.size());
        for (int line = assignment_line + 1; line <= instruction_count; ++line) {
            if (LuaPreviewWritesRegisterAtLine(context, reg, line)) {
                break;
            }
            if (has_meaningful_name_at(line)) {
                materialization_line = line;
                break;
            }
        }
    }

    if (cache != nullptr) {
        cache->materialization_lines.emplace(cache_key, materialization_line);
    }
    return materialization_line;
}

namespace {

std::vector<bool> CollectLuaPreviewJoinWrittenRegisters(
    const LuaPreviewRenderContext& context,
    int start_line,
    int end_line,
    int statement_line) {
    std::vector<bool> written_registers =
        GetLuaPreviewWrittenRegistersForLine(context, statement_line);
    if (start_line > end_line) {
        return written_registers;
    }

    const std::vector<bool> range_written_registers =
        CollectWrittenLuaRegistersInRange(
            context.instructions, context.register_count, start_line, end_line);
    const std::size_t register_limit =
        std::min(written_registers.size(), range_written_registers.size());
    for (std::size_t register_index = 0; register_index < register_limit; ++register_index) {
        written_registers[register_index] =
            written_registers[register_index] || range_written_registers[register_index];
    }
    return written_registers;
}

bool RequiresLuaPreviewFullTailRender(const std::vector<Lua51Instruction>& instructions,
                                      int start_line,
                                      int statement_line) {
    if (start_line >= statement_line) {
        return false;
    }

    for (int current = start_line; current < statement_line; ++current) {
        if (current < 1 || static_cast<std::size_t>(current) > instructions.size()) {
            break;
        }

        switch (instructions[static_cast<std::size_t>(current - 1)].opcode) {
            case Lua51Opcode::kNewTable:
            case Lua51Opcode::kSetTable:
            case Lua51Opcode::kSetList:
            case Lua51Opcode::kClosure:
                return true;
            case Lua51Opcode::kCall:
            case Lua51Opcode::kTailCall:
                if (current + 1 != statement_line) {
                    return true;
                }
                break;
            default:
                break;
        }
    }

    return false;
}

template <typename ErrorBuilder>
bool TryApplyLuaPreviewBuiltOperation(const LuaPreviewRenderContext& context,
                                      int current,
                                      int line,
                                      std::vector<StraightLineValue>* preview_values,
                                      std::vector<bool>* preview_declared,
                                      std::string* preview_error,
                                      int* next_current,
                                      ErrorBuilder&& error_builder) {
    if (preview_values == nullptr || next_current == nullptr) {
        return false;
    }

    std::vector<bool> default_declared;
    if (preview_declared == nullptr) {
        default_declared.resize(static_cast<std::size_t>(context.register_count), false);
        preview_declared = &default_declared;
    }

    const LuaPreviewOperationContext preview_context{
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .values = preview_values,
        .declared = preview_declared,
        .call_arguments_for = context.call_arguments_for,
        .render_child = context.render_child,
    };

    LuaPreviewOperationDisposition disposition = LuaPreviewOperationDisposition::kBuildFailure;
    std::string build_error;
    if (!TryApplyLuaPreviewOperation(
            preview_context,
            current,
            line,
            LuaPreviewOperationMode::kStateTracking,
            &disposition,
            &build_error,
            next_current)) {
        if (!build_error.empty() && preview_error != nullptr) {
            *preview_error = error_builder(build_error);
        } else if (preview_error != nullptr) {
            switch (disposition) {
                case LuaPreviewOperationDisposition::kUnsupportedTailCall:
                    *preview_error = error_builder("tailcall");
                    break;
                case LuaPreviewOperationDisposition::kUnsupportedCallShape:
                    *preview_error = error_builder("call_shape");
                    break;
                case LuaPreviewOperationDisposition::kUnsupportedOpcode:
                case LuaPreviewOperationDisposition::kUnsupportedSideEffect:
                    *preview_error = error_builder("opcode");
                    break;
                case LuaPreviewOperationDisposition::kApplied:
                case LuaPreviewOperationDisposition::kBuildFailure:
                    break;
            }
        }
        return false;
    }
    return true;
}

}  // namespace

bool EvaluateLuaInlineRange(const LuaPreviewRenderContext& context,
                            int start_line,
                            int end_line,
                            std::vector<StraightLineValue>* preview_values,
                            std::vector<bool>* preview_declared,
                            std::string* preview_error) {
    if (start_line > end_line) {
        return true;
    }

    for (int current = start_line; current <= end_line; ++current) {
        if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
            if (preview_error != nullptr) {
                *preview_error = "invalid_preview_line";
            }
            return false;
        }

        const Lua51Instruction& instruction =
            context.instructions[static_cast<std::size_t>(current - 1)];
        const int line = static_cast<int>(instruction.line == 0 ? current : instruction.line);
        int next_current = current + 1;
        if (TryApplyLuaPreviewBuiltOperation(
                context,
                current,
                line,
                preview_values,
                preview_declared,
                preview_error,
                &next_current,
                [](std::string_view reason) {
                    if (reason == "tailcall") {
                        return std::string("unsupported_preview_tailcall");
                    }
                    if (reason == "call_shape") {
                        return std::string("unsupported_preview_call_shape");
                    }
                    if (reason == "unsupported_nested_closure") {
                        return std::string("unsupported_preview_nested_closure");
                    }
                    if (reason == "invalid_constant_index") {
                        return std::string("invalid_preview_constant_index");
                    }
                    if (reason == "invalid_global_name") {
                        return std::string("invalid_preview_global_name");
                    }
                    if (reason == "opcode") {
                        return std::string("unsupported_preview_opcode");
                    }
                    return std::string(reason);
                })) {
            // LOADBOOL with a skip flag always skips the next instruction. Some
            // valid skip forms are not paired LOADBOOL patterns.
            if (instruction.opcode == Lua51Opcode::kLoadBool &&
                instruction.c != 0 &&
                (IsLoadBoolPair(context.instructions, current) ||
                 ShouldUseLuaPreviewCanonicalLoadBoolSkip(context)) &&
                next_current <= current + 1) {
                next_current = std::min(end_line + 1, current + 2);
            }
            current = next_current - 1;
            continue;
        }
        switch (instruction.opcode) {
            case Lua51Opcode::kLoadBool:
                if (instruction.c != 0) {
                    ++current;
                }
                break;
            case Lua51Opcode::kSetList:
                if (instruction.c == 0) {
                    ++current;
                }
                break;
            case Lua51Opcode::kClose:
                break;
            default:
                if (preview_error != nullptr) {
                    *preview_error = "unsupported_preview_opcode";
                }
                return false;
        }
    }

    return true;
}

bool EvaluateLuaBranchPathToJoin(const LuaPreviewRenderContext& context,
                                 int start_line,
                                 int join_line,
                                 std::vector<StraightLineValue>* preview_values,
                                 std::vector<bool>* preview_declared,
                                 std::string* preview_error) {
    if (preview_values == nullptr) {
        return false;
    }
    if (start_line < 1 || join_line < start_line ||
        static_cast<std::size_t>(join_line) > context.instructions.size()) {
        if (preview_error != nullptr) {
            *preview_error = "invalid_branch_preview_range";
        }
        return false;
    }

    std::unordered_set<int> visited_lines;
    int current = start_line;
    int steps = 0;
    const int max_steps = std::max<int>(16, join_line - start_line + 4);

    while (current < join_line) {
        if (current < 1 || static_cast<std::size_t>(current) > context.instructions.size()) {
            if (preview_error != nullptr) {
                *preview_error = "invalid_branch_preview_line";
            }
            return false;
        }
        if (!visited_lines.insert(current).second || ++steps > max_steps) {
            if (preview_error != nullptr) {
                *preview_error = "branch_preview_cycle";
            }
            return false;
        }

        const Lua51Instruction& instruction =
            context.instructions[static_cast<std::size_t>(current - 1)];
        const int line = static_cast<int>(instruction.line == 0 ? current : instruction.line);
        int next_current = current + 1;
        auto try_consume_root_packet_chain = [&](const LuaConditionalRootCommitPacket& root_packet) {
            if (!root_packet.chain_proven || !root_packet.ownership_proven) {
                return false;
            }
            const int carried_register_filter =
                root_packet.carried_register >= 0 ? root_packet.carried_register : -1;
            ApplyLuaConditionalChainTestSetCarryEffects(context.instructions,
                                                        root_packet.chain_span,
                                                        context.declarations,
                                                        context.function,
                                                        preview_values,
                                                        carried_register_filter,
                                                        true);
            const int resolved_join_line =
                root_packet.chain_span.join_line > 0
                    ? root_packet.chain_span.join_line
                    : root_packet.effective_join_line;
            if (resolved_join_line <= 0) {
                return false;
            }
            current = resolved_join_line;
            return true;
        };
    const auto try_resolve_and_consume_root_packet_chain = [&](int condition_line) {
        if (!IsLuaPreviewCheapConditionalRootShape(context, condition_line, join_line)) {
            return false;
        }
        LuaConditionalRootCommitPacket root_packet;
        return TryResolveLuaPreviewConditionalRootCommitPacket(context,
                                                               condition_line,
                                                                   join_line - 1,
                                                                   join_line,
                                                                   -1,
                                                                   join_line,
                                                                   join_line,
                                                                   &root_packet) &&
                   try_consume_root_packet_chain(root_packet);
        };

        switch (instruction.opcode) {
            case Lua51Opcode::kJmp: {
                const int target = decompiler::ComputeJumpTargetLine(
                    current, instruction.sbx, static_cast<int>(context.instructions.size()));
                if (target < current + 1 || target > join_line) {
                    if (preview_error != nullptr) {
                        *preview_error = "unsupported_branch_preview_jump";
                    }
                    return false;
                }
                current = target;
                continue;
            }
            case Lua51Opcode::kClose:
                ++current;
                continue;
            case Lua51Opcode::kTest: {
                if (try_resolve_and_consume_root_packet_chain(current)) {
                    continue;
                }
                if (current + 1 > static_cast<int>(context.instructions.size()) ||
                    context.instructions[static_cast<std::size_t>(current)].opcode != Lua51Opcode::kJmp) {
                    if (preview_error != nullptr) {
                        *preview_error = "unsupported_branch_preview_test_shape";
                    }
                    return false;
                }

                const Lua51Instruction& jump =
                    context.instructions[static_cast<std::size_t>(current)];
                const int target = decompiler::ComputeJumpTargetLine(
                    current + 1, jump.sbx, static_cast<int>(context.instructions.size()));
                if (target > current + 1 && target <= join_line) {
                    current = target;
                    continue;
                }
                if (target == current + 3 &&
                    current + 2 <= static_cast<int>(context.instructions.size())) {
                    const Lua51Instruction& fallback =
                        context.instructions[static_cast<std::size_t>(current + 1)];
                    if (fallback.opcode == Lua51Opcode::kLoadNil &&
                        fallback.a == instruction.a &&
                        fallback.b == instruction.a) {
                        current = target;
                        continue;
                    }
                }

                if (preview_error != nullptr) {
                    *preview_error = "unsupported_branch_preview_conditional";
                }
                return false;
            }
            case Lua51Opcode::kEq:
            case Lua51Opcode::kLt:
            case Lua51Opcode::kLe: {
                if (!try_resolve_and_consume_root_packet_chain(current)) {
                    if (preview_error != nullptr) {
                        *preview_error = "unsupported_branch_preview_conditional";
                    }
                    return false;
                }
                continue;
            }
            case Lua51Opcode::kTestSet: {
                if (!try_resolve_and_consume_root_packet_chain(current)) {
                    if (preview_error != nullptr) {
                        *preview_error = "unsupported_branch_preview_conditional";
                    }
                    return false;
                }
                continue;
            }
            default:
                if (TryApplyLuaPreviewBuiltOperation(
                        context,
                        current,
                        line,
                        preview_values,
                        preview_declared,
                        preview_error,
                        &next_current,
                        [&](std::string_view reason) {
                            if (reason == "tailcall") {
                                return std::string("unsupported_branch_preview_tailcall");
                            }
                            if (reason == "call_shape") {
                                return std::string("unsupported_branch_preview_call_shape");
                            }
                            if (reason == "unsupported_nested_closure") {
                                return std::string("unsupported_branch_preview_nested_closure");
                            }
                            if (reason == "invalid_constant_index") {
                                return std::string("invalid_branch_preview_constant_index");
                            }
                            if (reason == "invalid_global_name") {
                                return std::string("invalid_branch_preview_global_name");
                            }
                            if (reason == "opcode") {
                                return std::string("unsupported_branch_preview_opcode");
                            }
                            return std::string(reason);
                        })) {
                    current = next_current;
                    continue;
                }
                if (preview_error != nullptr) {
                    *preview_error = "unsupported_branch_preview_opcode";
                }
                return false;
        }
    }

    return current == join_line;
}

bool TryBuildLuaPreviewStatementJoin(const LuaPreviewRenderContext& context,
                                     const std::vector<StraightLineValue>& base_values,
                                     const std::vector<bool>& base_declared,
                                     int then_start_line,
                                     int then_end_line,
                                     int else_start_line,
                                     int else_end_line,
                                     int statement_line,
                                     LuaPreviewStatementJoin* join,
                                     std::string* preview_error) {
    if (join == nullptr || statement_line < 1 ||
        static_cast<std::size_t>(statement_line) > context.instructions.size()) {
        return false;
    }

    join->then_values = base_values;
    join->else_values = base_values;
    join->then_declared = base_declared;
    join->else_declared = base_declared;
    join->then_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context,
        then_start_line,
        then_end_line,
        statement_line);
    join->else_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context,
        else_start_line,
        else_end_line,
        statement_line);
    join->then_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         then_start_line,
                                         statement_line);
    join->else_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         else_start_line,
                                         statement_line);
    join->statement_line = statement_line;

    if (then_start_line <= then_end_line &&
        !EvaluateLuaInlineRange(
            context,
            then_start_line,
            then_end_line,
            &join->then_values,
            &join->then_declared,
            preview_error)) {
        return false;
    }
    FinalizeLuaJoinCandidateState(&join->then_values,
                                  &join->then_declared,
                                  base_values,
                                  base_declared,
                                  join->then_written_registers,
                                  context.declarations,
                                  context.function,
                                  statement_line,
                                  context.register_count);
    if (else_start_line <= else_end_line &&
        !EvaluateLuaInlineRange(
            context,
            else_start_line,
            else_end_line,
            &join->else_values,
            &join->else_declared,
            preview_error)) {
        return false;
    }
    FinalizeLuaJoinCandidateState(&join->else_values,
                                  &join->else_declared,
                                  base_values,
                                  base_declared,
                                  join->else_written_registers,
                                  context.declarations,
                                  context.function,
                                  statement_line,
                                  context.register_count);
    return true;
}

bool TryBuildLuaPreviewStatementJoinToLine(const LuaPreviewRenderContext& context,
                                           const std::vector<StraightLineValue>& base_values,
                                           const std::vector<bool>& base_declared,
                                           int then_start_line,
                                           int else_start_line,
                                           int join_line,
                                           LuaPreviewStatementJoin* join,
                                           std::string* preview_error) {
    if (join == nullptr || join_line < 1 ||
        static_cast<std::size_t>(join_line) > context.instructions.size()) {
        return false;
    }

    join->then_values = base_values;
    join->else_values = base_values;
    join->then_declared = base_declared;
    join->else_declared = base_declared;
    join->then_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context,
        then_start_line,
        join_line - 1,
        join_line);
    join->else_written_registers = CollectLuaPreviewJoinWrittenRegisters(
        context,
        else_start_line,
        join_line - 1,
        join_line);
    join->then_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         then_start_line,
                                         join_line);
    join->else_requires_full_tail_render =
        RequiresLuaPreviewFullTailRender(context.instructions,
                                         else_start_line,
                                         join_line);
    join->statement_line = join_line;

    if (!EvaluateLuaBranchPathToJoin(
            context,
            then_start_line,
            join_line,
            &join->then_values,
            &join->then_declared,
            preview_error) ||
        !EvaluateLuaBranchPathToJoin(
            context,
            else_start_line,
            join_line,
            &join->else_values,
            &join->else_declared,
            preview_error)) {
        return false;
    }
    FinalizeLuaJoinCandidateState(&join->then_values,
                                  &join->then_declared,
                                  base_values,
                                  base_declared,
                                  join->then_written_registers,
                                  context.declarations,
                                  context.function,
                                  join_line,
                                  context.register_count);
    FinalizeLuaJoinCandidateState(&join->else_values,
                                  &join->else_declared,
                                  base_values,
                                  base_declared,
                                  join->else_written_registers,
                                  context.declarations,
                                  context.function,
                                  join_line,
                                  context.register_count);
    return true;
}

bool TryRenderLuaPreviewStatementJoin(const LuaPreviewRenderContext& context,
                                      const LuaPreviewStatementJoin& join,
                                      std::string* then_statement,
                                      std::string* else_statement) {
    return RenderLuaSingleStatementFromPreview(
               context,
               join.then_values,
               &join.then_declared,
               join.statement_line,
               then_statement) &&
           RenderLuaSingleStatementFromPreview(
               context,
               join.else_values,
               &join.else_declared,
               join.statement_line,
               else_statement);
}

bool TryBuildLuaPreviewStatementJoinStates(const LuaPreviewRenderContext& context,
                                           const LuaPreviewStatementJoin& join,
                                           LuaBranchStateSnapshot* then_state,
                                           LuaBranchStateSnapshot* else_state) {
    if (then_state == nullptr || else_state == nullptr ||
        join.statement_line < 1 ||
        static_cast<std::size_t>(join.statement_line) > context.instructions.size()) {
        return false;
    }

    const auto apply_join_state = [&](const std::vector<StraightLineValue>& source_values,
                                      const std::vector<bool>& source_declared,
                                      const std::vector<bool>& written_registers,
                                      LuaBranchStateSnapshot* state) {
        state->values = source_values;
        state->declared = source_declared;

        const Lua51Instruction& statement_instruction =
            context.instructions[static_cast<std::size_t>(join.statement_line - 1)];
        const int render_line = static_cast<int>(
            statement_instruction.line == 0 ? join.statement_line : statement_instruction.line);
        const LuaPreviewOperationContext preview_context{
            .instructions = context.instructions,
            .declarations = context.declarations,
            .function = context.function,
            .register_count = context.register_count,
            .values = &state->values,
            .declared = &state->declared,
            .call_arguments_for = context.call_arguments_for,
            .render_child = context.render_child,
        };
        LuaPreviewOperationDisposition disposition = LuaPreviewOperationDisposition::kBuildFailure;
        std::string build_error;
        int next_current = join.statement_line + 1;
        if (!TryApplyLuaPreviewOperation(preview_context,
                                         join.statement_line,
                                         render_line,
                                         LuaPreviewOperationMode::kStateTracking,
                                         &disposition,
                                         &build_error,
                                         &next_current)) {
            return false;
        }
        FinalizeLuaJoinCandidateState(&state->values,
                                      &state->declared,
                                      source_values,
                                      source_declared,
                                      written_registers,
                                      context.declarations,
                                      context.function,
                                      render_line,
                                      context.register_count);
        return true;
    };

    return apply_join_state(
               join.then_values, join.then_declared, join.then_written_registers, then_state) &&
           apply_join_state(
               join.else_values, join.else_declared, join.else_written_registers, else_state);
}

bool IsLuaPreviewRangeSideEffectFree(const LuaPreviewRenderContext& context,
                                     int start_line,
                                     int end_line) {
    if (start_line > end_line) {
        return true;
    }
    if (start_line < 1 || static_cast<std::size_t>(end_line) > context.instructions.size()) {
        return false;
    }
    std::vector<StraightLineValue> preview_values(static_cast<std::size_t>(context.register_count));
    std::vector<bool> preview_declared(static_cast<std::size_t>(context.register_count), false);
    for (int line = start_line; line <= end_line; ++line) {
        const Lua51Instruction& preview_instruction =
            context.instructions[static_cast<std::size_t>(line - 1)];
        if (preview_instruction.opcode == Lua51Opcode::kClose) {
            continue;
        }
        if (preview_instruction.opcode == Lua51Opcode::kLoadBool &&
            preview_instruction.c != 0 &&
            !IsLoadBoolPair(context.instructions, line) &&
            !ShouldUseLuaPreviewCanonicalLoadBoolSkip(context)) {
            return false;
        }
        const LuaPreviewOperationContext preview_context{
            .instructions = context.instructions,
            .declarations = context.declarations,
            .function = context.function,
            .register_count = context.register_count,
            .values = &preview_values,
            .declared = &preview_declared,
            .call_arguments_for = context.call_arguments_for,
            .render_child = context.render_child,
        };
        LuaPreviewOperationDisposition disposition = LuaPreviewOperationDisposition::kBuildFailure;
        std::string build_error;
        int next_current = line + 1;
        if (!TryApplyLuaPreviewOperation(preview_context,
                line,
                line,
                LuaPreviewOperationMode::kSideEffectFreeOnly,
                &disposition,
                &build_error,
                &next_current)) {
            return false;
        }
        line = next_current - 1;
    }
    return true;
}

bool RenderLuaSingleStatementFromPreview(const LuaPreviewRenderContext& context,
                                         const std::vector<StraightLineValue>& state_values,
                                         const std::vector<bool>* state_declared,
                                         int statement_line,
                                         std::string* statement) {
    if (statement == nullptr || statement_line < 1 ||
        static_cast<std::size_t>(statement_line) > context.instructions.size()) {
        return false;
    }

    const Lua51Instruction& statement_instruction =
        context.instructions[static_cast<std::size_t>(statement_line - 1)];
    const int render_line = static_cast<int>(
        statement_instruction.line == 0 ? statement_line : statement_instruction.line);
    std::vector<StraightLineValue> preview_values = state_values;
    const LuaOperationBuildContext build_context{
        .instructions = context.instructions,
        .declarations = context.declarations,
        .function = context.function,
        .register_count = context.register_count,
        .values = &preview_values,
        .declared = state_declared,
        .call_arguments_for = context.call_arguments_for,
        .return_values_for = nullptr,
        .setlist_values_for = nullptr,
        .render_child = context.render_child,
        .resolve_setlist_block = nullptr,
        .trace_render = false,
        .trace = {},
        .allow_opaque_preview_closures = true,
    };
    LuaBuiltOperation built_operation;
    std::string build_error;
    if (!TryBuildLuaBuiltOperation(build_context,
                                   statement_line,
                                   statement_line,
                                   statement_line,
                                   render_line,
                                   &build_error,
                                   &built_operation)) {
        return false;
    }

    const LuaOperationRenderContext render_context{
        .lines = nullptr,
        .values = nullptr,
        .declared = nullptr,
        .declarations = context.declarations,
        .function = context.function,
    };
    return TryRenderLuaBuiltOperationStatement(render_context, built_operation, statement);
}

}  // namespace refii::lua
