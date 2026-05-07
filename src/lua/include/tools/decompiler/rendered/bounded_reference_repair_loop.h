#pragma once

#include "tools/decompiler/rendered/repair_state_keys.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>

namespace refii::lua::internal {

namespace detail {

inline void AppendBoundedReferenceRepairNoProgressNote(std::string* note,
                                                       std::string_view key) {
    if (note == nullptr || key.empty()) {
        return;
    }
    *note = JoinNotes(*note, std::string(key));
}

}  // namespace detail

template <typename AbortFn, typename RepairFn, typename ValidateFn>
void RunBoundedReferenceRepairLoop(int pass_budget,
                                   bool guard_repeated_source,
                                   std::string* source,
                                   std::string* validation_error,
                                   std::string* note,
                                   std::string_view no_progress_note,
                                   AbortFn&& should_abort,
                                   RepairFn&& try_repair,
                                   ValidateFn&& validate_source) {
    if (pass_budget <= 0) {
        if (note != nullptr && !no_progress_note.empty()) {
            *note = JoinNotes(*note,
                              std::string(no_progress_note) + "_disabled_fast_path");
        }
        return;
    }
    if (source == nullptr) {
        return;
    }

    std::string local_validation_error;
    std::string* validation_error_ptr = validation_error;
    if (validation_error_ptr == nullptr) {
        local_validation_error = validate_source();
        validation_error_ptr = &local_validation_error;
    }

    constexpr std::size_t kMaxReferenceRepairSourceBytes = 2 * 1024 * 1024;
    constexpr std::size_t kMaxReferenceRepairSourceGrowthBytes = 512 * 1024;
    const std::size_t initial_source_size = source->size();
    std::string previous_source_identity;
    std::string previous_state;
    const int pass_cap = std::max(pass_budget, 1);
    for (int pass = 0; pass < pass_cap; ++pass) {
        (void)pass;
        if (should_abort()) {
            break;
        }
        const std::size_t allowed_source_size = std::min(
            kMaxReferenceRepairSourceBytes,
            initial_source_size +
                std::max(initial_source_size, kMaxReferenceRepairSourceGrowthBytes));
        if (source->size() > allowed_source_size) {
            detail::AppendBoundedReferenceRepairNoProgressNote(
                note, std::string(no_progress_note) + "_source_growth_guard");
            break;
        }
        const std::string before_state =
            BuildRepairQualityStateKey(*source, *validation_error_ptr);
        if (guard_repeated_source) {
            const std::string source_identity = ComputeStableBytecodeIdentity(*source);
            if (source_identity == previous_source_identity) {
                break;
            }
            previous_source_identity = source_identity;
        }
        if (!try_repair()) {
            break;
        }
        if (source->size() > allowed_source_size) {
            detail::AppendBoundedReferenceRepairNoProgressNote(
                note, std::string(no_progress_note) + "_source_growth_guard");
            break;
        }
        *validation_error_ptr = validate_source();
        const std::string after_state =
            BuildRepairQualityStateKey(*source, *validation_error_ptr);
        if (after_state == before_state || after_state == previous_state) {
            detail::AppendBoundedReferenceRepairNoProgressNote(note, no_progress_note);
            break;
        }
        previous_state = std::move(after_state);
    }
}

}  // namespace refii::lua::internal
