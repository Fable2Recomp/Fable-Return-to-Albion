#pragma once

#include "tools/native_lua_chunk.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace refii::lua {

enum class LuaScriptProfileFlag : std::uint64_t {
    kNone = 0,
    kBehavior = 1ull << 0,
    kQuest = 1ull << 1,
    kQuestHotspot = 1ull << 2,
    kFallbackQuestHotspot = 1ull << 3,
    kScopedSmallHoldout = 1ull << 4,
    kScopedLargeHoldout = 1ull << 5,
    kPriorityOwnership = 1ull << 6,
    kAiCoreHotspot = 1ull << 7,
    kInteractiveCutsceneActions = 1ull << 8,
    kInteractiveCutsceneActionsF2 = 1ull << 9,
    kGroupMind = 1ull << 10,
    kCallCarryParityTarget = 1ull << 11,
    kConstructorCombatStyles = 1ull << 12,
    kConstructorDeepTarget = 1ull << 13,
    kConstructorIdentityHotspot = 1ull << 14,
    kConstructorAliasHotspot = 1ull << 15,
    kFallbackQc010 = 1ull << 16,
    kDisassemblyRecoveryPriority = 1ull << 17,
    kRawInvalidRecoveryPriority = 1ull << 18,
    kRuntimeLightModeHotspot = 1ull << 19,
    kGameface = 1ull << 20,
    kRuntimeBoundedAttemptAiCombat = 1ull << 21,
    kDirectNonexactNativeExactRescueOverride = 1ull << 22,
    kQuestQc200BloodstoneAssault = 1ull << 23,
    kBehaviorCultist = 1ull << 24,
    kBehaviorTag2 = 1ull << 25,
    kCameraScriptPath = 1ull << 26,
    kEntityModeScriptPath = 1ull << 27,
    kQuestLuaEnumsPath = 1ull << 28,
};

constexpr std::uint64_t LuaScriptProfileFlagMask(LuaScriptProfileFlag flag) {
    return static_cast<std::uint64_t>(flag);
}

struct LuaScriptProfile {
    std::uint64_t flags = 0;
    int line_defined = 0;
    int instruction_count = 0;

    bool Has(LuaScriptProfileFlag flag) const {
        return (flags & LuaScriptProfileFlagMask(flag)) != 0;
    }
};

struct LuaScriptPolicy {
    LuaScriptProfile profile;

    int conditional_chain_max_links = 4;
    int conditional_chain_max_span = 32;
    int conditional_join_scan_span = 32;
    int conditional_chain_instruction_budget = 1536;

    int preview_chain_scan_limit = 40;
    int preview_root_shape_span = 40;
    std::size_t preview_fastpath_instruction_budget = 384;

    int fallback_branch_span_budget = 96;
    int fallback_node_budget = 512;
    int fallback_deadline_ms = 120;

    bool constructor_scope_enabled = false;
    bool constructor_combatstyles_scope = false;
    bool constructor_high_budget_scope = false;
    int constructor_attempt_budget = 12;

    bool runtime_hotspot = false;
    bool runtime_bounded_attempt_ai_combat_hotspot = false;
    bool runtime_disassembly_recovery_target = false;
    bool runtime_fallback_quest_hotspot = false;
    bool runtime_behavior_path = false;
    bool direct_nonexact_native_exact_rescue_override = false;
};

std::string NormalizeLuaSourcePathForPolicy(std::string_view source_name);

bool IsLuaScriptProfileNormalizedPath(std::string_view normalized_source_name,
                                      LuaScriptProfileFlag flag);

bool IsLuaScriptProfilePath(std::string_view source_name, LuaScriptProfileFlag flag);

LuaScriptProfile ClassifyLuaScriptProfile(std::string_view source_name,
                                          int line_defined = 0,
                                          int instruction_count = 0);

LuaScriptProfile ClassifyLuaScriptProfile(const Lua51Function& function,
                                          int instruction_count = 0);

const LuaScriptProfile& GetLuaScriptProfileCached(const Lua51Function& function);

LuaScriptPolicy BuildLuaScriptPolicy(std::string_view source_name,
                                     int line_defined = 0,
                                     int instruction_count = 0);

LuaScriptPolicy BuildLuaScriptPolicy(const Lua51Function& function,
                                     int instruction_count = 0);

bool IsLuaPolicyScopedSmallHoldoutFastPathEnabled(const LuaScriptPolicy& policy,
                                                  std::size_t instruction_count);

bool IsLuaPolicyScopedLargeHoldoutFastPathEnabled(const LuaScriptPolicy& policy,
                                                  std::size_t instruction_count);

bool IsLuaPolicyPriorityOwnershipFastPathEnabled(const LuaScriptPolicy& policy,
                                                 std::size_t instruction_count);

bool IsLuaPolicyGenericBehaviorFastPathEnabled(const LuaScriptPolicy& policy,
                                               std::size_t instruction_count);

bool IsLuaPolicyPreviewFastPathEnabled(const LuaScriptPolicy& policy,
                                       std::size_t instruction_count);

bool IsLuaPolicyPreviewCompatibilityReuseEnabled(const LuaScriptPolicy& policy,
                                                 std::size_t instruction_count);

}  // namespace refii::lua
