#include "runtime/refii_renderer/plume/fable/title/fable_title_scene_observer.h"

#include <cmath>

namespace refii::renderer {

namespace {

constexpr uint32_t kCurrentSceneIndexOffset = 116u;
constexpr uint32_t kPreviousSceneIndexOffset = 120u;
constexpr uint32_t kNextSceneIndexOffset = 124u;
constexpr uint32_t kPendingSceneIndexOffset = 128u;
constexpr uint32_t kSceneSwapPendingOffset = 132u;
constexpr uint32_t kTransitionTimeOffset = 152u;
constexpr uint32_t kTransitionTargetTimeOffset = 160u;
constexpr uint32_t kFrameAdvancePendingOffset = 168u;
constexpr uint32_t kWorldOrGameOffset = 592u;
constexpr uint32_t kFrameTimeReferenceOffset = 596u;
constexpr uint32_t kSceneFactoryTableBeginAddress = 0x8349F79Cu;
constexpr uint32_t kSceneFactoryTableEndAddress = 0x8349F7A0u;
constexpr uint32_t kSchedulerGlobalAAddress = 0x83496AB8u;
constexpr uint32_t kSchedulerGlobalBAddress = 0x83496AC0u;
constexpr uint32_t kSchedulerGlobalCAddress = 0x8335C4C4u;
constexpr uint32_t kSchedulerStreamsBeginAddress = 0x8349F9B0u;
constexpr uint32_t kSchedulerTimesBeginAddress = 0x8335F7D8u;

bool CanReadU32(const FableTitleSceneObserverCallbacks& callbacks) {
  return callbacks.scene_table.load_guest_u32 != nullptr;
}

bool CanReadU8(const FableTitleSceneObserverCallbacks& callbacks) {
  return callbacks.scene_table.load_guest_u8 != nullptr;
}

bool CanReadF32(const FableTitleSceneObserverCallbacks& callbacks) {
  return callbacks.load_guest_f32 != nullptr;
}

bool CanReadF64(const FableTitleSceneObserverCallbacks& callbacks) {
  return callbacks.load_guest_f64 != nullptr;
}

bool IsLikelyFableObjectAddress(
    uint32_t address,
    const FableTitleSceneObserverCallbacks& callbacks) {
  return callbacks.scene_table.is_likely_fable_object_address != nullptr &&
         callbacks.scene_table.is_likely_fable_object_address(address);
}

uint32_t LoadU32(uint8_t* base,
                 uint32_t address,
                 const FableTitleSceneObserverCallbacks& callbacks) {
  return CanReadU32(callbacks)
             ? callbacks.scene_table.load_guest_u32(base, address)
             : 0u;
}

uint8_t LoadU8(uint8_t* base,
               uint32_t address,
               const FableTitleSceneObserverCallbacks& callbacks) {
  return CanReadU8(callbacks) ? callbacks.scene_table.load_guest_u8(base, address)
                              : 0u;
}

float LoadF32(uint8_t* base,
              uint32_t address,
              const FableTitleSceneObserverCallbacks& callbacks) {
  return CanReadF32(callbacks) ? callbacks.load_guest_f32(base, address) : 0.0f;
}

double LoadF64(uint8_t* base,
               uint32_t address,
               const FableTitleSceneObserverCallbacks& callbacks) {
  return CanReadF64(callbacks) ? callbacks.load_guest_f64(base, address) : 0.0;
}

uint32_t SelectSchedulerGlobal(uint32_t global_a,
                               uint32_t global_b,
                               uint32_t global_c) {
  if (global_a != 0) {
    return global_a;
  }
  return global_b != 0 ? global_b : global_c;
}

}  // namespace

FableTitleRenderFrameOwnerSnapshot FableReadTitleRenderFrameOwnerSnapshot(
    uint8_t* base,
    uint32_t renderer,
    const FableTitleSceneObserverCallbacks& callbacks) {
  FableTitleRenderFrameOwnerSnapshot snapshot;
  if (base == nullptr || renderer == 0 || !CanReadU32(callbacks)) {
    return snapshot;
  }

  snapshot.valid = true;
  snapshot.renderer = renderer;
  snapshot.current_index =
      LoadU32(base, renderer + kCurrentSceneIndexOffset, callbacks);
  snapshot.previous_index =
      LoadU32(base, renderer + kPreviousSceneIndexOffset, callbacks);
  snapshot.next_index =
      LoadU32(base, renderer + kNextSceneIndexOffset, callbacks);
  snapshot.pending_index =
      LoadU32(base, renderer + kPendingSceneIndexOffset, callbacks);
  snapshot.scene_swap_pending =
      LoadU8(base, renderer + kSceneSwapPendingOffset, callbacks);
  snapshot.frame_advance_pending =
      LoadU8(base, renderer + kFrameAdvancePendingOffset, callbacks);
  snapshot.transition_time =
      LoadF64(base, renderer + kTransitionTimeOffset, callbacks);
  snapshot.transition_target_time =
      LoadF64(base, renderer + kTransitionTargetTimeOffset, callbacks);
  snapshot.transition_ready =
      std::isfinite(snapshot.transition_time) &&
      std::isfinite(snapshot.transition_target_time) &&
      snapshot.transition_time >= snapshot.transition_target_time;
  snapshot.world_or_game =
      LoadU32(base, renderer + kWorldOrGameOffset, callbacks);
  snapshot.frame_time_reference =
      LoadF32(base, renderer + kFrameTimeReferenceOffset, callbacks);
  snapshot.table = FableReadTitleSceneTable(base, callbacks.scene_table);
  snapshot.scheduler_global_a =
      LoadU32(base, kSchedulerGlobalAAddress, callbacks);
  snapshot.scheduler_global_b =
      LoadU32(base, kSchedulerGlobalBAddress, callbacks);
  snapshot.scheduler_global_c =
      LoadU32(base, kSchedulerGlobalCAddress, callbacks);
  snapshot.scheduler_selected =
      SelectSchedulerGlobal(snapshot.scheduler_global_a,
                            snapshot.scheduler_global_b,
                            snapshot.scheduler_global_c);
  snapshot.scheduler_selected_inner =
      IsLikelyFableObjectAddress(snapshot.scheduler_selected, callbacks)
          ? LoadU32(base, snapshot.scheduler_selected + 8u, callbacks)
          : 0u;
  snapshot.scheduler_holder =
      IsLikelyFableObjectAddress(snapshot.scheduler_selected_inner, callbacks)
          ? LoadU32(base, snapshot.scheduler_selected_inner + 36u, callbacks)
          : 0u;
  snapshot.scheduler_holder_owner =
      IsLikelyFableObjectAddress(snapshot.scheduler_holder, callbacks)
          ? LoadU32(base, snapshot.scheduler_holder + 4u, callbacks)
          : 0u;
  snapshot.scheduler_holder_lock =
      IsLikelyFableObjectAddress(snapshot.scheduler_holder, callbacks)
          ? LoadU32(base, snapshot.scheduler_holder + 8u, callbacks)
          : 0u;
  snapshot.scheduler_holder_mode =
      IsLikelyFableObjectAddress(snapshot.scheduler_holder, callbacks)
          ? LoadU32(base, snapshot.scheduler_holder + 44u, callbacks)
          : UINT32_MAX;
  snapshot.current_slot =
      FableReadTitleSceneSlot(base, snapshot.current_index,
                              callbacks.scene_table);
  snapshot.previous_slot =
      FableReadTitleSceneSlot(base, snapshot.previous_index,
                              callbacks.scene_table);
  snapshot.next_slot =
      FableReadTitleSceneSlot(base, snapshot.next_index, callbacks.scene_table);
  snapshot.pending_slot =
      FableReadTitleSceneSlot(base, snapshot.pending_index,
                              callbacks.scene_table);
  return snapshot;
}

FableTitleFrameDirectorGateSnapshot FableReadTitleFrameDirectorGateSnapshot(
    uint8_t* base,
    uint32_t scene_index,
    const FableTitleSceneObserverCallbacks& callbacks) {
  FableTitleFrameDirectorGateSnapshot snapshot;
  if (base == nullptr || !CanReadU32(callbacks)) {
    return snapshot;
  }

  snapshot.valid = true;
  snapshot.scene_index = scene_index;
  snapshot.table = FableReadTitleSceneTable(base, callbacks.scene_table);
  snapshot.factory_table_begin =
      LoadU32(base, kSceneFactoryTableBeginAddress, callbacks);
  snapshot.factory_table_end =
      LoadU32(base, kSceneFactoryTableEndAddress, callbacks);
  snapshot.factory_table_count =
      snapshot.factory_table_end >= snapshot.factory_table_begin
          ? (snapshot.factory_table_end - snapshot.factory_table_begin) / 4u
          : 0u;

  for (uint32_t stream_index = 0;
       stream_index < snapshot.scheduler_streams.size(); ++stream_index) {
    snapshot.scheduler_streams[stream_index] =
        LoadU32(base, kSchedulerStreamsBeginAddress + stream_index * 4u,
                callbacks);
    if (snapshot.scheduler_streams[stream_index] != 0) {
      ++snapshot.scheduler_stream_count;
    }
    snapshot.scheduler_times[stream_index] =
        LoadF64(base, kSchedulerTimesBeginAddress +
                          stream_index * sizeof(double),
                callbacks);
    if (IsLikelyFableObjectAddress(snapshot.scheduler_streams[stream_index],
                                   callbacks)) {
      snapshot.stream_visible_ends[stream_index] =
          LoadU32(base, snapshot.scheduler_streams[stream_index] + 16u,
                  callbacks);
      snapshot.stream_published_ends[stream_index] =
          LoadU32(base, snapshot.scheduler_streams[stream_index] + 60u,
                  callbacks);
    }
  }

  snapshot.valid_index =
      scene_index != UINT32_MAX && scene_index < snapshot.table.count;
  snapshot.scene_slot =
      FableReadTitleSceneSlot(base, scene_index, callbacks.scene_table);
  snapshot.readable_scene =
      snapshot.scene_slot.valid_index &&
      IsLikelyFableObjectAddress(snapshot.scene_slot.scene, callbacks);
  snapshot.scene_submit_gate =
      snapshot.valid_index && snapshot.scene_slot.scene != 0 &&
      snapshot.scene_slot.scene_type == 1u;

  if (snapshot.readable_scene) {
    for (uint32_t i = 0; i < 4u; ++i) {
      snapshot.scene_rect_target[i] =
          LoadF32(base, snapshot.scene_slot.scene + 532u + i * 4u, callbacks);
      snapshot.scene_rect_source[i] =
          LoadF32(base, snapshot.scene_slot.scene + 660u + i * 4u, callbacks);
      snapshot.scene_rect_effective[i] =
          LoadF32(base, snapshot.scene_slot.scene + 128u + i * 4u, callbacks);
    }
  }
  return snapshot;
}

}  // namespace refii::renderer
