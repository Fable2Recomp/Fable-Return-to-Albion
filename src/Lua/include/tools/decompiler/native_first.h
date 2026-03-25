#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::decompiler {

struct NativeMainChunkCandidate {
    std::string source;
    std::string note;
    bool used_raw_renderer = false;
    bool syntax_validated = false;
};

bool TryRenderValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                       std::string_view chunk_name,
                                       NativeMainChunkCandidate* candidate);

}  // namespace refii::lua::decompiler
