#pragma once

#include <cstdint>
#include <vector>

#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"

namespace refii::renderer {

void RegisterBinkVideoPlaneBindingForPlume(uint32_t texture,
                                           uint32_t plane_memory,
                                           uint32_t plane_bytes,
                                           uint32_t row_pitch,
                                           uint32_t width_bytes);

void RequeueBinkPlaneSnapshotsForPlume(
    std::vector<FableGuestResourceSnapshot>&& snapshots);

}  // namespace refii::renderer
