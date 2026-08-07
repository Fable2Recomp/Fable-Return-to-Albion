#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"

#include <algorithm>

namespace refii::renderer {

DirectImageShape DirectImageShapeForTrackedResource(
    const PlumeTrackedResource& tracked) {
  return DirectImageShape{
      .dimension = tracked.texture_dimension,
      .view_dimension = tracked.texture_view_dimension,
      .format = tracked.texture_format,
      .width = PlumeGuestTextureWidth(tracked),
      .height = PlumeGuestTextureHeight(tracked),
      .depth = PlumeGuestTextureDepth(tracked),
      .mip_levels = tracked.texture_mip_levels,
      .render_target = tracked.texture_render_target,
      .depth_target = tracked.texture_depth_target,
  };
}

// IS THE LIVE IMAGE THE WRONG SIZE FOR WHAT WE NOW INTEND? (2026-08-04)
//
// The shape comparison above is metadata-vs-metadata: both sides run the same
// decode, so both carry the same promotion and they always agree. That is why
// a promotion landing AFTER an image was materialized could never retire it —
// RefreshPlumeGuestImageObjectsFromTrackedFields relabels every extent record
// to the promoted value while deliberately preserving the existing VkImage, so
// the records said 1680x1080 and the image was still 1120x720. The viewport
// scaler then took its numerator from the record and its denominator from
// promoted_from_guest_* and scaled draws 1.5x onto an image that never grew.
// Measured run 596 frame 13696: sunglare, sundisc, sunbeams.
//
// This is deliberately a SEPARATE predicate rather than a widening of the
// shape comparison. Widening it was tried first and caused periodic retires
// (run 597, every ~3 frames): the decoded probe sets render_target from the
// snapshot KIND, so for a kind=texture resource it reads false while the
// tracked resource has it true, and shapes then differed on a field that has
// nothing to do with extent. Keep the general comparison exactly as it was and
// ask only the question the bug poses — is the image the size we now want?
//
// Converges by construction: after the recreate the image is built at the
// intended extent, so this returns false on the next sync.
bool DirectTrackedImageExtentStale(const PlumeTrackedResource& tracked,
                                   const DirectImageShape& intended) {
  if (tracked.guest_texture.texture == nullptr ||
      tracked.guest_texture.image_width == 0 ||
      tracked.guest_texture.image_height == 0 || intended.width == 0 ||
      intended.height == 0) {
    return false;
  }
  return tracked.guest_texture.image_width != intended.width ||
         tracked.guest_texture.image_height != intended.height;
}

bool DirectImageShapeEquals(const DirectImageShape& lhs,
                            const DirectImageShape& rhs) {
  return lhs.dimension == rhs.dimension &&
         lhs.view_dimension == rhs.view_dimension && lhs.format == rhs.format &&
         lhs.width == rhs.width && lhs.height == rhs.height &&
         lhs.depth == rhs.depth && lhs.mip_levels == rhs.mip_levels &&
         lhs.render_target == rhs.render_target &&
         lhs.depth_target == rhs.depth_target;
}

bool DirectImageShapeUsable(const DirectImageShape& shape) {
  return shape.dimension != plume::RenderTextureDimension::UNKNOWN &&
         shape.view_dimension != plume::RenderTextureViewDimension::UNKNOWN &&
         shape.format != plume::RenderFormat::UNKNOWN && shape.width != 0 &&
         shape.height != 0 && shape.mip_levels != 0;
}

bool DirectTrackedResourceReady(const PlumeTrackedResource& resource) {
  if (IsBufferResource(resource.kind)) {
    return PlumeGuestBufferReady(resource);
  }
  if (IsShaderResource(resource.kind)) {
    return PlumeGuestShaderReady(resource);
  }
  if (IsDeclarationResource(resource.kind)) {
    return PlumeGuestDeclarationReady(resource);
  }
  if (IsImageResource(resource.kind)) {
    return PlumeGuestImageReady(resource) &&
           DirectImageShapeUsable(DirectImageShapeForTrackedResource(resource));
  }
  return false;
}

bool DirectTrackedResourceReady(const PlumeTrackedResource* resource) {
  return resource && DirectTrackedResourceReady(*resource);
}

bool DirectTrackedImageResourceReady(const PlumeTrackedResource* resource) {
  return resource && IsImageResource(resource->kind) &&
         DirectTrackedResourceReady(resource);
}

uint32_t DirectTrackedBufferViewBytes(const PlumeTrackedResource& buffer,
                                      uint32_t offset) {
  const uint32_t byte_size = PlumeGuestBufferByteSize(buffer);
  if (offset >= byte_size) {
    return 0;
  }
  return byte_size - offset;
}

bool DirectTrackedVertexBufferCoversDraw(const PlumeTrackedResource& buffer,
                                         uint32_t offset,
                                         uint32_t required_view_bytes) {
  if (!PlumeGuestBufferReady(buffer)) {
    return false;
  }
  return required_view_bytes == 0 ||
         DirectTrackedBufferViewBytes(buffer, offset) >= required_view_bytes;
}

bool FableTrackedResourceHasMeaningfulTextureUpload(
    const PlumeTrackedResource& resource) {
  return resource.texture_upload_has_source && resource.upload_generation != 0 &&
         resource.texture_upload_size != 0;
}

bool FableTrackedResourceHasGpuProducedContent(
    const PlumeTrackedResource& resource) {
  if (!IsImageResource(resource.kind) || !PlumeGuestImageReady(resource)) {
    return false;
  }
  if (FableTrackedResourceHasMeaningfulTextureUpload(resource)) {
    return true;
  }
  return resource.draw_content_serial != 0 || resource.clear_content_serial != 0;
}

bool FableTrackedResourceIsUnbackedSampleProxy(
    const PlumeTrackedResource& resource) {
  return IsImageResource(resource.kind) && PlumeGuestImageReady(resource) &&
         !resource.texture_render_target && !resource.texture_depth_target &&
         !resource.texture_upload_has_source &&
         resource.texture_upload_size == 0 &&
         resource.draw_content_serial == 0 &&
         resource.clear_content_serial == 0;
}

bool FableTrackedResourceIsBlankUploadedSampleProxy(
    const PlumeTrackedResource& resource) {
  return IsImageResource(resource.kind) && PlumeGuestImageReady(resource) &&
         !resource.texture_render_target && !resource.texture_depth_target &&
         FableTrackedResourceHasMeaningfulTextureUpload(resource) &&
         resource.debug_first_nonzero_offset == UINT32_MAX;
}

bool FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(
    const PlumeTrackedResource& resource) {
  if (!IsImageResource(resource.kind) || !PlumeGuestImageReady(resource)) {
    return false;
  }
  if (FableTrackedResourceHasMeaningfulTextureUpload(resource) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(resource)) {
    return true;
  }
  return resource.draw_content_serial != 0 || resource.clear_content_serial != 0;
}

uint64_t FableTrackedResourceCopyContentSerial(
    const PlumeTrackedResource& resource) {
  return std::max({resource.content_serial, resource.draw_content_serial,
                   resource.clear_content_serial, resource.upload_generation,
                   resource.serial});
}

uint64_t FableTrackedResourceVisibleContentSerial(
    const PlumeTrackedResource& resource) {
  return std::max({resource.content_serial, resource.draw_content_serial,
                   resource.clear_content_serial, resource.upload_generation});
}

bool FableTrackedResourceHasSampleableContent(
    const PlumeTrackedResource& resource) {
  if (!IsImageResource(resource.kind) || !PlumeGuestImageReady(resource)) {
    return false;
  }
  if (FableTrackedResourceHasMeaningfulTextureUpload(resource)) {
    return true;
  }
  return resource.draw_content_serial != 0;
}

uint64_t FableZeroUploadHashForSize(uint64_t size) {
  constexpr uint64_t kFnvOffsetBasis = 14695981039346656037ull;
  constexpr uint64_t kFnvPrime = 1099511628211ull;

  uint64_t multiplier = kFnvPrime;
  uint64_t repeated_multiplier = 1u;
  while (size != 0) {
    if ((size & 1u) != 0) {
      repeated_multiplier *= multiplier;
    }
    multiplier *= multiplier;
    size >>= 1u;
  }

  return kFnvOffsetBasis * repeated_multiplier;
}

}  // namespace refii::renderer
