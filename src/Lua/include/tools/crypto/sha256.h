#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::crypto {

std::string ComputeSha256Hex(const void* data, std::size_t size);
inline std::string ComputeSha256Hex(std::string_view bytes) {
    return ComputeSha256Hex(bytes.data(), bytes.size());
}
inline std::string ComputeSha256Hex(const std::vector<char>& bytes) {
    return ComputeSha256Hex(bytes.data(), bytes.size());
}

}  // namespace refii::lua::crypto
