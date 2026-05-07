#pragma once

#include "tools/native/native_lua_decompiler.h"

#include <string>

namespace refii::lua::workspace {

bool RunLuaWorkspaceDecompilePipeline(const LuaWorkspaceDecompileOptions& raw_options,
                                      LuaWorkspaceDecompileSummary* summary,
                                      std::string* error);

}  // namespace refii::lua::workspace
