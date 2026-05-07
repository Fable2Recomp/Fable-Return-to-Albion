#pragma once

#include "tools/decompiler/lua_decompiler_internal.h"

#include <string>
#include <string_view>

namespace refii::lua::internal {

SyntaxRepairAttemptResult TryRunOrderedSyntaxRepairPass(
    std::string& decompiled_source,
    std::string& validation_error,
    std::string_view validation_label,
    std::string* note,
    bool* tried_insert_before_reported_line,
    const OrderedSyntaxRepairPassOptions& options);

SyntaxRepairAttemptResult RunOrderedSyntaxRepairPassAttempts(
    std::string& decompiled_source,
    std::string& validation_error,
    std::string_view validation_label,
    std::string* note,
    bool* tried_insert_before_reported_line,
    const OrderedSyntaxRepairPassOptions& options,
    int max_attempts,
    bool note_cycles);

} // namespace refii::lua::internal
