#pragma once

#include "runtime/refii_renderer/plume/fable/title/fable_title_scene_table.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FableTitleSceneObserverCallbacks {
  FableTitleSceneTableAddressCallbacks scene_table;
  double (*load_guest_f64)(uint8_t* base, uint32_t guest_address) = nullptr;
  float (*load_guest_f32)(uint8_t* base, uint32_t guest_address) = nullptr;
};

struct FableTitleRenderFrameOwnerSnapshot {
  bool valid = false;
  uint32_t renderer = 0;
  uint32_t current_index = UINT32_MAX;
  uint32_t previous_index = UINT32_MAX;
  uint32_t next_index = UINT32_MAX;
  uint32_t pending_index = UINT32_MAX;
  uint8_t scene_swap_pending = 0;
  uint8_t frame_advance_pending = 0;
  double transition_time = 0.0;
  double transition_target_time = 0.0;
  bool transition_ready = false;
  uint32_t world_or_game = 0;
  float frame_time_reference = 0.0f;
  FableTitleSceneTableSnapshot table;
  uint32_t scheduler_global_a = 0;
  uint32_t scheduler_global_b = 0;
  uint32_t scheduler_global_c = 0;
  uint32_t scheduler_selected = 0;
  uint32_t scheduler_selected_inner = 0;
  uint32_t scheduler_holder = 0;
  uint32_t scheduler_holder_owner = 0;
  uint32_t scheduler_holder_lock = 0;
  uint32_t scheduler_holder_mode = UINT32_MAX;
  FableTitleSceneSlotSnapshot current_slot;
  FableTitleSceneSlotSnapshot previous_slot;
  FableTitleSceneSlotSnapshot next_slot;
  FableTitleSceneSlotSnapshot pending_slot;
};

struct FableTitleFrameDirectorGateSnapshot {
  bool valid = false;
  uint32_t scene_index = UINT32_MAX;
  FableTitleSceneTableSnapshot table;
  uint32_t factory_table_begin = 0;
  uint32_t factory_table_end = 0;
  uint32_t factory_table_count = 0;
  std::array<uint32_t, 4> scheduler_streams = {};
  std::array<double, 4> scheduler_times = {};
  std::array<uint32_t, 4> stream_visible_ends = {};
  std::array<uint32_t, 4> stream_published_ends = {};
  uint32_t scheduler_stream_count = 0;
  bool valid_index = false;
  FableTitleSceneSlotSnapshot scene_slot;
  bool readable_scene = false;
  bool scene_submit_gate = false;
  std::array<float, 4> scene_rect_target = {};
  std::array<float, 4> scene_rect_source = {};
  std::array<float, 4> scene_rect_effective = {};
};

FableTitleRenderFrameOwnerSnapshot FableReadTitleRenderFrameOwnerSnapshot(
    uint8_t* base,
    uint32_t renderer,
    const FableTitleSceneObserverCallbacks& callbacks);

FableTitleFrameDirectorGateSnapshot FableReadTitleFrameDirectorGateSnapshot(
    uint8_t* base,
    uint32_t scene_index,
    const FableTitleSceneObserverCallbacks& callbacks);

}  // namespace refii::renderer
