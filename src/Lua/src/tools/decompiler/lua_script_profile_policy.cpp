#include "tools/decompiler/lua_script_profile_policy.h"

#include <algorithm>
#include <cctype>
#include <string>
#include <string_view>
#include <unordered_map>

namespace refii::lua {
namespace {

constexpr std::size_t kLuaPreviewInteractiveCutsceneActionsScopedBudget = 512;
constexpr std::size_t kLuaPreviewGenericInstructionBudget = 384;
constexpr std::size_t kLuaPreviewCompatibilityReuseGenericBudget = 2048;

bool Contains(std::string_view text, std::string_view needle) {
    return text.find(needle) != std::string_view::npos;
}

bool IsQuestHotspotPath(std::string_view normalized) {
    return Contains(normalized, "scripts/quests/qr_creaturehunter.lua") ||
           Contains(normalized, "scripts/quests/qr_assassinationglobal.lua") ||
           Contains(normalized, "scripts/quests/gameflow.lua") ||
           Contains(normalized, "scripts/quests/qo890_colosseum.lua") ||
           Contains(normalized, "scripts/quests/qr_slavetrader.lua") ||
           Contains(normalized, "scripts/quests/luaenums.lua");
}

bool IsFallbackQuestHotspotPath(std::string_view normalized) {
    return Contains(normalized, "qc010_childhood.lua") ||
           Contains(normalized, "qc130_thecampinitiation.lua") ||
           Contains(normalized, "qo235_toesacrifices.lua") ||
           Contains(normalized, "qo860_wraithmarshrevisited.lua") ||
           IsQuestHotspotPath(normalized);
}

bool IsScopedLargeHoldoutPath(std::string_view normalized) {
    return Contains(normalized, "behaviourinteractivecutscene.lua") ||
           Contains(normalized, "combatinfo.lua");
}

bool IsScopedSmallHoldoutPath(std::string_view normalized) {
    return Contains(normalized, "behaviourspouseleavehero.lua") ||
           Contains(normalized, "behaviourtag2.lua") ||
           Contains(normalized, "behaviourstallvendor.lua") ||
           Contains(normalized, "behaviourinteractivecutsceneactions.lua");
}

bool IsInteractiveCutsceneActionsPath(std::string_view normalized) {
    return Contains(normalized, "behaviourinteractivecutsceneactions.lua");
}

bool IsGroupMindPath(std::string_view normalized) {
    return Contains(normalized, "behaviourgrouppreparetoruns.lua") ||
           Contains(normalized, "bargroupmind.lua");
}

bool IsCallCarryParityTargetPath(std::string_view normalized) {
    return Contains(normalized, "miscellaneous/sexfunctions.lua") ||
           Contains(normalized, "miscellaneous/actionusescripts/onactionuseknockdoor.lua") ||
           Contains(normalized, "scripts/ai/bargroupmind.lua");
}

bool IsAiCoreHotspotPath(std::string_view normalized) {
    const bool is_top12_drift_hotspot =
        Contains(normalized, "behaviourspellresponse.lua") ||
        Contains(normalized, "behaviourrunaway.lua") ||
        Contains(normalized, "bargroupmind.lua") ||
        Contains(normalized, "behaviourmonk.lua") ||
        Contains(normalized, "behaviourcampseated.lua") ||
        Contains(normalized, "behaviourbarmanjob.lua") ||
        Contains(normalized, "aimanager.lua") ||
        Contains(normalized, "behaviourtowncrier.lua") ||
        Contains(normalized, "behavioursculpt.lua") ||
        Contains(normalized, "behaviourcratecarrying.lua") ||
        Contains(normalized, "behaviourchicken.lua") ||
        Contains(normalized, "behaviourbeetleidle.lua");
    return Contains(normalized, "combatstates.lua") ||
           Contains(normalized, "interactivecutscenegroupmind.lua") ||
           Contains(normalized, "aibase.lua") ||
           Contains(normalized, "behaviourinteractivecutscene.lua") ||
           Contains(normalized, "behaviourinteractivecutsceneactions.lua") ||
           Contains(normalized, "combatinfo.lua") ||
           IsCallCarryParityTargetPath(normalized) ||
           is_top12_drift_hotspot ||
           IsQuestHotspotPath(normalized);
}

bool IsPriorityOwnershipPath(std::string_view normalized) {
    if (!Contains(normalized, "gamescripts/scripts/ai/")) {
        return false;
    }
    if (Contains(normalized, "gamescripts/scripts/ai/behaviours/")) {
        return true;
    }
    return Contains(normalized, "gamescripts/scripts/ai/aibase.lua") ||
           Contains(normalized, "gamescripts/scripts/ai/combat") ||
           Contains(normalized, "gamescripts/scripts/ai/behaviour") ||
           Contains(normalized, "gamescripts/scripts/ai/conversation");
}

bool IsBehaviorPath(std::string_view normalized) {
    return Contains(normalized, "gamescripts/scripts/ai/behaviours/");
}

bool IsConstructorCombatstylesPath(std::string_view normalized) {
    return Contains(normalized, "scripts/ai/combatstyles/");
}

bool IsConstructorDeepTargetPath(std::string_view normalized) {
    return Contains(normalized, "scripts/ai/combatsequences.lua") ||
           Contains(normalized, "scripts/miscellaneous/crescendomechanics.lua") ||
           Contains(normalized, "scripts/ai/combatstyles/") ||
           Contains(normalized, "scripts/miscellaneous/sexfunctions.lua") ||
           Contains(normalized, "scripts/miscellaneous/actionusescripts/onactionuseknockdoor.lua");
}

bool IsConstructorIdentityHotspotPath(std::string_view normalized) {
    return Contains(normalized, "scripts/ai/aibase.lua") ||
           Contains(normalized, "scripts/ai/behaviours/behaviourfollow.lua") ||
           Contains(normalized, "scripts/ai/behaviours/behaviourguard.lua") ||
           Contains(normalized, "scripts/ai/behaviours/behaviourflee.lua");
}

bool IsRuntimeLightModeHotspotPath(std::string_view normalized) {
    return Contains(normalized, "gamescripts/scripts/ai/combatsequences.lua") ||
           Contains(normalized, "gamescripts/scripts/ai/combatstates.lua") ||
           Contains(normalized, "gamescripts/scripts/ai/interactivecutscenegroupmind.lua") ||
           Contains(normalized, "gamescripts/scripts/ai/aimanager.lua") ||
           Contains(normalized, "gamescripts/scripts/ai/aibase.lua") ||
           Contains(normalized, "gamescripts/scripts/miscellaneous/miscfunctions.lua") ||
           Contains(normalized, "gamescripts/scripts/camera/stringandcagecamera.lua");
}

bool IsRuntimeBoundedAttemptAiCombatPath(std::string_view normalized) {
    return Contains(normalized, "scripts/ai/combatsequences.lua") ||
           Contains(normalized, "scripts/ai/combatinfo.lua") ||
           Contains(normalized, "scripts/ai/combatstates.lua") ||
           Contains(normalized, "scripts/ai/combatregister.lua");
}

bool IsDirectNonexactNativeExactRescueOverridePath(std::string_view normalized) {
    return normalized == "gamescripts/scripts/ai/behaviours/behaviourinteractivecutscene.lua" ||
           normalized == "gamescripts/scripts/ai/combatinfo.lua";
}

bool IsQuestQc200BloodstoneAssaultPath(std::string_view normalized) {
    return Contains(normalized, "qc200_bloodstoneassault.lua");
}

bool IsBehaviorCultistPath(std::string_view normalized) {
    return Contains(normalized, "behaviourcultist.lua");
}

bool IsBehaviorTag2Path(std::string_view normalized) {
    return Contains(normalized, "behaviourtag2.lua");
}

bool IsCameraScriptPath(std::string_view normalized) {
    return Contains(normalized, "gamescripts/scripts/camera/");
}

bool IsEntityModeScriptPath(std::string_view normalized) {
    return Contains(normalized, "gamescripts/scripts/miscellaneous/entitymodescripts/");
}

bool IsQuestLuaEnumsPath(std::string_view normalized) {
    return Contains(normalized, "gamescripts/scripts/quests/luaenums.lua");
}

bool IsRawInvalidRecoveryPriorityPath(std::string_view normalized) {
    return Contains(normalized, "behaviourprisoner.lua") ||
           Contains(normalized, "behaviourcultist.lua") ||
           Contains(normalized, "camerabase.lua") ||
           Contains(normalized, "camerafunctions.lua") ||
           Contains(normalized, "stringandcagecamera.lua") ||
           Contains(normalized, "herolocomotionstates.lua") ||
           Contains(normalized, "crescendoequations.lua") ||
           Contains(normalized, "luaenums.lua") ||
           Contains(normalized, "menulib.lua");
}

bool IsDisassemblyRecoveryPriorityPath(std::string_view normalized) {
    return IsRawInvalidRecoveryPriorityPath(normalized) ||
           Contains(normalized, "qo225_toldonations.lua") ||
           Contains(normalized, "qo880_vision.lua") ||
           Contains(normalized, "behaviourhare.lua") ||
           Contains(normalized, "behaviourdog.lua") ||
           Contains(normalized, "behaviourwander.lua") ||
           Contains(normalized, "behaviourtrollonknees.lua") ||
           Contains(normalized, "combatdebug.lua") ||
           Contains(normalized, "simplecamera.lua") ||
           Contains(normalized, "stringandcagecamera.lua");
}

}  // namespace

std::string NormalizeLuaSourcePathForPolicy(std::string_view source_name) {
    std::string normalized;
    normalized.reserve(source_name.size());
    for (const char ch : source_name) {
        if (ch == '\\') {
            normalized.push_back('/');
        } else {
            normalized.push_back(
                static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
        }
    }
    return normalized;
}

bool IsLuaScriptProfileNormalizedPath(std::string_view normalized_source_name,
                                      LuaScriptProfileFlag flag) {
    switch (flag) {
        case LuaScriptProfileFlag::kBehavior:
            return IsBehaviorPath(normalized_source_name);
        case LuaScriptProfileFlag::kQuest:
            return Contains(normalized_source_name, "gamescripts/scripts/quests/");
        case LuaScriptProfileFlag::kQuestHotspot:
            return IsQuestHotspotPath(normalized_source_name);
        case LuaScriptProfileFlag::kFallbackQuestHotspot:
            return IsFallbackQuestHotspotPath(normalized_source_name);
        case LuaScriptProfileFlag::kScopedSmallHoldout:
            return IsScopedSmallHoldoutPath(normalized_source_name);
        case LuaScriptProfileFlag::kScopedLargeHoldout:
            return IsScopedLargeHoldoutPath(normalized_source_name);
        case LuaScriptProfileFlag::kPriorityOwnership:
            return IsPriorityOwnershipPath(normalized_source_name);
        case LuaScriptProfileFlag::kAiCoreHotspot:
            return IsAiCoreHotspotPath(normalized_source_name);
        case LuaScriptProfileFlag::kInteractiveCutsceneActions:
            return IsInteractiveCutsceneActionsPath(normalized_source_name);
        case LuaScriptProfileFlag::kGroupMind:
            return IsGroupMindPath(normalized_source_name);
        case LuaScriptProfileFlag::kCallCarryParityTarget:
            return IsCallCarryParityTargetPath(normalized_source_name);
        case LuaScriptProfileFlag::kConstructorCombatStyles:
            return IsConstructorCombatstylesPath(normalized_source_name);
        case LuaScriptProfileFlag::kConstructorDeepTarget:
            return IsConstructorDeepTargetPath(normalized_source_name);
        case LuaScriptProfileFlag::kConstructorIdentityHotspot:
            return IsConstructorIdentityHotspotPath(normalized_source_name);
        case LuaScriptProfileFlag::kConstructorAliasHotspot:
            return IsGroupMindPath(normalized_source_name);
        case LuaScriptProfileFlag::kFallbackQc010:
            return Contains(normalized_source_name, "qc010_childhood.lua");
        case LuaScriptProfileFlag::kDisassemblyRecoveryPriority:
            return IsDisassemblyRecoveryPriorityPath(normalized_source_name);
        case LuaScriptProfileFlag::kRawInvalidRecoveryPriority:
            return IsRawInvalidRecoveryPriorityPath(normalized_source_name);
        case LuaScriptProfileFlag::kRuntimeLightModeHotspot:
            return IsRuntimeLightModeHotspotPath(normalized_source_name);
        case LuaScriptProfileFlag::kRuntimeBoundedAttemptAiCombat:
            return IsRuntimeBoundedAttemptAiCombatPath(normalized_source_name);
        case LuaScriptProfileFlag::kDirectNonexactNativeExactRescueOverride:
            return IsDirectNonexactNativeExactRescueOverridePath(normalized_source_name);
        case LuaScriptProfileFlag::kQuestQc200BloodstoneAssault:
            return IsQuestQc200BloodstoneAssaultPath(normalized_source_name);
        case LuaScriptProfileFlag::kBehaviorCultist:
            return IsBehaviorCultistPath(normalized_source_name);
        case LuaScriptProfileFlag::kBehaviorTag2:
            return IsBehaviorTag2Path(normalized_source_name);
        case LuaScriptProfileFlag::kCameraScriptPath:
            return IsCameraScriptPath(normalized_source_name);
        case LuaScriptProfileFlag::kEntityModeScriptPath:
            return IsEntityModeScriptPath(normalized_source_name);
        case LuaScriptProfileFlag::kQuestLuaEnumsPath:
            return IsQuestLuaEnumsPath(normalized_source_name);
        case LuaScriptProfileFlag::kGameface:
            return Contains(normalized_source_name, "guiscripts/scripts/gameface/");
        case LuaScriptProfileFlag::kInteractiveCutsceneActionsF2:
        case LuaScriptProfileFlag::kNone:
            return false;
    }
    return false;
}

bool IsLuaScriptProfilePath(std::string_view source_name, LuaScriptProfileFlag flag) {
    const std::string normalized = NormalizeLuaSourcePathForPolicy(source_name);
    return IsLuaScriptProfileNormalizedPath(normalized, flag);
}

LuaScriptProfile ClassifyLuaScriptProfile(std::string_view source_name,
                                          int line_defined,
                                          int instruction_count) {
    const std::string normalized = NormalizeLuaSourcePathForPolicy(source_name);
    LuaScriptProfile profile;
    profile.line_defined = line_defined;
    profile.instruction_count = instruction_count;

    const auto set = [&](LuaScriptProfileFlag flag) {
        profile.flags |= LuaScriptProfileFlagMask(flag);
    };

    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kBehavior)) {
        set(LuaScriptProfileFlag::kBehavior);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kQuest)) {
        set(LuaScriptProfileFlag::kQuest);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kQuestHotspot)) {
        set(LuaScriptProfileFlag::kQuestHotspot);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kFallbackQuestHotspot)) {
        set(LuaScriptProfileFlag::kFallbackQuestHotspot);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kScopedSmallHoldout)) {
        set(LuaScriptProfileFlag::kScopedSmallHoldout);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kScopedLargeHoldout)) {
        set(LuaScriptProfileFlag::kScopedLargeHoldout);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kPriorityOwnership)) {
        set(LuaScriptProfileFlag::kPriorityOwnership);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kAiCoreHotspot)) {
        set(LuaScriptProfileFlag::kAiCoreHotspot);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kInteractiveCutsceneActions)) {
        set(LuaScriptProfileFlag::kInteractiveCutsceneActions);
    }
    if (profile.Has(LuaScriptProfileFlag::kInteractiveCutsceneActions) &&
        line_defined >= 17 && line_defined <= 23) {
        set(LuaScriptProfileFlag::kInteractiveCutsceneActionsF2);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kGroupMind)) {
        set(LuaScriptProfileFlag::kGroupMind);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kCallCarryParityTarget)) {
        set(LuaScriptProfileFlag::kCallCarryParityTarget);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kConstructorCombatStyles)) {
        set(LuaScriptProfileFlag::kConstructorCombatStyles);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kConstructorDeepTarget)) {
        set(LuaScriptProfileFlag::kConstructorDeepTarget);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kConstructorIdentityHotspot)) {
        set(LuaScriptProfileFlag::kConstructorIdentityHotspot);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kConstructorAliasHotspot)) {
        set(LuaScriptProfileFlag::kConstructorAliasHotspot);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kFallbackQc010)) {
        set(LuaScriptProfileFlag::kFallbackQc010);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kDisassemblyRecoveryPriority)) {
        set(LuaScriptProfileFlag::kDisassemblyRecoveryPriority);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kRawInvalidRecoveryPriority)) {
        set(LuaScriptProfileFlag::kRawInvalidRecoveryPriority);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kRuntimeLightModeHotspot)) {
        set(LuaScriptProfileFlag::kRuntimeLightModeHotspot);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kRuntimeBoundedAttemptAiCombat)) {
        set(LuaScriptProfileFlag::kRuntimeBoundedAttemptAiCombat);
    }
    if (IsLuaScriptProfileNormalizedPath(
            normalized, LuaScriptProfileFlag::kDirectNonexactNativeExactRescueOverride)) {
        set(LuaScriptProfileFlag::kDirectNonexactNativeExactRescueOverride);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized,
                                         LuaScriptProfileFlag::kQuestQc200BloodstoneAssault)) {
        set(LuaScriptProfileFlag::kQuestQc200BloodstoneAssault);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kBehaviorCultist)) {
        set(LuaScriptProfileFlag::kBehaviorCultist);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kBehaviorTag2)) {
        set(LuaScriptProfileFlag::kBehaviorTag2);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kCameraScriptPath)) {
        set(LuaScriptProfileFlag::kCameraScriptPath);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kEntityModeScriptPath)) {
        set(LuaScriptProfileFlag::kEntityModeScriptPath);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kQuestLuaEnumsPath)) {
        set(LuaScriptProfileFlag::kQuestLuaEnumsPath);
    }
    if (IsLuaScriptProfileNormalizedPath(normalized, LuaScriptProfileFlag::kGameface)) {
        set(LuaScriptProfileFlag::kGameface);
    }

    return profile;
}

