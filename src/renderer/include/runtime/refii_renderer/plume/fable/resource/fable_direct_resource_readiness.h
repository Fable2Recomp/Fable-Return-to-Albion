#pragma once

#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <cstdint>

namespace refii::renderer {

struct DirectImageShape {
  plume::RenderTextureDimension dimension =
      plume::RenderTextureDimension::UNKNOWN;
  plume::RenderTextureViewDimension view_dimension =
      plume::RenderTextureViewDimension::UNKNOWN;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t depth = 0;
  uint32_t mip_levels = 0;
  bool render_target = false;
  bool depth_target = false;
};

DirectImageShape DirectImageShapeForTrackedResource(
    const PlumeTrackedResource& tracked);

bool DirectImageShapeEquals(const DirectImageShape& lhs,
                            const DirectImageShape& rhs);

// True when a materialized image's real extent no longer matches the extent we
// now intend to give it — the promotion-after-materialization case. See the
// comment on the definition; this is deliberately narrower than the shape
// comparison above.
bool DirectTrackedImageExtentStale(const PlumeTrackedResource& tracked,
                                   const DirectImageShape& intended);

bool DirectImageShapeUsable(const DirectImageShape& shape);

bool DirectTrackedResourceReady(const PlumeTrackedResource& resource);
bool DirectTrackedResourceReady(const PlumeTrackedResource* resource);

bool DirectTrackedImageResourceReady(const PlumeTrackedResource* resource);

uint32_t DirectTrackedBufferViewBytes(const PlumeTrackedResource& buffer,
                                      uint32_t offset);

bool DirectTrackedVertexBufferCoversDraw(const PlumeTrackedResource& buffer,
                                         uint32_t offset,
                                         uint32_t required_view_bytes);

bool FableTrackedResourceHasMeaningfulTextureUpload(
    const PlumeTrackedResource& resource);

bool FableTrackedResourceHasGpuProducedContent(
    const PlumeTrackedResource& resource);

bool FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(
    const PlumeTrackedResource& resource);

bool FableTrackedResourceIsUnbackedSampleProxy(
    const PlumeTrackedResource& resource);

bool FableTrackedResourceIsBlankUploadedSampleProxy(
    const PlumeTrackedResource& resource);

uint64_t FableTrackedResourceCopyContentSerial(
    const PlumeTrackedResource& resource);

uint64_t FableTrackedResourceVisibleContentSerial(
    const PlumeTrackedResource& resource);

bool FableTrackedResourceHasSampleableContent(
    const PlumeTrackedResource& resource);

uint64_t FableZeroUploadHashForSize(uint64_t size);

}  // namespace refii::renderer
