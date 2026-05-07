#include "tools/decompiler/repair/lua_decompile_repair_context.h"

#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <chrono>
#include <string>
#include <string_view>

namespace refii::lua::internal {

namespace {

constexpr int kReferenceFunctionRepairMaxPasses = 16;
thread_local DecompileRepairContext g_decompile_repair_context;

}  // namespace

const DecompileRepairContext& GetCurrentDecompileRepairContext() {
    return g_decompile_repair_context;
}

bool ShouldUseExhaustiveRescueForCurrentFile() {
    return GetCurrentDecompileRepairContext().prefer_exhaustive_rescue;
}

int GetCurrentReferenceRepairPassBudget() {
    if (!ShouldUseExhaustiveRescueForCurrentFile()) {
        return 0;
    }
    return GetCurrentDecompileRepairContext().outlier_candidate ? 4
                                                                : kReferenceFunctionRepairMaxPasses;
}

int GetCurrentRepairAttemptCap(int default_cap, int nonfailed_outlier_cap) {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    if (!context.outlier_candidate || context.previous_failed_file ||
        context.unfinished_active || ShouldUseExhaustiveRescueForCurrentFile()) {
        return default_cap;
    }
    return std::min(default_cap, nonfailed_outlier_cap);
}

int GetCurrentRepairFamilyRepeatBudget() {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    if (!context.outlier_candidate) {
        return 4;
    }
    if (!context.previous_failed_file && !context.unfinished_active &&
        !ShouldUseExhaustiveRescueForCurrentFile()) {
        return 2;
    }
    return 6;
}

void SetCurrentDecompileRepairContext(bool prefer_exhaustive_rescue,
                                      bool outlier_candidate,
                                      bool previous_failed_file,
                                      bool unfinished_active,
                                      std::chrono::steady_clock::time_point deadline) {
    g_decompile_repair_context.prefer_exhaustive_rescue = prefer_exhaustive_rescue;
    g_decompile_repair_context.outlier_candidate = outlier_candidate;
    g_decompile_repair_context.previous_failed_file = previous_failed_file;
    g_decompile_repair_context.unfinished_active = unfinished_active;
    g_decompile_repair_context.deadline_active =
        deadline != std::chrono::steady_clock::time_point{};
    g_decompile_repair_context.deadline = deadline;
    g_decompile_repair_context.deadline_noted = false;
}

void ResetCurrentDecompileRepairContext() {
    g_decompile_repair_context = DecompileRepairContext{};
}

bool IsCurrentDecompileRepairDeadlineExceeded() {
    const DecompileRepairContext& context = GetCurrentDecompileRepairContext();
    return context.deadline_active &&
           std::chrono::steady_clock::now() >= context.deadline;
}

bool ShouldAbortCurrentDecompileRepair(std::string* note, std::string_view note_prefix) {
    if (!IsCurrentDecompileRepairDeadlineExceeded()) {
        return false;
    }

    if (note != nullptr && !g_decompile_repair_context.deadline_noted) {
        const std::string prefix =
            note_prefix.empty() ? "repair" : std::string(note_prefix);
        *note = JoinNotes(*note, prefix + "_deadline_exceeded");
        g_decompile_repair_context.deadline_noted = true;
    }
    return true;
}

}  // namespace refii::lua::internal
