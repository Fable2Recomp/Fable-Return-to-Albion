#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"

#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"

namespace refii::renderer {

bool IsFableTitleSceneVertexShader(uint32_t shader) {
  return shader == kFableTitleSceneVertexShader ||
         shader == kFableTitleSceneMenuVertexShader ||
         shader == kFableTitleSceneTextVertexShader ||
         shader == 0x4C273920u ||
         shader == 0x4C274180u || shader == 0x4C274190u ||
         shader == 0x4C2743A0u ||
         shader == 0x4C2744F0u ||
         shader == 0x4C271420u ||
         shader == 0x4C276310u || shader == 0x4C276720u ||
         shader == 0x4C277730u ||
         shader == 0x4C277C60u ||
         shader == 0x4C278010u ||
         shader == 0x4C278030u || shader == 0x4C278070u ||
         shader == 0x4C278420u ||
         shader == 0x4C278440u ||
         shader == 0x4C278480u ||
         shader == 0x4C278640u ||
         shader == 0x4C14F970u ||
         shader == 0x4C278220u ||
         shader == 0x4C278180u || shader == 0x4C2780A0u ||
         shader == 0x4C278E60u ||
         shader == 0x4C277D70u || shader == 0x4C277C90u ||
         shader == 0x4C277A90u ||
         shader == 0x4C282C40u ||
         shader == 0x4C289810u ||
         shader == 0x4C29E1C0u ||
         shader == 0x4C293FD0u ||
         shader == 0x4C284750u ||
         shader == 0x4C2B92E0u ||
         shader == 0x4C2BEC40u ||
         shader == 0x4C2BCC20u ||
         shader == 0x4C2C03C0u ||
         shader == 0x4C2C2070u ||
         shader == 0x4C27D5C0u ||
         shader == 0x4C27CEC0u;
}

bool IsFableTitleGuiSingleTexturePostprocessPixelShader(uint32_t shader,
                                                        uint64_t pixel_hash) {
  if (pixel_hash == kFableTitleScreenFacingAtlasSpritePixelHash ||
      pixel_hash == kFableTexturedQuadPreserveStencilPixelHash) {
    return true;
  }

  switch (shader) {
    case 0x4215A710u:  // 0170_shader_postprocessing_gaussblur
    case 0x4216D090u:  // 0160_shader_screenfacingatlassprite
    case 0x4E107770u:  // 0159_shader_decal
    case 0x4251B2B0u:  // 0166_shader_postprocessing_downscale4
    case 0x4252B2B0u:  // 0166_shader_postprocessing_downscale4
    case 0x4252B3D0u:  // 0166_shader_postprocessing_downscale4
    case 0x425332B0u:  // 0166_shader_postprocessing_downscale4
    case 0x425333D0u:  // 0167_shader_postprocessing_downscale_and_composite sibling
    case 0x4252B070u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4251B070u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4252B190u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4252B4F0u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x42533070u:  // 0167_shader_postprocessing_downscale_and_composite
    case 0x4253B2B0u:  // 0166_shader_postprocessing_downscale4 sibling
    case 0x4253B3D0u:  // 0166_shader_postprocessing_downscale4 sibling
    case 0x4253B070u:  // 0167_shader_postprocessing_downscale_and_composite sibling
    case 0x4253B190u:  // 0167_shader_postprocessing_downscale_and_composite sibling
    case 0x42220410u:  // title GUI/postprocess sibling observed in menu flow
      return true;
    default:
      return false;
  }
}

bool IsFableTitleGuiText3DPixelShaderAddress(uint32_t shader) {
  switch (shader) {
    case 0x4216D090u:
    case 0x4215A710u:
    case 0x42220410u:
    case 0x42220510u:
    case 0x42608610u:
    case 0x4E107770u:
    case 0x4252B2B0u:
    case 0x4C27E100u:
    case 0x4C27CCF0u:
    case 0x4C2786B0u:
    case 0x4252B070u:
    case 0x4C14D390u:
    case 0x425332B0u:
    case 0x4252B4F0u:
    case 0x4C1F6640u:
    case 0x42533070u:
    case 0x4C275860u:
    case 0x4253B2B0u:
    case 0x4253B070u:
    case 0x4C276C70u:
    case 0x4C14D0C0u:
    case 0x4C1F9F20u:
    case 0x4C278A10u:
    case 0x4C14F610u:
    case 0x4C14CA80u:
    case 0x4C273850u:
    case 0x4C277A80u:
    case 0x4C2773E0u:
    case 0x4C14C100u:
    case 0x4C277550u:
    case 0x4C279940u:
    case 0x4C277AB0u:
    case 0x4C19BF40u:
    case 0x4C277600u:
    case 0x4C141B00u:
    case 0x4252B3D0u:
    case 0x4252B190u:
    case 0x4C279D90u:
    case 0x4C2779D0u:
    case 0x4C2778E0u:
    case 0x4C277650u:
    case 0x4C277030u:
    case 0x4C278DE0u:
    case 0x4C278E90u:
    case 0x4C2787F0u:
    case 0x4C27A060u:
    case 0x4C278960u:
    case 0x4C27AD50u:
    case 0x4C278CF0u:
    case 0x4C278CC0u:
    case 0x4C278EC0u:
    case 0x4C278A60u:
    case 0x4C27ADD0u:
    case 0x4C277850u:
    case 0x4C27C830u:
    case 0x4C277070u:
    case 0x4251B2B0u:
    case 0x4251B070u:
    case 0x4C27DC40u:
      return true;
    default:
      return false;
  }
}

bool IsFableTitleTextureIndirectionChainAddress(uint32_t address) {
  return ((address >= 0x4C120000u && address < 0x4C140000u) &&
          (address & (alignof(uint32_t) - 1u)) == 0);
}

bool IsFableTitleGuiTextAtlasObjectAddress(uint32_t address) {
  return ((address >= 0x404D0000u && address < 0x404E0000u) ||
          (address >= 0x404E0000u && address < 0x404F0000u) ||
          (address >= 0x4051C000u && address < 0x40522000u) ||
          address == 0x40743590u ||
          (address >= 0x405B0000u && address < 0x405C0000u) ||
          (address >= 0x40940000u && address < 0x40950000u) ||
          (address >= 0x44127000u && address < 0x44128000u)) &&
         (address & (alignof(uint32_t) - 1u)) == 0;
}

bool IsFableTitleTextureProbeAddress(uint32_t address) {
  const uint32_t normalized_address = NormalizeFableGpuAddress(address);
  switch (address) {
    case 0x40070210u:
    case 0x40070250u:
    case 0x40070310u:
    case 0x404BB310u:
    case 0x4C1312E0u:
    case 0x4C130E00u:
    case 0x4C130E34u:
    case 0x404BB350u:
    case 0x404BB490u:
    case 0x404BB4D0u:
    case 0x404DD2D0u:
    case 0x404DD310u:
    case 0x404DD350u:
    case 0x404DD390u:
    case 0x404DD3D0u:
    case 0x404DD450u:
    case 0x404DD490u:
    case 0x404DD4D0u:
    case 0x404DD510u:
    case 0x404DDC50u:
    case 0x404DDC90u:
    case 0x404DDB50u:
    case 0x404DDB90u:
    case 0x404DDD90u:
    case 0x404DDDD0u:
    case 0x404DE450u:
    case 0x404DE490u:
    case 0x404DE4D0u:
    case 0x404DE510u:
    case 0x404DE550u:
    case 0x404DE610u:
    case 0x404DE690u:
    case 0x404DE6D0u:
    case 0x404DE710u:
    case 0x40513BD0u:
    case 0x40513C10u:
    case 0x40513CD0u:
    case 0x40513D10u:
    case 0x40513D90u:
    case 0x40513DD0u:
    case 0x4051B4D0u:
    case 0x4051BB90u:
    case 0x4051CB50u:
    case 0x4051CB90u:
    case 0x4051DB50u:
    case 0x4051DB90u:
    case 0x4051DC10u:
    case 0x4051F650u:
    case 0x4051F610u:
    case 0x4051FD90u:
    case 0x4051FF90u:
    case 0x4C12C1C8u:
    case 0x4C12C1FCu:
    case 0x4C12C230u:
    case 0x4C12C264u:
    case 0x4C12C298u:
    case 0x4C12C300u:
    case 0x4C12C368u:
    case 0x4C131688u:
    case 0x4C1316BCu:
    case 0x4C132C70u:
    case 0x4C132CA4u:
    case 0x4C132CD8u:
    case 0x4C133150u:
    case 0x4C133184u:
    case 0x44127210u:
    case 0x44127278u:
    case 0x441272ACu:
    case 0x441272E0u:
    case 0x44127314u:
    case 0x44127348u:
    case 0x4412737Cu:
    case 0x44127480u:
    case 0x441274B4u:
    case 0x441274E8u:
    case 0x4412751Cu:
    case 0x13520000u:
    case 0x13530000u:
    case 0x13540000u:
    case 0x13550000u:
    case 0x13580000u:
    case 0x13590000u:
    case 0x16C40000u:
    case 0x14740000u:
    case 0x14760000u:
    case 0x14780000u:
    case 0x147A0000u:
    case 0x15FE0000u:
    case 0x1A0D7000u:
    case 0x1A0D9000u:
    case 0x1A0EA000u:
    case 0x15800000u:
    case 0x1A0DB000u:
    case 0x1A0F9000u:
    case 0x1A0EC000u:
    case 0x1A0EE000u:
    case 0x1A048000u:
    case 0x1A07E000u:
    case 0x12656000u:
    case 0x12681000u:
    case 0x12771000u:
    case 0x12759000u:
    case 0x12867000u:
    case 0x1289D000u:
    case 0x128D3000u:
    case 0x128D9000u:
    case 0x1293F000u:
    case 0x1FAA0000u:
    case 0x1FAB0000u:
    case 0x1FAB1000u:
    case 0x1FAB2000u:
    case 0x1FAB3000u:
    case 0x1FAB4000u:
    case 0x1FAB5000u:
      return true;
    default:
      break;
  }
  if (((normalized_address >= 0x4051C000u &&
        normalized_address < 0x40522000u) ||
       normalized_address == 0x40743590u) &&
      (normalized_address & (alignof(uint32_t) - 1u)) == 0) {
    return true;
  }
  if (normalized_address >= 0x44127000u &&
      normalized_address < 0x44128000u &&
      (normalized_address & (alignof(uint32_t) - 1u)) == 0) {
    return true;
  }
  if (normalized_address == address) {
    return false;
  }
  return IsFableTitleTextureProbeAddress(normalized_address);
}

bool IsFableTitleGuiTextAtlasBaseAddress(uint32_t address) {
  const uint32_t normalized_address = NormalizeFableGpuAddress(address);
  return normalized_address == 0x13520000u ||
         normalized_address == 0x13530000u ||
         normalized_address == 0x13540000u ||
         normalized_address == 0x13580000u ||
         normalized_address == 0x13590000u ||
         normalized_address == 0x1A07E000u ||
         normalized_address == 0x1A0D7000u ||
         normalized_address == 0x1A0D9000u ||
         normalized_address == 0x1A0EA000u ||
         normalized_address == 0x1FAB4000u ||
         normalized_address == 0x1FAB5000u;
}

bool IsFableTitlePrivateTextureProxyAddress(uint32_t address) {
  switch (address) {
    case 0x40130C90u:
    case 0x40130CD0u:
    case 0x40131610u:
    case 0x40131650u:
    case 0x40131C90u:
    case 0x40131CD0u:
    case 0x40131D10u:
    case 0x40137C90u:
    case 0x40137CD0u:
    case 0x40137D10u:
    case 0x40136C90u:
    case 0x40136CD0u:
    case 0x40133CD0u:
    case 0x40133D10u:
    case 0x404DE450u:
    case 0x404DE4D0u:
    case 0x404DE510u:
    case 0x404DE550u:
    case 0x404DE590u:
    case 0x404DE5D0u:
    case 0x404DE610u:
    case 0x404DE650u:
    case 0x404DE690u:
    case 0x404DE6D0u:
    case 0x404DE710u:
    case 0x40513AD0u:
    case 0x40513B10u:
    case 0x40513B50u:
    case 0x40513B90u:
    case 0x40513BD0u:
    case 0x40513C10u:
    case 0x40513C50u:
    case 0x40513C90u:
    case 0x40513CD0u:
    case 0x40513D10u:
    case 0x40513D90u:
    case 0x40513DD0u:
    case 0x12656000u:
    case 0x12681000u:
    case 0x12759000u:
    case 0x12771000u:
    case 0x12867000u:
    case 0x1289D000u:
    case 0x128D3000u:
    case 0x128D9000u:
    case 0x1293F000u:
    case 0x1FAA0000u:
    case 0x1A048000u:
      return true;
    default:
      return false;
  }
}

bool IsFablePrivateTextureCopyProxyAddress(uint32_t address) {
  switch (address) {
    case 0x40130C90u:
    case 0x40130CD0u:
    case 0x40131610u:
    case 0x40131650u:
    case 0x40131C90u:
    case 0x40131CD0u:
    case 0x40131D10u:
    case 0x40137C90u:
    case 0x40137CD0u:
    case 0x40137D10u:
    case 0x40136C90u:
    case 0x40136CD0u:
    case 0x40133CD0u:
    case 0x40133D10u:
      return true;
    default:
      return false;
  }
}

bool IsFableHeaderOnlyPrivateTextureProxyAddress(uint32_t address) {
  switch (address) {
    case 0x404DE550u:
    case 0x404DE590u:
    case 0x404DE5D0u:
    case 0x404DE610u:
    case 0x404DE650u:
    case 0x404DE690u:
    case 0x404DE6D0u:
    case 0x404DE710u:
      return true;
    default:
      return false;
  }
}

bool IsFableMutablePrivateTextureHeaderAddress(uint32_t address) {
  if (IsFableHeaderOnlyPrivateTextureProxyAddress(address)) {
    return true;
  }
  switch (address) {
    case 0x40513AD0u:
    case 0x40513B10u:
    case 0x40513B50u:
    case 0x40513B90u:
    case 0x40513BD0u:
    case 0x40513C10u:
    case 0x40513C50u:
    case 0x40513C90u:
    case 0x40513CD0u:
    case 0x40513D10u:
    case 0x40513D90u:
    case 0x40513DD0u:
    case 0x12656000u:
    case 0x12681000u:
    case 0x12759000u:
    case 0x12771000u:
    case 0x12867000u:
    case 0x1289D000u:
    case 0x128D3000u:
    case 0x128D9000u:
    case 0x1293F000u:
      return true;
    default:
      return false;
  }
}

bool IsFableTinyTitleUploadedTextureAddress(uint32_t address) {
  switch (address) {
    case 0x401314D0u:
    case 0x401317D0u:
    case 0x40131810u:
    case 0x40131850u:
    case 0x40131890u:
    case 0x401318D0u:
    case 0x40131910u:
    case 0x404DE490u:
    case 0x404DE4D0u:
    case 0x404DE510u:
    case 0x404DE550u:
    case 0x1FC40000u:
    case 0x1FAB0000u:
    case 0x1FAB1000u:
    case 0x1FAB2000u:
    case 0x1FAB3000u:
    case 0x1FAB4000u:
    case 0x1FAB5000u:
      return true;
    default:
      return false;
  }
}

bool IsFableTinyTitleUploadedTextureFilterAddress(uint32_t address) {
  switch (address) {
    case 0x1A0D7000u:
    case 0x1FAB0000u:
    case 0x1FAB1000u:
    case 0x1FAB2000u:
    case 0x1FAB3000u:
    case 0x1FAB4000u:
    case 0x1FAB5000u:
      return true;
    default:
      break;
  }
  return IsFableTinyTitleUploadedTextureAddress(address);
}

bool IsFableExactTitleUploadedTextureFilterAddress(uint32_t address) {
  switch (address) {
    case 0x44127210u:
    case 0x44127278u:
    case 0x441272ACu:
    case 0x441272E0u:
    case 0x44127314u:
    case 0x44127348u:
    case 0x4412737Cu:
    case 0x44127480u:
    case 0x441274B4u:
    case 0x441274E8u:
    case 0x4412751Cu:
    case 0x13520000u:
      return true;
    default:
      break;
  }
  return address >= 0x13530000u && address <= 0x135B0000u &&
         ((address - 0x13530000u) % 0x10000u) == 0;
}

FablePrivateTextureProxyRole FablePrivateTextureProxyRoleForAddress(
    uint32_t address) {
  switch (address) {
    case 0x40130C90u:
    case 0x40131610u:
    case 0x40131C90u:
    case 0x40137C90u:
    case 0x40136C90u:
      return FablePrivateTextureProxyRole::kDepth;
    case 0x40130CD0u:
    case 0x40131650u:
    case 0x40131CD0u:
    case 0x40131D10u:
    case 0x40133CD0u:
    case 0x40133D10u:
    case 0x40136CD0u:
    case 0x40137CD0u:
    case 0x40137D10u:
      return FablePrivateTextureProxyRole::kColor;
    default:
      return FablePrivateTextureProxyRole::kUnknown;
  }
}

bool IsFableTitleBillboardAtlasUploadCarrierAddress(uint32_t address) {
  return address == 0x4C132C70u || address == 0x4C133150u ||
         address == 0x4C133184u ||
         IsFableTitleTextureIndirectionChainAddress(address);
}

bool IsFableTitleBillboardAtlasUploadFilterAddress(uint32_t address) {
  if (IsFableTitleBillboardTileUploadObjectAddress(address) ||
      IsFableTitleBillboardTileUploadHeaderAddress(address) ||
      IsFableTitleBillboardTileUploadBaseAddress(address)) {
    return true;
  }
  switch (address) {
    case kFableCanonicalTitleBillboardBase:
    case 0x4C132C70u:
    case 0x4C132CA4u:
    case 0x4C132CD8u:
    case 0x4C133150u:
    case 0x4C133184u:
      return true;
    default:
      return false;
  }
}

bool IsFableTitleGuiTextAtlasUploadFilterAddress(uint32_t address) {
  if (IsFableTitleGuiTextAtlasObjectAddress(address)) {
    return true;
  }
  switch (address) {
    case 0x1A0D9000u:
    case 0x1A0EA000u:
      return true;
    default:
      return false;
  }
}

bool IsFableTitleBillboardTileUploadBaseAddress(uint32_t base) {
  const auto in_strided_range = [](uint32_t value, uint32_t first,
                                   uint32_t last, uint32_t stride) {
    return value >= first && value <= last &&
           ((value - first) % stride) == 0;
  };
  return in_strided_range(base, kFableTitleBillboardTileFirstBase,
                          kFableTitleBillboardTileLastBase,
                          kFableTitleBillboardTileBaseStride) ||
         in_strided_range(base, kFableTitleBillboardTileAltFirstBase,
                          kFableTitleBillboardTileAltLastBase,
                          kFableTitleBillboardTileAltBaseStride) ||
         in_strided_range(base, kFableTitleBillboardTileHeaderFirstBase,
                          kFableTitleBillboardTileHeaderLastBase,
                          kFableTitleBillboardTileHeaderBaseStride) ||
         base == 0x13520000u ||
         in_strided_range(base, 0x13530000u, 0x135B0000u, 0x00010000u);
}

bool IsFableTitleBillboardTileUploadObjectAddress(uint32_t address) {
  const auto in_strided_range = [](uint32_t value, uint32_t first,
                                   uint32_t last) {
    return value >= first && value <= last &&
           ((value - first) % kFableTitleBillboardTileObjectStride) == 0;
  };
  return in_strided_range(address, kFableTitleBillboardTileFirstObject,
                          kFableTitleBillboardTileLastObject) ||
         in_strided_range(address, kFableTitleBillboardTileAltFirstObject,
                          kFableTitleBillboardTileAltLastObject) ||
         in_strided_range(address, kFableTitleBillboardTileHeaderFirstObject,
                          kFableTitleBillboardTileHeaderLastObject);
}

bool IsFableTitleBillboardTileUploadHeaderAddress(uint32_t address) {
  if (address < kFableTitleBillboardTileHeaderOffset) {
    return false;
  }
  return IsFableTitleBillboardTileUploadObjectAddress(
      address - kFableTitleBillboardTileHeaderOffset);
}

uint32_t FableTitleBillboardTileHeaderAddressForObject(uint32_t object) {
  return IsFableTitleBillboardTileUploadObjectAddress(object)
             ? object + kFableTitleBillboardTileHeaderOffset
             : 0u;
}

uint32_t FableTitleBillboardTileObjectAddressForHeader(uint32_t header) {
  return IsFableTitleBillboardTileUploadHeaderAddress(header)
             ? header - kFableTitleBillboardTileHeaderOffset
             : 0u;
}

uint32_t FableTitleBillboardTileObjectAddressForBase(uint32_t base) {
  const auto map_base_to_object = [](uint32_t value, uint32_t first_base,
                                     uint32_t last_base, uint32_t base_stride,
                                     uint32_t first_object,
                                     uint32_t last_object) {
    if (value < first_base || value > last_base ||
        ((value - first_base) % base_stride) != 0) {
      return 0u;
    }
    const uint32_t index = (value - first_base) / base_stride;
    const uint32_t object =
        first_object + index * kFableTitleBillboardTileObjectStride;
    return object <= last_object ? object : 0u;
  };

  if (uint32_t object = map_base_to_object(
          base, kFableTitleBillboardTileFirstBase,
          kFableTitleBillboardTileLastBase, kFableTitleBillboardTileBaseStride,
          kFableTitleBillboardTileFirstObject,
          kFableTitleBillboardTileLastObject)) {
    return object;
  }
  if (uint32_t object = map_base_to_object(
          base, kFableTitleBillboardTileAltFirstBase,
          kFableTitleBillboardTileAltLastBase,
          kFableTitleBillboardTileAltBaseStride,
          kFableTitleBillboardTileAltFirstObject,
          kFableTitleBillboardTileAltLastObject)) {
    return object;
  }
  return map_base_to_object(base, kFableTitleBillboardTileHeaderFirstBase,
                            kFableTitleBillboardTileHeaderLastBase,
                            kFableTitleBillboardTileHeaderBaseStride,
                            kFableTitleBillboardTileHeaderFirstObject,
                            kFableTitleBillboardTileHeaderLastObject);
}


bool IsFableTitleBillboardTileUploadAliasForBase(uint32_t address,
                                                 uint32_t base) {
  if (address == 0 || !IsFableTitleBillboardTileUploadBaseAddress(base)) {
    return false;
  }
  const uint32_t object = FableTitleBillboardTileObjectAddressForBase(base);
  const uint32_t header = FableTitleBillboardTileHeaderAddressForObject(object);
  return address == base || address == object || address == header;
}

bool IsFableTitleBillboardTileUploadBase(uint32_t base, uint32_t width,
                                         uint32_t height) {
  if (!IsFableTitleBillboardTileUploadBaseAddress(base)) {
    return false;
  }
  if (base == 0x13520000u) {
    return width == 64u && height == 64u;
  }
  if (base >= 0x13530000u && base <= 0x135B0000u &&
      ((width == 128u && height == 128u) ||
       (base == 0x13580000u && width == 64u && height == 64u))) {
    return true;
  }
  return (width == 512u && height == 512u) ||
         (width == 1024u && height == 1024u) ||
         (width == 1024u && height == 512u) ||
         (width == 256u && height == 256u) ||
         (width == 256u && height == 1024u) ||
         (width == 128u && height == 1024u);
}

bool IsFableTitleBillboardTileUploadShape(uint32_t base, uint32_t width,
                                          uint32_t height, uint32_t format,
                                          uint32_t byte_size) {
  if (!IsFableTitleBillboardTileUploadBaseAddress(base)) {
    return false;
  }
  return (byte_size == 131072u && width == 512u && height == 512u &&
          format == 18u) ||
         (byte_size == 65536u && width == 256u && height == 256u &&
          format == 20u) ||
         (byte_size == 131072u && width == 128u && height == 1024u &&
          format == 20u);
}

bool IsFableTitleGuiAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot) {
  return snapshot.kind == RendererTrackedResourceKind::kTexture &&
         (IsFableTitleGuiTextAtlasBaseAddress(snapshot.texture.base_address) ||
          IsFableTitleTextureProbeAddress(snapshot.guest_address) ||
          IsFableTitleTextureProbeAddress(snapshot.texture.object_address));
}

