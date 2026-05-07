#include "tools/decompiler/rendered/shared_cleanup_signal_helpers.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <algorithm>
#include <string_view>

namespace refii::lua::internal {

bool HasQuestLikeCleanupSignals(const std::vector<std::string>& lines) {
    std::size_t loop_markers = 0;
    const std::size_t scan_limit = std::min<std::size_t>(lines.size(), 6000);
    for (std::size_t index = 0; index < scan_limit; ++index) {
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(lines[index]));
        if (trimmed.empty()) {
            continue;
        }
        if (trimmed.find("CurrentState") != std::string_view::npos ||
            trimmed.find("MessageEvents.") != std::string_view::npos ||
            trimmed.find("coroutine.yield") != std::string_view::npos ||
            trimmed.find("-- UNHANDLED IF") != std::string_view::npos ||
            trimmed.find("while true do") != std::string_view::npos) {
            return true;
        }
        if (trimmed.find("while ") != std::string_view::npos ||
            trimmed.find("repeat") != std::string_view::npos ||
            trimmed.find("until ") != std::string_view::npos) {
            ++loop_markers;
            if (loop_markers >= 4) {
                return true;
            }
        }
    }
    return false;
}

bool HasTableAliasCleanupSignals(const std::vector<std::string>& lines) {
    std::size_t table_markers = 0;
    const std::size_t scan_limit = std::min<std::size_t>(lines.size(), 4096);
    for (std::size_t index = 0; index < scan_limit; ++index) {
        const std::string_view trimmed =
            refii::lua::TrimLuaWhitespace(refii::lua::StripLuaLineComment(lines[index]));
        if (trimmed.empty()) {
            continue;
        }
        if (trimmed.find('{') != std::string_view::npos || trimmed.find("Set") == 0 ||
            trimmed.find("[") != std::string_view::npos ||
            trimmed.find(".Actions") != std::string_view::npos) {
            ++table_markers;
            if (table_markers >= 3) {
                return true;
            }
        }
    }
    return false;
}

bool ApplyQuestStructuralCleanupIfRelevant(std::vector<std::string>* lines) {
    if (lines == nullptr || !HasQuestLikeCleanupSignals(*lines)) {
        return false;
    }
    return refii::lua::ApplyRenderedLuaQuestStructuralCleanupProfile(lines);
}

bool ApplyQualifiedTableBuilderCleanupIfRelevant(std::vector<std::string>* lines) {
    if (lines == nullptr || !HasTableAliasCleanupSignals(*lines)) {
        return false;
    }
    return refii::lua::ComposeRenderedLuaQualifiedTableBuilderCleanupTail(lines);
}

}  // namespace refii::lua::internal