LuaScriptProfile ClassifyLuaScriptProfile(const Lua51Function& function,
                                          int instruction_count) {
    if (instruction_count == 0) {
        instruction_count = static_cast<int>(function.code.size());
    }
    return ClassifyLuaScriptProfile(function.source_name,
                                    function.line_defined,
                                    instruction_count);
}

const LuaScriptProfile& GetLuaScriptProfileCached(const Lua51Function& function) {
    static thread_local const Lua51Function* last_function = nullptr;
    static thread_local LuaScriptProfile last_profile = {};
    if (&function == last_function) {
        return last_profile;
    }

    static thread_local std::unordered_map<const Lua51Function*, LuaScriptProfile> cached_profiles;
    const auto cached = cached_profiles.find(&function);
    if (cached != cached_profiles.end()) {
        last_function = &function;
        last_profile = cached->second;
        return last_profile;
    }

    const LuaScriptProfile profile = ClassifyLuaScriptProfile(function);
    cached_profiles.emplace(&function, profile);
    last_function = &function;
    last_profile = profile;
    return last_profile;
}

LuaScriptPolicy BuildLuaScriptPolicy(std::string_view source_name,
                                     int line_defined,
                                     int instruction_count) {
    LuaScriptPolicy policy;
    policy.profile = ClassifyLuaScriptProfile(source_name, line_defined, instruction_count);

    if (policy.profile.Has(LuaScriptProfileFlag::kAiCoreHotspot) ||
        policy.profile.Has(LuaScriptProfileFlag::kBehavior)) {
        policy.conditional_chain_max_links = 6;
        policy.conditional_chain_max_span = 48;
        policy.conditional_join_scan_span = 48;
    }

    if (policy.profile.Has(LuaScriptProfileFlag::kScopedSmallHoldout)) {
        policy.preview_fastpath_instruction_budget = 2048;
        policy.preview_chain_scan_limit = 96;
    } else if (policy.profile.Has(LuaScriptProfileFlag::kScopedLargeHoldout)) {
        policy.preview_fastpath_instruction_budget = 4600;
        policy.preview_chain_scan_limit = 160;
    } else if (policy.profile.Has(LuaScriptProfileFlag::kAiCoreHotspot)) {
        policy.preview_fastpath_instruction_budget = 2300;
        policy.preview_chain_scan_limit = 48;
    } else if (policy.profile.Has(LuaScriptProfileFlag::kBehavior) ||
               policy.profile.Has(LuaScriptProfileFlag::kCallCarryParityTarget)) {
        policy.preview_fastpath_instruction_budget = 1536;
        policy.preview_chain_scan_limit = 48;
    } else {
        policy.preview_fastpath_instruction_budget = 384;
        policy.preview_chain_scan_limit = 40;
    }
    policy.preview_root_shape_span = policy.preview_chain_scan_limit;

    if (policy.profile.Has(LuaScriptProfileFlag::kFallbackQc010)) {
        policy.fallback_branch_span_budget = 48;
        policy.fallback_node_budget = 64;
        policy.fallback_deadline_ms = 20;
    } else if (policy.profile.Has(LuaScriptProfileFlag::kFallbackQuestHotspot)) {
        policy.fallback_branch_span_budget = 64;
        policy.fallback_node_budget = 192;
        policy.fallback_deadline_ms = 40;
    } else {
        policy.fallback_branch_span_budget = 96;
        policy.fallback_node_budget = 512;
        policy.fallback_deadline_ms = 120;
    }

    const bool is_top_level = line_defined == 0;
    policy.constructor_scope_enabled =
        is_top_level && policy.profile.Has(LuaScriptProfileFlag::kConstructorDeepTarget);
    policy.constructor_combatstyles_scope =
        is_top_level && policy.profile.Has(LuaScriptProfileFlag::kConstructorCombatStyles);
    policy.constructor_high_budget_scope =
        is_top_level && policy.profile.Has(LuaScriptProfileFlag::kConstructorDeepTarget);
    if (!policy.constructor_scope_enabled) {
        policy.constructor_attempt_budget = 12;
    } else if (policy.constructor_combatstyles_scope) {
        policy.constructor_attempt_budget = 192;
    } else if (policy.constructor_high_budget_scope) {
        policy.constructor_attempt_budget = 64;
    } else {
        policy.constructor_attempt_budget = 12;
    }

    policy.runtime_hotspot =
        policy.profile.Has(LuaScriptProfileFlag::kRuntimeLightModeHotspot);
    policy.runtime_bounded_attempt_ai_combat_hotspot =
        policy.profile.Has(LuaScriptProfileFlag::kRuntimeBoundedAttemptAiCombat);
    policy.runtime_disassembly_recovery_target =
        policy.profile.Has(LuaScriptProfileFlag::kDisassemblyRecoveryPriority);
    policy.runtime_fallback_quest_hotspot =
        policy.profile.Has(LuaScriptProfileFlag::kFallbackQuestHotspot);
    policy.runtime_behavior_path =
        policy.profile.Has(LuaScriptProfileFlag::kBehavior);
    policy.direct_nonexact_native_exact_rescue_override = policy.profile.Has(
        LuaScriptProfileFlag::kDirectNonexactNativeExactRescueOverride);

    return policy;
}

