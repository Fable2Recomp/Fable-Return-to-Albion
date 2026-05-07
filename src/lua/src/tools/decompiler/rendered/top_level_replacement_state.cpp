#include "tools/decompiler/rendered/top_level_replacement_state.h"

#include "tools/decompiler/rendered/repair_state_keys.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <sstream>

namespace refii::lua::internal {

std::uint64_t ComputeLightweightSourceFingerprint(std::string_view text) {
    std::uint64_t hash = 1469598103934665603ull;
    const auto mix_byte = [&](unsigned char byte) {
        hash ^= static_cast<std::uint64_t>(byte);
        hash *= 1099511628211ull;
    };
    const auto mix_range = [&](std::size_t begin, std::size_t end) {
        for (std::size_t index = begin; index < end; ++index) {
            mix_byte(static_cast<unsigned char>(text[index]));
        }
    };

    const std::size_t size = text.size();
    for (int shift = 0; shift < 8; ++shift) {
        mix_byte(static_cast<unsigned char>((size >> (shift * 8)) & 0xffu));
    }
    if (size == 0) {
        return hash;
    }

    const std::size_t prefix_count = std::min<std::size_t>(size, 128);
    mix_range(0, prefix_count);

    const std::size_t suffix_count = std::min<std::size_t>(size - prefix_count, 128);
    if (suffix_count > 0) {
        mix_range(size - suffix_count, size);
    }

    if (size > prefix_count + suffix_count) {
        const std::size_t remaining_begin = prefix_count;
        const std::size_t remaining_end = size - suffix_count;
        const std::size_t remaining = remaining_end - remaining_begin;
        const std::size_t stride = std::max<std::size_t>(1, remaining / 64);
        for (std::size_t index = remaining_begin; index < remaining_end; index += stride) {
            mix_byte(static_cast<unsigned char>(text[index]));
        }
    }

    return hash;
}

std::string BuildTopLevelReplacementState(std::string_view source,
                                          std::string_view validation_error) {
    std::ostringstream stream;
    const std::uint64_t replacement_hash = ComputeLightweightSourceFingerprint(source);
    stream << ExtractLuaErrorLine(validation_error) << "|"
           << BuildRepairFamilyStateKey(source, validation_error) << "|"
           << BuildRepairProgressStateKey(source, validation_error) << "|"
           << replacement_hash;
    return stream.str();
}

}  // namespace refii::lua::internal
