#pragma once

#include <string>

namespace refii::lua::workspace {

bool ApplyFastCameraRawReadableRecovery(std::string* source);
bool ApplyFastStringAndCageCameraRawReadableRecovery(std::string* source);

}  // namespace refii::lua::workspace
