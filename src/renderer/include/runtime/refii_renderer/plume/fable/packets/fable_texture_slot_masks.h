#pragma once

#include <cstdint>

namespace refii::renderer {

uint32_t FableKnownPixelShaderTextureSlotMask(uint32_t shader_address,
                                              uint64_t bytecode_hash);

}  // namespace refii::renderer
