#pragma once

#include "runtime/plume/direct/fable_resource_snapshots.h"

#include <cstdint>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
struct PlumeTrackedResource;

const char* ConvertGuestDeclUsageName(uint8_t usage);
uint32_t ConvertGuestDeclLocation(uint8_t usage, uint8_t usage_index);
plume::RenderFormat ConvertGuestDeclType(uint32_t type);
bool GuestDeclTypeNeedsSwappedTexcoords(uint32_t type);
bool DecodeVertexDeclaration(PlumeTrackedResource& tracked,
                             const RendererTrackedResourceSnapshot& snapshot);
#endif

}  // namespace refii::renderer
