#include "tools/decompiler/rendered/shared_guard_cleanup_repairs.h"

#include "tools/decompiler/lua_decompiler_internal.h"

#include <string_view>

namespace refii::lua::internal {
namespace {

bool LooksLikeNilGuardCondition(std::string_view condition) {
    condition = TrimWhitespace(condition);
    return condition.find("~= nil") != std::string_view::npos ||
           condition.find("nil ~=") != std::string_view::npos;
}

}  // namespace

bool TryRepairEmptyNilGuardedAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view if_trimmed =
            TrimWhitespace(StripLineComment((*lines)[line_index]));
        if (!StartsWith(if_trimmed, "if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string condition(
            TrimWhitespace(if_trimmed.substr(3, if_trimmed.size() - 8)));
        if (!LooksLikeNilGuardCondition(condition)) {
            continue;
        }

        auto next_nonempty = [&](std::size_t start) -> std::size_t {
            for (std::size_t i = start; i < lines->size(); ++i) {
                if (!TrimWhitespace(StripLineComment((*lines)[i])).empty()) {
                    return i;
                }
            }
            return lines->size();
        };

        const std::size_t else_line = next_nonempty(line_index + 1);
        if (else_line >= lines->size() ||
            TrimWhitespace(StripLineComment((*lines)[else_line])) != "else") {
            continue;
        }
        const std::size_t end_line = next_nonempty(else_line + 1);
        if (end_line >= lines->size() ||
            TrimWhitespace(StripLineComment((*lines)[end_line])) != "end") {
            continue;
        }
        const std::size_t assignment_line = next_nonempty(end_line + 1);
        if (assignment_line >= lines->size()) {
            continue;
        }

        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment((*lines)[assignment_line], &indent, &lhs, &rhs)) {
            continue;
        }

        (*lines)[line_index] = indent + lhs + " = ((" + condition + ") and " + rhs + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(assignment_line + 1));
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua::internal
