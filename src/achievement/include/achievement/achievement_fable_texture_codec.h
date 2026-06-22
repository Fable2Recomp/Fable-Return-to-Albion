#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace refii::achievement {

bool DecodeFableCompressedBc1Mip(const std::uint8_t *body,
                                 std::size_t body_size,
                                 int &out_width,
                                 int &out_height,
                                 std::vector<std::uint8_t> &out_bc1,
                                 std::string *error = nullptr,
                                 bool comp11_layout = false);

bool DecodeFableCompressedVariant234(const std::uint8_t *body,
                                     std::size_t body_size,
                                     int mode,
                                     int width,
                                     int height,
                                     std::vector<std::uint8_t> &out_bytes,
                                     std::string *error = nullptr);

} // namespace refii::achievement
