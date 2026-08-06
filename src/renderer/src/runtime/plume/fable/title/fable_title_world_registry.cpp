#include "runtime/refii_renderer/plume/fable/title/fable_title_world_registry.h"

#include <algorithm>
#include <limits>

namespace refii::renderer {

namespace {

constexpr uint32_t kWorldRenderableRegistryOffset = 3412u;
constexpr uint32_t kRegistryBeginOffset = 4u;
constexpr uint32_t kRegistryEndOffset = 8u;
constexpr uint32_t kRegistryEntryStride = 24u;
constexpr uint32_t kRegistryEntryObjectsBeginOffset = 12u;
constexpr uint32_t kRegistryEntryObjectsEndOffset = 16u;
constexpr uint32_t kRenderObjectSearchKeyOffset = 16u;
constexpr uint32_t kMaxRegistryBuckets = 4096u;

bool CanReadGuestU32(const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  return callbacks.load_guest_u32 != nullptr;
}

bool IsLikelyGuestDwordAddress(
    uint32_t address,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  return callbacks.is_likely_guest_dword_address != nullptr &&
         callbacks.is_likely_guest_dword_address(address);
}

bool IsLikelyFableObjectAddress(
    uint32_t address,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  return callbacks.is_likely_fable_object_address != nullptr &&
         callbacks.is_likely_fable_object_address(address);
}

uint32_t LoadGuestU32IfLikely(
    uint8_t* base,
    uint32_t address,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  if (base == nullptr || !CanReadGuestU32(callbacks) ||
      !IsLikelyGuestDwordAddress(address, callbacks)) {
    return 0;
  }
  return callbacks.load_guest_u32(base, address);
}

uint32_t CountEntries(uint32_t begin, uint32_t end, uint32_t stride) {
  if (end < begin || stride == 0) {
    return 0;
  }
  return (end - begin) / stride;
}

FableTitleRenderableBucketSnapshot ReadBucket(
    uint8_t* base,
    uint32_t entry,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  FableTitleRenderableBucketSnapshot bucket;
  if (base == nullptr || !CanReadGuestU32(callbacks) ||
      !IsLikelyGuestDwordAddress(entry + kRegistryEntryObjectsEndOffset,
                                 callbacks)) {
    return bucket;
  }

  bucket.valid = true;
  bucket.entry = entry;
  bucket.key = callbacks.load_guest_u32(base, entry);
  bucket.objects_begin =
      callbacks.load_guest_u32(base, entry + kRegistryEntryObjectsBeginOffset);
  bucket.objects_end =
      callbacks.load_guest_u32(base, entry + kRegistryEntryObjectsEndOffset);
  bucket.object_count = CountEntries(bucket.objects_begin, bucket.objects_end,
                                     sizeof(uint32_t));
  if (bucket.object_count != 0 &&
      IsLikelyGuestDwordAddress(bucket.objects_begin, callbacks)) {
    bucket.first_object = callbacks.load_guest_u32(base, bucket.objects_begin);
    if (IsLikelyFableObjectAddress(bucket.first_object, callbacks)) {
      bucket.first_object_vtable =
          LoadGuestU32IfLikely(base, bucket.first_object, callbacks);
    }
  }
  return bucket;
}

FableTitleRenderableBucketSnapshot FindBucketForKey(
    uint8_t* base,
    uint32_t begin,
    uint32_t count,
    uint32_t search_key,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  if (base == nullptr || !CanReadGuestU32(callbacks) || begin == 0 ||
      count == 0) {
    return {};
  }

  uint32_t first = begin;
  uint32_t remaining = std::min<uint32_t>(count, kMaxRegistryBuckets);
  while (remaining > 0) {
    const uint32_t half = remaining >> 1u;
    const uint32_t mid = first + half * kRegistryEntryStride;
    if (!IsLikelyGuestDwordAddress(mid, callbacks)) {
      return {};
    }
    const uint32_t mid_key = callbacks.load_guest_u32(base, mid);
    if (mid_key >= search_key) {
      remaining = half;
    } else {
      first = mid + kRegistryEntryStride;
      remaining = remaining - half - 1u;
    }
  }

  if (!IsLikelyGuestDwordAddress(first, callbacks) ||
      callbacks.load_guest_u32(base, first) != search_key) {
    return {};
  }
  return ReadBucket(base, first, callbacks);
}

}  // namespace

FableTitleWorldRenderableRegistrySnapshot
FableReadTitleWorldRenderableRegistry(
    uint8_t* base,
    uint32_t world,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  FableTitleWorldRenderableRegistrySnapshot snapshot;
  if (base == nullptr || !CanReadGuestU32(callbacks) ||
      !IsLikelyFableObjectAddress(world, callbacks) ||
      world > std::numeric_limits<uint32_t>::max() -
                  kWorldRenderableRegistryOffset) {
    return snapshot;
  }

  snapshot.valid = true;
  snapshot.world = world;
  snapshot.registry = world + kWorldRenderableRegistryOffset;
  if (!IsLikelyGuestDwordAddress(snapshot.registry + kRegistryEndOffset,
                                 callbacks)) {
    return snapshot;
  }

  snapshot.begin =
      callbacks.load_guest_u32(base, snapshot.registry + kRegistryBeginOffset);
  snapshot.end =
      callbacks.load_guest_u32(base, snapshot.registry + kRegistryEndOffset);
  snapshot.count =
      CountEntries(snapshot.begin, snapshot.end, kRegistryEntryStride);
  if (snapshot.count != 0 && IsLikelyGuestDwordAddress(snapshot.begin,
                                                       callbacks)) {
    snapshot.first_bucket = ReadBucket(base, snapshot.begin, callbacks);
  }
  return snapshot;
}

FableTitleWorldRenderableRegistrySnapshot
FableReadTitleWorldRenderableRegistryForScene(
    uint8_t* base,
    uint32_t scene,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  if (base == nullptr || !CanReadGuestU32(callbacks) ||
      !IsLikelyFableObjectAddress(scene, callbacks)) {
    return {};
  }

  FableTitleWorldRenderableRegistrySnapshot snapshot =
      FableReadTitleWorldRenderableRegistry(
          base, callbacks.load_guest_u32(base, scene + 20u), callbacks);
  snapshot.scene = scene;
  return snapshot;
}

FableTitleWorldRenderableRegistrySnapshot
FableReadTitleWorldRenderableRegistryForRenderCallback(
    uint8_t* base,
    uint32_t render_object,
    uint32_t world,
    const FableTitleWorldRegistryAddressCallbacks& callbacks) {
  FableTitleWorldRenderableRegistrySnapshot snapshot =
      FableReadTitleWorldRenderableRegistry(base, world, callbacks);
  snapshot.render_object = render_object;
  if (base == nullptr || !CanReadGuestU32(callbacks) ||
      !IsLikelyFableObjectAddress(render_object, callbacks)) {
    return snapshot;
  }

  snapshot.render_object_vtable =
      LoadGuestU32IfLikely(base, render_object, callbacks);
  snapshot.search_key =
      LoadGuestU32IfLikely(base, render_object + kRenderObjectSearchKeyOffset,
                           callbacks);
  snapshot.matched_bucket =
      FindBucketForKey(base, snapshot.begin, snapshot.count,
                       snapshot.search_key, callbacks);
  return snapshot;
}

}  // namespace refii::renderer
