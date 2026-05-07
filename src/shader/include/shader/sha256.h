#pragma once

#include <cstddef>
#include <string>
#include <string_view>

namespace refii::shader::crypto {

std::string ComputeSha256Hex(const void* data, std::size_t size);
inline std::string ComputeSha256Hex(std::string_view bytes) {
    return ComputeSha256Hex(bytes.data(), bytes.size());
}

}  // namespace refii::shader::crypto
