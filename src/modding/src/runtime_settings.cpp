#include "modding/runtime_settings.h"

namespace refii::modding {

const std::vector<RuntimeSettingDefinition>& GetRuntimeSettingDefinitions() {
  static const std::vector<RuntimeSettingDefinition> definitions = {
                        {"lua_use_mod_overrides",
       "Lua",
       RuntimeSettingType::Bool,
       "true",
       "Prefer decompiled loose Lua files from the mod workspace."},
      {"lua_trace_mod_overrides",
       "Lua",
       RuntimeSettingType::Bool,
       "false",
       "Log Lua workspace override matches and fallbacks."},
      {"lua_mod_workspace_root",
       "Lua",
       RuntimeSettingType::String,
       "",
       "Optional host Lua mod workspace root."},
  };
  return definitions;
}

}  // namespace refii::modding
