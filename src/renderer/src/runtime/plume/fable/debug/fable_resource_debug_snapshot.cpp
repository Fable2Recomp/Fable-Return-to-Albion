#include "runtime/refii_renderer/plume/fable/debug/fable_resource_debug_snapshot.h"

#if REFII_RENDERER_HAS_PLUME
#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <algorithm>
#include <cstring>
#include <string_view>

namespace refii::renderer {
namespace {

void CopyDirectResourceAnnotation(
    std::array<char, kFableDirectResourceAnnotationCapacity>& output,
    std::string_view annotation) {
  output.fill('\0');
  if (annotation.empty()) {
    return;
  }
  const size_t copy_size =
      std::min(annotation.size(),
               static_cast<size_t>(kFableDirectResourceAnnotationCapacity - 1));
  std::memcpy(output.data(), annotation.data(), copy_size);
}

FableDirectResourceDebugEntry BuildDirectResourceDebugEntry(
    const PlumeTrackedResource& tracked) {
  FableDirectResourceDebugEntry entry;
  entry.valid = true;
  entry.ready = DirectTrackedResourceReady(tracked);
  entry.renderdoc_annotated = !tracked.debug_summary.empty();
  entry.kind = tracked.kind;
  entry.guest_address = tracked.guest_address;
  entry.parent_address = tracked.parent_address;
  entry.serial = tracked.serial;
  entry.content_serial = tracked.content_serial;
  entry.upload_generation = tracked.upload_generation;
  if (IsBufferResource(tracked.kind)) {
    entry.base_or_gpu_address = PlumeGuestBufferGpuAddress(tracked);
    entry.byte_size = PlumeGuestBufferByteSize(tracked);
    entry.format_or_stride = PlumeGuestBufferStrideOrFormat(tracked);
  } else if (IsImageResource(tracked.kind)) {
    entry.base_or_gpu_address = PlumeGuestTextureBaseAddress(tracked);
    entry.byte_size = tracked.guest_texture.byte_size != 0
                          ? tracked.guest_texture.byte_size
                          : tracked.size;
    entry.width = PlumeGuestTextureWidth(tracked);
    entry.height = PlumeGuestTextureHeight(tracked);
    entry.depth = PlumeGuestTextureDepth(tracked);
    entry.format_or_stride = PlumeGuestTextureGuestFormat(tracked);
  } else if (IsShaderResource(tracked.kind)) {
    entry.base_or_gpu_address = tracked.guest_shader.bytecode_address;
    entry.byte_size = tracked.guest_shader.bytecode_size;
    entry.content_hash = tracked.guest_shader.bytecode_hash;
  } else if (IsDeclarationResource(tracked.kind)) {
    entry.base_or_gpu_address =
        tracked.guest_vertex_declaration.elements_address;
    entry.byte_size = tracked.guest_vertex_declaration.elements_size;
    entry.format_or_stride =
        tracked.guest_vertex_declaration.input_element_count;
    entry.content_hash = tracked.guest_vertex_declaration.elements_hash;
  }
  CopyDirectResourceAnnotation(entry.annotation, tracked.debug_summary);
  return entry;
}

}  // namespace

FableDirectResourceDebugSnapshot BuildFableDirectResourceDebugSnapshot(
    std::vector<const PlumeTrackedResource*> resources) {
  FableDirectResourceDebugSnapshot snapshot;
  std::sort(resources.begin(), resources.end(),
            [](const PlumeTrackedResource* lhs,
               const PlumeTrackedResource* rhs) {
              const uint64_t lhs_sort =
                  std::max(lhs->content_serial, lhs->serial);
              const uint64_t rhs_sort =
                  std::max(rhs->content_serial, rhs->serial);
              if (lhs_sort != rhs_sort) {
                return lhs_sort > rhs_sort;
              }
              return lhs->guest_address < rhs->guest_address;
            });
  for (const PlumeTrackedResource* tracked : resources) {
    if (snapshot.count >= kFableDirectResourceDebugCapacity) {
      ++snapshot.omitted;
      continue;
    }
    snapshot.entries[snapshot.count++] =
        BuildDirectResourceDebugEntry(*tracked);
  }
  return snapshot;
}

}  // namespace refii::renderer
#endif
