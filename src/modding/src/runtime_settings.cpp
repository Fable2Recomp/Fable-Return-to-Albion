#include "modding/runtime_settings.h"

namespace refii::modding {

const std::vector<RuntimeSettingDefinition>& GetRuntimeSettingDefinitions() {
  static const std::vector<RuntimeSettingDefinition> definitions = {
      {"vfs_loose_asset_backend",
       "VFS",
       RuntimeSettingType::String,
       "native",
       "VFS backend: refii enables loose asset and mod overlays, native uses the original Fable 2 VFS.",
       "native,refii"},
      {"vfs_mods_root",
       "VFS",
       RuntimeSettingType::String,
       "mods",
       "Assets-root mod folder containing installable loose-file mods. The default resolves to <assets_root>/mods."},
      {"vfs_enabled_mods",
       "VFS",
       RuntimeSettingType::String,
       "",
       "Optional semicolon-separated enabled mod folder names under vfs_mods_root; mods/enabled.txt is also read."},
      {"vfs_mod_roots",
       "VFS",
       RuntimeSettingType::String,
       "",
       "Developer override: semicolon-separated absolute or relative mod asset roots. Normal mod installs live under <assets_root>/mods."},
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
