#pragma once

#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FableDirectPresentSnapshot {
  uint64_t serial = 0;
  uint32_t display_texture_base = 0;
  uint32_t display_width = 0;
  uint32_t display_height = 0;
  uint32_t display_format = 0;
  uint32_t display_surface_object = 0;
  uint32_t display_source = 0;
  uint32_t display_register = 0;
  uint32_t display_register_value = 0;
  std::array<uint32_t, 6> display_fetch_dwords = {};
  uint32_t surface_copy_alias_count = 0;
  std::array<RendererSurfaceCopyAliasSnapshot, kRendererSurfaceCopyAliasSlots>
      surface_copy_aliases = {};
};

void FableDirectSetDisplayPresent(uint32_t texture_base, uint32_t width,
                                  uint32_t height, uint32_t format,
                                  uint32_t source, uint32_t register_index,
                                  uint32_t register_value,
                                  const std::array<uint32_t, 6>* fetch_dwords,
                                  uint32_t surface_object);
void FableDirectTrackSurfaceCopyAlias(
    const RendererSurfaceCopyAliasSnapshot& alias);
bool FableDirectFindSurfaceCopyAliasByDestination(
    uint32_t destination_surface, RendererSurfaceCopyAliasSnapshot* alias);
void FableDirectForgetPresentResource(uint32_t resource_object,
                                      uint32_t texture_base = 0);
void FableDirectClearDisplayPresent();
FableDirectPresentSnapshot GetFableDirectPresentSnapshot();

}  // namespace refii::renderer
