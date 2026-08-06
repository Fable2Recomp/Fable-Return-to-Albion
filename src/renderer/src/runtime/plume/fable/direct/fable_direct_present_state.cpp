#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"

#include <algorithm>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace refii::renderer {
namespace {

struct FableDirectPresentState {
  std::mutex mutex;
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
  std::unordered_map<uint32_t, RendererSurfaceCopyAliasSnapshot>
      surface_copy_aliases;
};

FableDirectPresentState g_fable_direct_present;

void ClearDisplayPresentLocked() {
  g_fable_direct_present.display_texture_base = 0;
  g_fable_direct_present.display_width = 0;
  g_fable_direct_present.display_height = 0;
  g_fable_direct_present.display_format = 0;
  g_fable_direct_present.display_surface_object = 0;
  g_fable_direct_present.display_source = 0;
  g_fable_direct_present.display_register = 0;
  g_fable_direct_present.display_register_value = 0;
  g_fable_direct_present.display_fetch_dwords = {};
}

}  // namespace

void FableDirectSetDisplayPresent(uint32_t texture_base, uint32_t width,
                                  uint32_t height, uint32_t format,
                                  uint32_t source, uint32_t register_index,
                                  uint32_t register_value,
                                  const std::array<uint32_t, 6>* fetch_dwords,
                                  uint32_t surface_object) {
  std::lock_guard<std::mutex> lock(g_fable_direct_present.mutex);
  ++g_fable_direct_present.serial;
  if (texture_base != 0) {
    g_fable_direct_present.display_texture_base = texture_base;
  }
  if (surface_object != 0) {
    g_fable_direct_present.display_surface_object = surface_object;
  }
  if (width != 0) {
    g_fable_direct_present.display_width = width;
  }
  if (height != 0) {
    g_fable_direct_present.display_height = height;
  }
  if (format != 0) {
    g_fable_direct_present.display_format = format;
  }
  g_fable_direct_present.display_source = source;
  g_fable_direct_present.display_register = register_index;
  g_fable_direct_present.display_register_value = register_value;
  if (fetch_dwords) {
    g_fable_direct_present.display_fetch_dwords = *fetch_dwords;
  }
}

void FableDirectTrackSurfaceCopyAlias(
    const RendererSurfaceCopyAliasSnapshot& alias) {
  if (alias.source_surface == 0 || alias.destination_surface == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct_present.mutex);
  ++g_fable_direct_present.serial;
  g_fable_direct_present.surface_copy_aliases[alias.destination_surface] =
      alias;
}

bool FableDirectFindSurfaceCopyAliasByDestination(
    uint32_t destination_surface, RendererSurfaceCopyAliasSnapshot* alias) {
  if (destination_surface == 0 || !alias) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct_present.mutex);
  const auto it =
      g_fable_direct_present.surface_copy_aliases.find(destination_surface);
  if (it == g_fable_direct_present.surface_copy_aliases.end()) {
    return false;
  }

  *alias = it->second;
  return true;
}

void FableDirectForgetPresentResource(uint32_t resource_object,
                                      uint32_t texture_base) {
  if (resource_object == 0 && texture_base == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct_present.mutex);
  bool changed = false;
  if ((resource_object != 0 &&
       g_fable_direct_present.display_surface_object == resource_object) ||
      (texture_base != 0 &&
       g_fable_direct_present.display_texture_base == texture_base)) {
    ClearDisplayPresentLocked();
    changed = true;
  }

  for (auto it = g_fable_direct_present.surface_copy_aliases.begin();
       it != g_fable_direct_present.surface_copy_aliases.end();) {
    const RendererSurfaceCopyAliasSnapshot& alias = it->second;
    if (resource_object != 0 &&
        (alias.source_surface == resource_object ||
         alias.destination_surface == resource_object)) {
      it = g_fable_direct_present.surface_copy_aliases.erase(it);
      changed = true;
      continue;
    }
    ++it;
  }

  if (changed) {
    ++g_fable_direct_present.serial;
  }
}

void FableDirectClearDisplayPresent() {
  std::lock_guard<std::mutex> lock(g_fable_direct_present.mutex);
  ClearDisplayPresentLocked();
  g_fable_direct_present.surface_copy_aliases.clear();
  ++g_fable_direct_present.serial;
}

FableDirectPresentSnapshot GetFableDirectPresentSnapshot() {
  std::lock_guard<std::mutex> lock(g_fable_direct_present.mutex);
  FableDirectPresentSnapshot snapshot;
  snapshot.serial = g_fable_direct_present.serial;
  snapshot.display_texture_base =
      g_fable_direct_present.display_texture_base;
  snapshot.display_width = g_fable_direct_present.display_width;
  snapshot.display_height = g_fable_direct_present.display_height;
  snapshot.display_format = g_fable_direct_present.display_format;
  snapshot.display_surface_object =
      g_fable_direct_present.display_surface_object;
  snapshot.display_source = g_fable_direct_present.display_source;
  snapshot.display_register = g_fable_direct_present.display_register;
  snapshot.display_register_value =
      g_fable_direct_present.display_register_value;
  snapshot.display_fetch_dwords = g_fable_direct_present.display_fetch_dwords;

  std::vector<RendererSurfaceCopyAliasSnapshot> aliases;
  aliases.reserve(g_fable_direct_present.surface_copy_aliases.size());
  for (const auto& [destination, alias] :
       g_fable_direct_present.surface_copy_aliases) {
    (void)destination;
    aliases.push_back(alias);
  }
  std::sort(aliases.begin(), aliases.end(),
            [](const RendererSurfaceCopyAliasSnapshot& lhs,
               const RendererSurfaceCopyAliasSnapshot& rhs) {
              return lhs.serial > rhs.serial;
            });
  for (const RendererSurfaceCopyAliasSnapshot& alias : aliases) {
    if (snapshot.surface_copy_alias_count >=
        snapshot.surface_copy_aliases.size()) {
      break;
    }
    snapshot.surface_copy_aliases[snapshot.surface_copy_alias_count++] = alias;
  }
  return snapshot;
}

}  // namespace refii::renderer
