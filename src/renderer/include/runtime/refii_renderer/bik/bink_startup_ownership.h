#pragma once

#include <cstdint>

namespace refii::renderer::bink {

bool BinkPlumeOwnsMovie(uint8_t *base, uint32_t movie_guest);

} // namespace refii::renderer::bink
