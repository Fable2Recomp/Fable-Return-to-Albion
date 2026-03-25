#pragma once

#include "tools/decompiler/lua51_analysis.h"

#include <string>

namespace refii::lua {

bool RenderLua51StraightLineFunction(const Lua51Function& function,
                                     std::string* output,
                                     std::string* error = nullptr);

bool RenderLua51StraightLineFunctionRaw(const Lua51Function& function,
                                        std::string* output,
                                        std::string* error = nullptr);

bool RenderLua51StraightLineFunctionBody(const Lua51Function& function,
                                         std::string* output,
                                         std::string* error = nullptr);

bool RenderLua51StraightLineFunctionBodyRaw(const Lua51Function& function,
                                            std::string* output,
                                            std::string* error = nullptr);

}  // namespace refii::lua