LuaScriptPolicy BuildLuaScriptPolicy(const Lua51Function& function,
                                     int instruction_count) {
    if (instruction_count == 0) {
        instruction_count = static_cast<int>(function.code.size());
    }
    return BuildLuaScriptPolicy(function.source_name,
                                function.line_defined,
                                instruction_count);
}

bool IsLuaPolicyScopedSmallHoldoutFastPathEnabled(const LuaScriptPolicy& policy,
                                                  std::size_t instruction_count) {
    if (!policy.profile.Has(LuaScriptProfileFlag::kScopedSmallHoldout)) {
        return false;
    }
    if (policy.profile.Has(LuaScriptProfileFlag::kInteractiveCutsceneActions)) {
        return instruction_count <= kLuaPreviewInteractiveCutsceneActionsScopedBudget;
    }
    return instruction_count <= policy.preview_fastpath_instruction_budget;
}

bool IsLuaPolicyScopedLargeHoldoutFastPathEnabled(const LuaScriptPolicy& policy,
                                                  std::size_t instruction_count) {
    return policy.profile.Has(LuaScriptProfileFlag::kScopedLargeHoldout) &&
           instruction_count <= policy.preview_fastpath_instruction_budget;
}

bool IsLuaPolicyPriorityOwnershipFastPathEnabled(const LuaScriptPolicy& policy,
                                                 std::size_t instruction_count) {
    return policy.profile.Has(LuaScriptProfileFlag::kAiCoreHotspot) &&
           instruction_count <= policy.preview_fastpath_instruction_budget;
}