bool IsFableExactTinyTitleUploadSnapshot(
    const RendererTrackedResourceSnapshot& snapshot) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture) {
    return false;
  }

  const auto is_known_object = [](uint32_t address) {
    return address == 0x401317D0u || address == 0x40131810u ||
           address == 0x404DE490u || address == 0x404DE4D0u ||
           address == 0x404DE550u;
  };
  const auto is_exact_tiny_title_shape =
      [](uint32_t base, uint32_t width, uint32_t height, uint32_t format,
         uint32_t byte_size) {
        if (base == 0x1A0D7000u && width == 16u && height == 16u &&
            format == 6u) {
          return byte_size == 0u || byte_size == 4096u;
        }
        if (base == 0x1FAB5000u && width == 64u && height == 1u &&
            format == 29u) {
          return byte_size == 0u || byte_size == 512u;
        }
        if (base == 0x1FAB4000u && width == 256u && height == 1u &&
            format == 6u) {
          return byte_size == 0u || byte_size == 1024u;
        }
        return false;
      };
  const auto is_exact_tiny_title_storage =
      [](uint32_t base, uint32_t byte_size) {
        return (base == 0x1A0D7000u && byte_size == 4096u) ||
               (base == 0x1FAB5000u && byte_size == 512u) ||
               (base == 0x1FAB4000u && byte_size == 1024u);
      };
  const bool known_object =
      is_known_object(snapshot.guest_address) ||
      is_known_object(snapshot.parent_address) ||
      is_known_object(snapshot.texture.object_address) ||
      is_known_object(snapshot.texture.parent_address);

  const std::array<uint32_t, 6>& fetch =
      FableTextureFetchHasDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const FableTextureFetchInfo info = FableTextureFetchInfoFromDwords(fetch);
  const uint32_t fetch_base = NormalizeFableGpuAddress(info.base_address);
  const uint32_t snapshot_base =
      NormalizeFableGpuAddress(snapshot.texture.base_address);
  const bool exact_fetch_shape = is_exact_tiny_title_shape(
      fetch_base, info.width, info.height, info.format,
      snapshot.texture.byte_size);
  const bool exact_snapshot_shape =
      is_exact_tiny_title_storage(snapshot_base, snapshot.texture.byte_size);
  return exact_fetch_shape || (known_object && exact_snapshot_shape);
}

