#include "runtime/refii_renderer/plume/fable/packets/fable_texture_slot_masks.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"

namespace refii::renderer {

uint32_t FableKnownPixelShaderTextureSlotMask(uint32_t shader_address,
                                              uint64_t bytecode_hash) {
  constexpr uint32_t kSlot0 = 1u << 0;
  constexpr uint32_t kSlot13 = 1u << kFablePrivateRectCopyTextureSlot;
  constexpr uint32_t kSlots012 = (1u << 0) | (1u << 1) | (1u << 2);

  switch (bytecode_hash) {
    case kFableBinkIndexedWrapperPixelShaderHash:  // 0180_shader_bink_movie
      return kSlots012;
    case 0x18B05F955703CF43ull:  // 0135_pshader_lighthalo
      return kSlot13;
    case 0x824604B7737F75AFull:  // 0160_shader_screenfacingatlassprite
    case kFablePostprocessingGaussBlurPixelHash:  // 0170_shader_postprocessing_gaussblur
    case kFableDecalPixelHash:                    // 0159_shader_decal
    case kFablePostprocessingDownscale4PixelHash:  // 0166_shader_postprocessing_downscale4
    case 0x4BAC6894265E28DEull:  // 0167_shader_postprocessing_downscale_and_composite
      return kSlot0;
    case 0x25AFD47B851CE267ull:  // 0163_shader_postprocessing_copy
    case kFableStandardMaterialBlt1Glt1Mist0Amb1Nrm1Scr1PixelHash:
      return kSlots012;
    default:
      break;
  }

  // Title HDR/tone-map composite. Keep this address-scoped until more Fable
  // standard-material variants are proven to share the same sampler contract.
  switch (shader_address) {
    case 0x4251B2B0u:  // 0166_shader_postprocessing_downscale4
    case 0x4252B2B0u:  // 0166_shader_postprocessing_downscale4
    case 0x4252B3D0u:  // 0166_shader_postprocessing_downscale4
    case 0x425332B0u:  // 0166_shader_postprocessing_downscale4
    case 0x425333D0u:  // 0167_shader_postprocessing_downscale_and_composite sibling
    case 0x4253B2B0u:  // 0166_shader_postprocessing_downscale4
    case 0x4253B3D0u:  // 0166_shader_postprocessing_downscale4 sibling
    case 0x4251B070u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4252B070u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4252B190u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4252B4F0u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x42533070u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4253B070u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4253B190u:  // 0167_shader_postprocessing_downscale_and_composite sibling
    case 0x42220410u:  // title GUI/postprocess sibling observed in menu flow
      return kSlot0;
    case 0x4C27D8D0u:
      return kSlots012;
    default:
      return 0;
  }
}

}  // namespace refii::renderer
