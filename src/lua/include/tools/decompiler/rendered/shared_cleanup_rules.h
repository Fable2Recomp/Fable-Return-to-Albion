#pragma once

#include "tools/decompiler/lua_decompiler_internal.h"

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

enum class RenderedCleanupRuleFamily {
    kQuest,
    kAlias,
    kTable,
    kCondition,
    kCall,
    kLocal,
    kLoop,
    kGuard,
    kReturn,
    kMisc,
};

enum class RenderedCleanupRuleScope {
    kBoth,
    kAggressiveOnly,
    kValidatedOnly,
};

struct RenderedCleanupRuleDescriptor {
    const char* id;
    RenderedCleanupRuleFamily family;
    RenderedCleanupRuleScope scope;
    bool (*precheck)(const std::vector<std::string>& lines);
    bool (*apply)(std::vector<std::string>* lines);
};

bool IsRuleEnabledForProfile(const RenderedCleanupRuleDescriptor& rule,
                             RenderedLuaCleanupProfile profile);
bool TryApplySharedUnhandledIfCommentCleanup(std::string& source, std::string* note);
bool TryApplySharedRenderedOutputCleanup(std::string& decompiled_source,
                                         std::string& validation_error,
                                         std::string_view validation_label,
                                         std::string* note);

}  // namespace refii::lua::internal