bool IsFableTitleBillboardAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture) {
    return false;
  }

  constexpr uint32_t kTitleBillboardAtlasObject = 0x4C132C70u;
  constexpr uint32_t kTitleBillboardAtlasAlias = 0x4C132CA4u;
  constexpr uint32_t kTitleBillboardAtlasBase = 0x16C40000u;
  const auto is_known_object = [](uint32_t address) {
    return address == kTitleBillboardAtlasObject ||
           address == kTitleBillboardAtlasAlias ||
           address == 0x4C132CD8u ||
           address == 0x4C133150u ||
           address == 0x4C133184u ||
           address == kTitleBillboardAtlasBase ||
           IsFableTitleBillboardTileUploadObjectAddress(address) ||
           IsFableTitleBillboardTileUploadHeaderAddress(address) ||
           IsFableTitleBillboardTileUploadBaseAddress(address);
  };

  const bool known_carrier =
      is_known_object(snapshot.guest_address) ||
      is_known_object(snapshot.parent_address) ||
      is_known_object(snapshot.texture.object_address) ||
      is_known_object(snapshot.texture.parent_address);
  const uint32_t snapshot_base =
      NormalizeFableGpuAddress(snapshot.texture.base_address);
  const bool known_base = snapshot_base == kTitleBillboardAtlasBase ||
                          IsFableTitleBillboardTileUploadBaseAddress(
                              snapshot_base);
  if (!known_carrier || !known_base) {
    return false;
  }

  const std::array<uint32_t, 6>& fetch =
      FableTextureFetchHasDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const FableTextureFetchInfo info = FableTextureFetchInfoFromDwords(fetch);
  const uint32_t fetch_base = NormalizeFableGpuAddress(info.base_address);
  const bool known_extent =
      fetch_base == kTitleBillboardAtlasBase &&
      info.width == 1024u && info.height == 512u && info.format == 20u;
  const bool known_tile_extent = IsFableTitleBillboardTileUploadShape(
      fetch_base, info.width, info.height, info.format,
      snapshot.texture.byte_size);
  return known_extent || known_tile_extent ||
         snapshot.texture.byte_size == 524288u;
}

