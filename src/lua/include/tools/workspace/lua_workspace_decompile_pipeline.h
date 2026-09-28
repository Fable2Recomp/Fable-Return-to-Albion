#pragma once

#include "tools/native/native_lua_decompiler.h"

#include <string>

namespace refii::lua::workspace {

// The decompile step of the workspace: luadec plus the in-process oracle over
// every script in the luasource/ cache. DecompileLuaWorkspace forwards here.
bool RunLuaWorkspaceDecompilePipeline(const LuaWorkspaceDecompileOptions& options,
                                      LuaWorkspaceDecompileSummary* summary,
                                      std::string* error);

}  // namespace refii::lua::workspace
