#pragma once

#include <cstdint>
#include <span>
#include <vector>

// THE GUEST-FACING HALF OF ACHIEVEMENTS, and nothing else. Godot owns the store, the
// definitions, the persistence, the toast and the page (Engine_Achievements); this library is
// the seam the kernel hooks write into and the host bridge reads out of:
//
//   guest XGIUserWriteAchievements  -> RecordAchievementWrites  -> GetAchievementWritesSnapshot
//   guest XamUserCreateAchievementEnumerator <- SetUnlockedAchievements (stated by Godot)
//   module launch                   -> SetTitleXdbf            -> GetTitleXdbfSnapshot
//   guest XamShowAchievementsUI     -> RequestAchievementsUi   -> GetAchievementsUiRequestCount
//
// Every entry point is safe from any thread and none touches the disk: the write hook runs on
// the guest thread that issued the XGI message, synchronously inside the kernel call.
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

/// Records the guest's writes, stamping each NEW (user, id) pair with a monotonic sequence. A
/// pair already recorded this session is not stamped again.
void RecordAchievementWrites(std::span<const AchievementWrite> writes);

/// Every write recorded this session, in sequence order. A sequence, not a queue: the host holds
/// the last sequence it saw and asks for what is newer, so a repeated read loses nothing.
std::vector<AchievementUnlock> GetAchievementWritesSnapshot();

/// The unlocked set as the host states it — ids with their unlock FILETIMEs — read back by the
/// guest's achievement enumerator. Replaces the previous statement wholesale.
void SetUnlockedAchievements(std::span<const std::uint32_t> ids,
                             std::span<const std::uint64_t> unlock_filetimes);

bool IsAchievementUnlocked(std::uint32_t achievement_id);

/// The unlock FILETIME, or 0 while locked. A write recorded this session counts as unlocked
/// from the moment it lands, so the guest never reads back a write it just made as locked.
std::uint64_t GetAchievementUnlockTime(std::uint32_t achievement_id);

/// The title's XDBF resource, copied out of the executable module once it is launched.
void SetTitleXdbf(std::span<const std::uint8_t> bytes);

std::vector<std::uint8_t> GetTitleXdbfSnapshot();

/// `XamShowAchievementsUI`: the guest asks for the achievements UI and the host draws it.
void RequestAchievementsUi(std::uint32_t user_index);

/// How many times the guest has asked this session.
std::uint32_t GetAchievementsUiRequestCount();

} // namespace refii::achievement