bool IsFableTitleIndirectionTextureBase(uint32_t base_address) {
  switch (base_address) {
    case 0x13520000u:
    case 0x13530000u:
    case 0x13540000u:
    case 0x13560000u:
    case 0x13570000u:
    case 0x13580000u:
    case 0x13590000u:
    case kFableTitleBillboardCommandSourceBase:
    case kFableTitlePrivateIndirectionCommandBase:
    case kFableTitlePrivateIndirectionHeader0Base:
    case kFableTitlePrivateIndirectionHeader1Base:
    case kFableTitlePrivateIndirectionSourceBase:
    case 0x16C40000u:
    case 0x1FAA0000u:
    case 0x1A0D9000u:
    case 0x1A0EA000u:
      return true;
    default:
      return false;
  }
}

bool IsFableOneDimensionalTitleUploadedTextureBase(uint32_t base,
                                                   uint32_t width,
                                                   uint32_t height) {
  if (height != 1u) {
    return false;
  }
  switch (base) {
    case 0x1FAA0000u:
      return width == 512u;
    case 0x1FAB1000u:
    case 0x1FAB3000u:
    case 0x1FAB5000u:
      return width == 64u;
    case 0x1FAB0000u:
    case 0x1FAB2000u:
    case 0x1FAB4000u:
      return width == 256u;
    default:
      return false;
  }
}

