#pragma once

#include "tools/bytecode/lua_bytecode_oracles.h"

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

std::string_view ExtractLocalDiffMismatchNote(std::string_view note);
bool TryExactifyLocalDiffHeaderParameters(
    const refii::lua::audit::LuaBytecodeParityReference& parity_reference,
    std::string_view chunk_name,
    std::string* source,
    refii::lua::audit::LuaBytecodeParityResult* parity_out,
    std::string* note,
    std::string_view note_prefix,
    const std::vector<char>* input_bytes = nullptr);

}  // namespace refii::lua::internal
