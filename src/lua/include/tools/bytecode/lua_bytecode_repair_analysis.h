#pragma once

#include "tools/native/native_lua_chunk.h"

#include <cstddef>
#include <vector>

namespace refii::lua::internal {

bool IsLuaFunctionStatsImproved(const refii::lua::Lua51Chunk& original_chunk,
                                const refii::lua::Lua51Chunk& before_chunk,
                                const refii::lua::Lua51Chunk& after_chunk,
                                std::size_t function_index);

std::vector<std::size_t> CollectLuaMismatchedFunctionIndices(
    const refii::lua::Lua51Chunk& original_chunk,
    const refii::lua::Lua51Chunk& candidate_chunk);

}  // namespace refii::lua::internal