bool IsFableTitleUploadedTextureIdentityBase(uint32_t base, uint32_t width,
                                             uint32_t height) {
  if (base == 0x16C40000u && width == 1024u && height == 512u) {
    return true;
  }
  if (IsFableTitleBillboardTileUploadBase(base, width, height)) {
    return true;
  }
  if (base == kFableTitleBillboardCommandSourceBase ||
      base == kFableTitlePrivateIndirectionCommandBase ||
      base == kFableTitlePrivateIndirectionHeader0Base ||
      base == kFableTitlePrivateIndirectionHeader1Base ||
      base == kFableTitlePrivateIndirectionSourceBase) {
    return true;
  }
  if (base == 0x1A0D9000u && width == 512u && height == 256u) {
    return true;
  }
  if (base == 0x1A0EA000u && width == 512u && height == 512u) {
    return true;
  }
  // The scene-scaler upload is a display-sized scene copy: its extent tracks
  // the live guest dims (qword_834A5FE8, stock 1280x720).
  if (base == kFableTitleSceneScalerUploadBase &&
      width == FableGuestDisplayWidth() &&
      height == FableGuestDisplayHeight()) {
    return true;
  }
  if (base == 0x1A07E000u) {
    return true;
  }
  if (base == 0x1A0D7000u && width == 16u && height == 16u) {
    return true;
  }
  if (base == 0x1FC40000u && width == 1u && height == 1u) {
    return true;
  }
  if (IsFableOneDimensionalTitleUploadedTextureBase(base, width, height)) {
    return true;
  }
  if (base == 0x13520000u && width == 64u && height == 64u) {
    return true;
  }
  if (base == 0x13580000u &&
      ((width == 128u && height == 128u) ||
       (width == 64u && height == 64u))) {
    return true;
  }
  if (base >= 0x13530000u && base <= 0x135B0000u && width == 128u &&
      height == 128u) {
    return true;
  }
  return false;
}

