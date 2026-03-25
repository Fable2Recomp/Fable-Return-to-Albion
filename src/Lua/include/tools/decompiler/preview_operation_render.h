#pragma once

#include "tools/decompiler/operation_build.h"

#include <string>

namespace refii::lua {

enum class LuaPreviewOperationMode {
    kSideEffectFreeOnly,
    kStateTracking,
};

enum class LuaPreviewOperationDisposition {
    kApplied,
    kBuildFailure,
    kUnsupportedSideEffect,
    kUnsupportedTailCall,
    kUnsupportedCallShape,
    kUnsupportedOpcode,
};

struct LuaPreviewOperationContext {
    const std::vector<Lua51Instruction>& instructions;
    const RegisterDeclarations& declarations;
    const Lua51Function& function;
    int register_count = 0;
    std::vector<StraightLineValue>* values = nullptr;
    std::vector<bool>* declared = nullptr;
    LuaOperationCallArgumentsRenderer call_arguments_for;
    LuaOperationChildRenderer render_child;
};

bool TryApplyLuaPreviewOperation(const LuaPreviewOperationContext& context,
                                 int current,
                                 int line,
                                 LuaPreviewOperationMode mode,
                                 LuaPreviewOperationDisposition* disposition,
                                 std::string* build_error,
                                 int* next_current);

}  // namespace refii::lua
