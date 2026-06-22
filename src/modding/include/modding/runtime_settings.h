#pragma once

#include <vector>

namespace refii::modding {

enum class RuntimeSettingType {
  Bool,
  Int,
  Double,
  String,
};

struct RuntimeSettingDefinition {
  const char* name;
  const char* category;
  RuntimeSettingType type;
  const char* default_value;
  const char* description;
  const char* allowed_values = nullptr;
};

const std::vector<RuntimeSettingDefinition>& GetRuntimeSettingDefinitions();

}  // namespace refii::modding