bool IsFableTinyTitleUploadedTextureIdentityBase(uint32_t base, uint32_t width,
                                                 uint32_t height) {
  return (base == 0x1A0D7000u && width == 16u && height == 16u) ||
         (base == 0x1FC40000u && width == 1u && height == 1u) ||
         IsFableOneDimensionalTitleUploadedTextureBase(base, width, height);
}

bool IsFableOneDimensionalTitleUploadedTextureIdentityBase(uint32_t base,
                                                           uint32_t width,
                                                           uint32_t height) {
  return IsFableOneDimensionalTitleUploadedTextureBase(base, width, height);
}

bool IsFableTitleUploadedTextureIdentity(
    const FableTextureIdentity* identity) {
  return identity &&
         IsFableTitleUploadedTextureIdentityBase(identity->base_address,
                                                identity->width,
                                                identity->height);
}

bool IsFableTinyTitleUploadedTextureIdentity(
    uint32_t guest, const FableTextureIdentity* identity) {
  if (guest == 0x401314D0u || guest == 0x401317D0u ||
      guest == 0x40131810u || guest == 0x40131850u ||
      guest == 0x40131890u || guest == 0x401318D0u ||
      guest == 0x40131910u) {
    return true;
  }
  if (!identity) {
    return false;
  }
  return IsFableTinyTitleUploadedTextureIdentityBase(identity->base_address,
                                                     identity->width,
                                                     identity->height);
}


uint32_t ExactFableTitleGuiTextAtlasUploadSize(uint32_t base, uint32_t width,
                                               uint32_t height,
                                               uint32_t format) {
  if (base == 0x1A0D9000u && width == 512u && height == 256u &&
      format == 59u) {
    return 65536u;
  }
  if (base == 0x1A0EA000u && width == 512u && height == 512u &&
      format == 59u) {
    return 131072u;
  }
  return 0;
}

bool IsFableExactTitleGuiTextAtlasIdentity(
    const FableTextureIdentity* identity) {
  return identity &&
         ExactFableTitleGuiTextAtlasUploadSize(identity->base_address,
                                              identity->width,
                                              identity->height,
                                              identity->format) != 0;
}

bool FableTitleCommandSlotHasExactGuiTextAtlasIdentity(
    const FableTitleGuiAtlasSlotIdentityFacts& facts) {
  if (IsFableExactTitleGuiTextAtlasIdentity(facts.identity)) {
    return true;
  }
  if (facts.fetch_valid &&
      ExactFableTitleGuiTextAtlasUploadSize(
          facts.fetch_base_address, facts.fetch_width, facts.fetch_height,
          facts.fetch_format) != 0) {
    return true;
  }

  return facts.bound_image_resource &&
         ExactFableTitleGuiTextAtlasUploadSize(
             facts.bound_base_address, facts.bound_width, facts.bound_height,
             facts.bound_format) != 0;
}

bool FableTitleGuiTextAtlasCommand(
    const FableTitleGuiTextAtlasCommandFacts& facts) {
  if (!facts.slot_in_range || !facts.scene_object_source ||
      facts.tiny_title_upload_identity) {
    return false;
  }

  if (facts.repeated_mesh_declaration && facts.primitive_type == 13u &&
      facts.title_scene_target && facts.exact_gui_text_atlas_identity) {
    return true;
  }

  if (facts.slot != facts.private_rect_source_slot ||
      !facts.private_rect_declaration || facts.input_slot0_stride != 20u ||
      !facts.gui_text_atlas_object) {
    return false;
  }

  if (facts.vertex_hash == kFableTitleWaterTessellatorMistedZPassVertexHash) {
    return true;
  }
  if (facts.pixel_hash == kFableTitleTextureMorphPixelHash ||
      facts.pixel_hash == kFableTitleGuiText3DPixelShaderHash) {
    return true;
  }
  return facts.pixel_shader >= 0x4C000000u &&
         facts.title_scene_vertex_shader_command;
}

uint32_t FableTitleGuiAtlasCandidateExactUploadSize(
    const FableTitleGuiAtlasCandidateFacts& facts) {
  return ExactFableTitleGuiTextAtlasUploadSize(
      facts.base_address, facts.width, facts.height, facts.format);
}

bool FableTitleGuiAtlasCandidateCanBeExactCommandUpload(
    const FableTitleGuiAtlasCandidateFacts& facts) {
  return facts.image_ready && !facts.texture_depth_target &&
         !facts.command_binds_candidate_as_target &&
         facts.command_fetch_valid &&
         facts.guest_address == facts.command_guest &&
         facts.gui_text_atlas_object;
}

bool FableTitleGuiAtlasCandidateIsExactCommandUpload(
    const FableTitleGuiAtlasCandidateFacts& facts) {
  if (!FableTitleGuiAtlasCandidateCanBeExactCommandUpload(facts)) {
    return false;
  }
  const uint32_t exact_upload_size =
      FableTitleGuiAtlasCandidateExactUploadSize(facts);
  return exact_upload_size != 0 &&
         facts.base_address == facts.command_fetch_base_address &&
         facts.width == facts.command_fetch_width &&
         facts.height == facts.command_fetch_height &&
         facts.format == facts.command_fetch_format &&
         facts.texture_upload_size == exact_upload_size &&
         facts.texture_upload_hash != 0 && facts.meaningful_upload;
}

