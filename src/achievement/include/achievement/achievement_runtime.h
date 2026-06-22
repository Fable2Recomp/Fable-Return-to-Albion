#pragma once

#include <cstdint>
#include <filesystem>
#include <span>
#include <string>
#include <vector>

namespace refii::achievement {

struct AchievementWrite {
  std::uint32_t user_index = 0;
  std::uint32_t achievement_id = 0;
};

struct AchievementUnlock {
  std::uint32_t user_index = 0;
  std::uint32_t achievement_id = 0;
  std::uint64_t sequence = 0;
};

struct AchievementIconPng {
  std::uint32_t achievement_id = 0;
  std::uint32_t image_id = 0;
  std::vector<std::uint8_t> bytes;
};

struct AchievementDefinition {
  std::uint32_t achievement_id = 0;
  std::uint32_t image_id = 0;
  std::uint32_t gamerscore = 0;
  std::uint32_t flags = 0;
  std::string name;
  std::string unlocked_description;
  std::string locked_description;
};

void RecordAchievementWrites(std::span<const AchievementWrite> writes);

bool IsAchievementUnlocked(std::uint32_t user_index,
                           std::uint32_t achievement_id);

std::vector<AchievementUnlock> GetUnlockedAchievementsSnapshot();

void LoadAchievementSave();

void FlushAchievementSave();

void ClearAchievementSave();

void SetAchievementSavePath(std::filesystem::path save_path);

void RegisterAchievementDefinition(const AchievementDefinition &definition);

AchievementDefinition
GetAchievementDefinitionSnapshot(std::uint32_t achievement_id);

std::vector<AchievementDefinition> GetAchievementDefinitionsSnapshot();

void RegisterAchievementIconPng(std::uint32_t achievement_id,
                                std::uint32_t image_id,
                                std::span<const std::uint8_t> png_bytes);

std::vector<std::uint8_t>
GetAchievementIconPngSnapshot(std::uint32_t achievement_id);

std::vector<std::uint8_t>
GetAchievementIconPngByImageIdSnapshot(std::uint32_t image_id);

} // namespace refii::achievement
