#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool ParseSimpleRenderedCall(std::string_view expression,
                             std::string* callee_out,
                             std::vector<std::string>* arguments_out);

}  // namespace refii::lua::internal
