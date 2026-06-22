#pragma once

#include <achievement/achievement_runtime.h>

#include <cstdint>
#include <span>

namespace refii::achievement {

template <typename XdbfGameData, typename XdbfImageSection,
          typename XdbfLanguage>
void CacheAchievementsFromXdbf(const XdbfGameData &db,
                               const XdbfImageSection image_section,
                               const XdbfLanguage language,
                               const std::uint32_t first_icon_id = 1,
                               const std::uint32_t last_icon_id = 66) {
  if (!db.is_valid()) {
    return;
  }

  for (const auto &entry : db.GetAchievements()) {
    const std::uint32_t achievement_id = entry.id;
    const std::uint32_t image_id = entry.image_id;

    AchievementDefinition definition;
    definition.achievement_id = achievement_id;
    definition.image_id = image_id;
    definition.gamerscore = entry.gamerscore;
    definition.flags = entry.flags;
    definition.name = db.GetStringTableEntry(language, entry.label_id);
    definition.unlocked_description =
        db.GetStringTableEntry(language, entry.description_id);
    definition.locked_description =
        db.GetStringTableEntry(language, entry.unachieved_id);
    RegisterAchievementDefinition(definition);

    if (image_id < first_icon_id || image_id > last_icon_id) {
      continue;
    }

    const auto icon = db.GetEntry(image_section, image_id);
    if (!icon) {
      continue;
    }

    RegisterAchievementIconPng(
        achievement_id, image_id,
        std::span<const std::uint8_t>(icon.buffer, icon.size));
  }
}

} // namespace refii::achievement
