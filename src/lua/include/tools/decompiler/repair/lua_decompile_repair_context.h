#pragma once

#include <chrono>
#include <string>
#include <string_view>

namespace refii::lua::internal {

struct DecompileRepairContext {
    bool prefer_exhaustive_rescue = true;
    bool outlier_candidate = false;
    bool previous_failed_file = false;
    bool unfinished_active = false;
    bool deadline_active = false;
    bool deadline_noted = false;
    std::chrono::steady_clock::time_point deadline{};
};

const DecompileRepairContext& GetCurrentDecompileRepairContext();
bool ShouldUseExhaustiveRescueForCurrentFile();
int GetCurrentReferenceRepairPassBudget();
int GetCurrentRepairAttemptCap(int default_cap, int nonfailed_outlier_cap);
int GetCurrentRepairFamilyRepeatBudget();
void SetCurrentDecompileRepairContext(bool prefer_exhaustive_rescue,
                                      bool outlier_candidate,
                                      bool previous_failed_file,
                                      bool unfinished_active,
                                      std::chrono::steady_clock::time_point deadline = {});
void ResetCurrentDecompileRepairContext();
bool IsCurrentDecompileRepairDeadlineExceeded();
bool ShouldAbortCurrentDecompileRepair(std::string* note,
                                       std::string_view note_prefix = {});

}  // namespace refii::lua::internal