bool IsLuaPolicyGenericBehaviorFastPathEnabled(const LuaScriptPolicy& policy,
                                               std::size_t instruction_count) {
    return (policy.profile.Has(LuaScriptProfileFlag::kBehavior) ||
            policy.profile.Has(LuaScriptProfileFlag::kCallCarryParityTarget)) &&
           instruction_count <= policy.preview_fastpath_instruction_budget;
}

bool IsLuaPolicyPreviewFastPathEnabled(const LuaScriptPolicy& policy,
                                       std::size_t instruction_count) {
    if (IsLuaPolicyScopedSmallHoldoutFastPathEnabled(policy, instruction_count) ||
        IsLuaPolicyScopedLargeHoldoutFastPathEnabled(policy, instruction_count) ||
        IsLuaPolicyPriorityOwnershipFastPathEnabled(policy, instruction_count) ||
        IsLuaPolicyGenericBehaviorFastPathEnabled(policy, instruction_count)) {
        return true;
    }
    return instruction_count <=
           std::max<std::size_t>(kLuaPreviewGenericInstructionBudget,
                                 policy.preview_fastpath_instruction_budget);
}

bool IsLuaPolicyPreviewCompatibilityReuseEnabled(const LuaScriptPolicy& policy,
                                                 std::size_t instruction_count) {
    return instruction_count <= kLuaPreviewCompatibilityReuseGenericBudget ||
           IsLuaPolicyScopedSmallHoldoutFastPathEnabled(policy, instruction_count) ||
           IsLuaPolicyScopedLargeHoldoutFastPathEnabled(policy, instruction_count) ||
           IsLuaPolicyPriorityOwnershipFastPathEnabled(policy, instruction_count) ||
           IsLuaPolicyGenericBehaviorFastPathEnabled(policy, instruction_count);
}

}  // namespace refii::lua