bool FableTitleGuiAtlasCandidateMatchesIdentityOrBoundResource(
    const FableTitleGuiAtlasCandidateFacts& facts) {
  if (!facts.title_gui_atlas_upload ||
      facts.command_binds_candidate_as_target) {
    return false;
  }

  if (IsFableTitleUploadedTextureIdentity(facts.identity)) {
    if (facts.identity->width != 0 && facts.width != facts.identity->width) {
      return false;
    }
    if (facts.identity->height != 0 &&
        facts.height != facts.identity->height) {
      return false;
    }
    if (facts.identity->base_address != 0 &&
        facts.base_address != facts.identity->base_address) {
      return false;
    }
    return facts.guest_address == facts.identity->object_texture ||
           facts.guest_address == facts.identity->parent_texture ||
           facts.parent_address == facts.identity->object_texture ||
           facts.parent_address == facts.identity->parent_texture ||
           facts.base_address == facts.identity->base_address;
  }

  if (!facts.bound_texture_ready) {
    return false;
  }
  return facts.guest_address == facts.bound_guest_address ||
         facts.guest_address == facts.bound_parent_address ||
         facts.parent_address == facts.bound_guest_address ||
         facts.parent_address == facts.bound_parent_address ||
         facts.base_address == facts.bound_base_address;
}

bool FableTitleResourceUploadDefinesTextureIdentity(
    const FableTitleResourceFacts& facts) {
  if (!facts.meaningful_upload || facts.blank_uploaded_sample_proxy) {
    return false;
  }

  return !IsFableMutablePrivateTextureHeaderAddress(facts.guest_address) &&
         !IsFableMutablePrivateTextureHeaderAddress(facts.parent_address) &&
         !IsFableMutablePrivateTextureHeaderAddress(facts.base_address);
}

bool FableTitleResourceHasExactUploadIdentity(
    const FableTitleResourceFacts& facts) {
  if (!facts.image_ready) {
    return false;
  }
  // Derived identity, no address windows: the identity registry only holds
  // entries created from observed guest texture bindings, so a registered
  // identity plus meaningfully captured upload content IS an uploaded
  // texture identity. The strided base windows below match the streamed
  // pool's physical layout only by coincidence (it varies per run) — panels
  // whose atlases landed outside them stayed undeliverable at crossfades.
  // Render/depth targets are excluded so GPU-composed pages keep waiting
  // for their producer (see the 2026-07-10 fetch-truth regressions).
  if (facts.identity != nullptr && facts.meaningful_upload &&
      !facts.blank_uploaded_sample_proxy && !facts.texture_render_target &&
      !facts.texture_depth_target) {
    return true;
  }
  return IsFableTitleUploadedTextureIdentityBase(
             facts.base_address, facts.width, facts.height) ||
         IsFableTitleUploadedTextureIdentity(facts.identity) ||
         IsFableTitleGuiTextAtlasObjectAddress(facts.guest_address) ||
         IsFableTitleGuiTextAtlasObjectAddress(facts.parent_address) ||
         IsFableTitleGuiTextAtlasObjectAddress(facts.base_address);
}

bool FableTitleExactUploadedResourceHasAuthoredPayload(
    const FableTitleResourceFacts& facts) {
  if (!FableTitleResourceHasExactUploadIdentity(facts)) {
    return false;
  }

  if (facts.base_address == 0x1FC40000u && facts.width == 1u &&
      facts.height == 1u && facts.format == 6u && facts.meaningful_upload) {
    return true;
  }
  if (facts.blank_uploaded_sample_proxy) {
    return false;
  }
  if (facts.meaningful_upload) {
    return true;
  }

  if (facts.base_address == 0x1FAA0000u && facts.width == 512u &&
      facts.height == 1u && facts.format == 36u) {
    return facts.sampleable_content || facts.gpu_produced_content ||
           facts.visible_content_serial != 0;
  }

  return false;
}

bool FableTitleResourceHasTextureSampleContentForExactProducer(
    const FableTitleResourceFacts& facts) {
  if (!facts.image_ready) {
    return false;
  }
  if (FableTitleExactUploadedResourceHasAuthoredPayload(facts)) {
    return true;
  }
  if (facts.meaningful_upload && !facts.blank_uploaded_sample_proxy) {
    return true;
  }
  if (facts.draw_content_serial != 0) {
    return true;
  }
  return facts.texture_depth_target && facts.clear_content_serial != 0;
}

bool FableTitleBillboardTileHasAuthoritativeContent(
    const FableTitleResourceFacts& facts) {
  if (!facts.image_ready || facts.texture_depth_target) {
    return false;
  }

  if (!IsFableTitleBillboardTileUploadBase(facts.base_address, facts.width,
                                           facts.height)) {
    return false;
  }

  const bool title_tile_address =
      IsFableTitleTextureIndirectionChainAddress(facts.guest_address) ||
      IsFableTitleTextureIndirectionChainAddress(facts.parent_address) ||
      IsFableTitleBillboardTileUploadAliasForBase(facts.guest_address,
                                                  facts.base_address) ||
      IsFableTitleBillboardTileUploadAliasForBase(facts.parent_address,
                                                  facts.base_address);
  return title_tile_address &&
         facts.texture_sample_content_for_exact_producer;
}

bool FableTitleBillboardAtlasResource(const FableTitleResourceFacts& facts) {
  if (!facts.image_ready || facts.texture_render_target ||
      facts.texture_depth_target) {
    return false;
  }

  const auto is_known_carrier = [](uint32_t address) {
    return address == 0x4C132C70u || address == 0x4C132CA4u ||
           address == 0x4C132CD8u || address == 0x4C133150u ||
           address == 0x4C133184u ||
           address == kFableCanonicalTitleBillboardBase;
  };
  const bool title_atlas_address =
      IsFableTitleTextureIndirectionChainAddress(facts.guest_address) ||
      IsFableTitleBillboardTileUploadBaseAddress(facts.guest_address) ||
      is_known_carrier(facts.guest_address) ||
      is_known_carrier(facts.parent_address);
  const bool canonical_atlas_extent =
      facts.width == 1024u && facts.height == 512u;
  const bool tile_atlas_extent = IsFableTitleBillboardTileUploadBase(
      facts.base_address, facts.width, facts.height);
  const bool has_authoritative_upload =
      facts.texture_upload_has_source && facts.upload_generation != 0 &&
      facts.texture_upload_hash != 0 &&
      facts.texture_upload_size >= 524288u &&
      facts.debug_first_nonzero_offset != UINT32_MAX;
  const bool has_authoritative_tile_upload =
      facts.texture_upload_has_source && facts.upload_generation != 0 &&
      facts.texture_upload_hash != 0 && tile_atlas_extent &&
      facts.texture_upload_size >= 65536u;
  const bool has_recorded_upload =
      facts.upload_generation != 0 && facts.texture_upload_hash != 0 &&
      ((tile_atlas_extent && facts.texture_upload_size >= 65536u) ||
       (canonical_atlas_extent && facts.texture_upload_size >= 524288u));
  return title_atlas_address && (canonical_atlas_extent || tile_atlas_extent) &&
         (has_authoritative_upload || has_authoritative_tile_upload ||
          has_recorded_upload ||
          facts.debug_first_nonzero_offset != UINT32_MAX);
}

