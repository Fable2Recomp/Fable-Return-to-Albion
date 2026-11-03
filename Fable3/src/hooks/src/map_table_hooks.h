// refii - Fable 3's map tables (map_table_hooks.cpp), what the render scene's remove shares with
// them (world_hooks.cpp): a removed render object id is no longer a map object.

#pragma once

#include <cstdint>

namespace refii::fable3 {

/// The render object `id` is gone (sub_82C3C148 posted its remove): drop it if a map holder owned it.
void MapTableObjectRemoved(uint32_t id);

}  // namespace refii::fable3
