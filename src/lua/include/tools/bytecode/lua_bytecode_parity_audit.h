#pragma once

#include "tools/native/native_lua_decompiler.h"

#include <string>

namespace refii::lua::audit {

bool RunLuaBytecodeParityAudit(const LuaBytecodeParityAuditOptions& raw_options,
                               LuaBytecodeParityAuditSummary* summary,
                               std::string* error);

}  // namespace refii::lua::audit