bool FableTitleBillboardAtlasHasAuthoritativeUpload(
    const FableTitleResourceFacts& facts) {
  if (FableTitleBillboardTileHasAuthoritativeContent(facts)) {
    return true;
  }

  const bool tile_atlas_extent = IsFableTitleBillboardTileUploadBase(
      facts.base_address, facts.width, facts.height);
  return FableTitleBillboardAtlasResource(facts) && facts.meaningful_upload &&
         (tile_atlas_extent || !facts.blank_uploaded_sample_proxy);
}

bool FableTitleBillboardTileUploadAddressMatchesResourceBase(
    uint32_t address,
    uint32_t base,
    uint32_t guest_address,
    uint32_t parent_address) {
  if (address == 0 || !IsFableTitleBillboardTileUploadBaseAddress(base)) {
    return false;
  }
  return address == guest_address || address == parent_address ||
         IsFableTitleBillboardTileUploadAliasForBase(address, base);
}

bool FableTitleBillboardTileUploadIdentity(
    const FableTitleBillboardTileUploadIdentityFacts& facts) {
  if (!facts.command_fetch_valid ||
      !IsFableTitleBillboardTileUploadBase(facts.command_fetch_base_address,
                                           facts.command_fetch_width,
                                           facts.command_fetch_height)) {
    return false;
  }

  const bool command_names_title_tile =
      IsFableTitleTextureIndirectionChainAddress(facts.command_guest) ||
      FableTitleBillboardTileUploadAddressMatchesResourceBase(
          facts.command_guest, facts.command_fetch_base_address,
          facts.guest_address, facts.parent_address);
  if (!command_names_title_tile) {
    return false;
  }

  const bool candidate_names_title_tile =
      facts.base_address == facts.command_fetch_base_address ||
      FableTitleBillboardTileUploadAddressMatchesResourceBase(
          facts.guest_address, facts.command_fetch_base_address,
          facts.guest_address, facts.parent_address) ||
      FableTitleBillboardTileUploadAddressMatchesResourceBase(
          facts.parent_address, facts.command_fetch_base_address,
          facts.guest_address, facts.parent_address);
  return candidate_names_title_tile &&
         facts.base_address == facts.command_fetch_base_address &&
         facts.width == facts.command_fetch_width &&
         facts.height == facts.command_fetch_height &&
         facts.authoritative_upload;
}

bool FableTitleIndirectionHasAuthoritativeUpload(
    const FableTitleResourceFacts& facts) {
  if (!facts.image_ready || facts.texture_render_target ||
      facts.texture_depth_target || !facts.meaningful_upload ||
      facts.texture_upload_hash == 0) {
    return false;
  }
  return IsFableTitleIndirectionTextureBase(facts.base_address) ||
         IsFableTitleBillboardTileUploadBaseAddress(facts.base_address);
}

bool FableTitleGuiAtlasResourceHasUpload(
    const FableTitleResourceFacts& facts) {
  if (!facts.image_ready || facts.texture_depth_target ||
      !facts.meaningful_upload || facts.texture_upload_hash == 0) {
    return false;
  }

  const uint32_t exact_upload_size = ExactFableTitleGuiTextAtlasUploadSize(
      facts.base_address, facts.width, facts.height, facts.format);
  return exact_upload_size != 0 &&
         facts.texture_upload_size == exact_upload_size &&
         (IsFableTitleGuiTextAtlasObjectAddress(facts.guest_address) ||
          IsFableTitleGuiTextAtlasObjectAddress(facts.parent_address) ||
          IsFableTitleGuiTextAtlasObjectAddress(facts.base_address));
}


bool FableTinyTitleCachedTextureHasUploadIdentity(
    uint32_t guest, const FableTextureIdentity* guest_identity,
    const FableTitleResourceFacts& facts) {
  if (!facts.image_ready || facts.texture_render_target ||
      facts.texture_depth_target || facts.blank_uploaded_sample_proxy) {
    return false;
  }

  const bool exact_tiny_identity =
      IsFableTinyTitleUploadedTextureIdentity(guest, guest_identity) ||
      IsFableTinyTitleUploadedTextureIdentity(facts.guest_address,
                                             facts.identity) ||
      IsFableTinyTitleUploadedTextureAddress(guest) ||
      IsFableTinyTitleUploadedTextureAddress(facts.guest_address) ||
      IsFableTinyTitleUploadedTextureAddress(facts.parent_address);
  const bool exact_tiny_shape = IsFableTinyTitleUploadedTextureIdentityBase(
      facts.base_address, facts.width, facts.height);
  return (exact_tiny_identity || exact_tiny_shape) && facts.meaningful_upload;
}

bool FableTinyTitleResourceMatchesFetchInfo(
    const FableTinyTitleResourceFetchMatchFacts& facts) {
  if (!facts.fetch_valid || !facts.resource_image_ready) {
    return false;
  }
  if (facts.resource_base_address != facts.fetch_base_address ||
      facts.resource_width != facts.fetch_width ||
      facts.resource_height != facts.fetch_height) {
    return false;
  }
  if (IsFableOneDimensionalTitleUploadedTextureIdentityBase(
          facts.fetch_base_address, facts.fetch_width, facts.fetch_height) &&
      IsFableOneDimensionalTitleUploadedTextureIdentityBase(
          facts.resource_base_address, facts.resource_width,
          facts.resource_height)) {
    return true;
  }
  return facts.fetch_format == 0u ||
         facts.resource_guest_format == facts.fetch_format ||
         facts.resource_render_format == facts.fetch_format;
}

}  // namespace refii::renderer
