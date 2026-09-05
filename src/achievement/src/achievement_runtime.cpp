#include <achievement/achievement_runtime.h>

#include <atomic>
#include <chrono>
#include <mutex>
#include <unordered_map>

namespace refii::achievement {
namespace {

std::uint64_t MakeUnlockKey(const std::uint32_t user_index,
                            const std::uint32_t achievement_id) {
  return (static_cast<std::uint64_t>(user_index) << 32) | achievement_id;
}

/// Now as a Windows FILETIME — 100ns ticks since 1601-01-01 — the unit the enumerator returns.
std::uint64_t NowAsFileTime() {
  using namespace std::chrono;
  const auto since_epoch = system_clock::now().time_since_epoch();
  const std::uint64_t ticks =
      static_cast<std::uint64_t>(duration_cast<nanoseconds>(since_epoch).count() / 100);
  return ticks + 116444736000000000ull;
}

std::mutex g_mutex;
std::uint64_t g_next_sequence = 1;
std::vector<AchievementUnlock> g_writes;
std::unordered_map<std::uint64_t, std::size_t> g_write_index;
std::unordered_map<std::uint32_t, std::uint64_t> g_unlock_time_by_id;
std::vector<std::uint8_t> g_title_xdbf;
std::atomic<std::uint32_t> g_ui_requests{0};

} // namespace

void RecordAchievementWrites(const std::span<const AchievementWrite> writes) {
  const std::lock_guard<std::mutex> lock(g_mutex);
  for (const AchievementWrite &write : writes) {
    const std::uint64_t key = MakeUnlockKey(write.user_index, write.achievement_id);
    if (g_write_index.contains(key)) {
      continue;
    }

    g_write_index[key] = g_writes.size();
    g_writes.push_back({write.user_index, write.achievement_id, g_next_sequence++});
    g_unlock_time_by_id.try_emplace(write.achievement_id, NowAsFileTime());
  }
}

std::vector<AchievementUnlock> GetAchievementWritesSnapshot() {
  const std::lock_guard<std::mutex> lock(g_mutex);
  return g_writes;
}

void SetUnlockedAchievements(const std::span<const std::uint32_t> ids,
                             const std::span<const std::uint64_t> unlock_filetimes) {
  const std::lock_guard<std::mutex> lock(g_mutex);
  g_unlock_time_by_id.clear();
  for (std::size_t i = 0; i < ids.size(); ++i) {
    const std::uint64_t when = i < unlock_filetimes.size() && unlock_filetimes[i] != 0
                                   ? unlock_filetimes[i]
                                   : NowAsFileTime();
    g_unlock_time_by_id[ids[i]] = when;
  }

  // A write the guest made this session stays unlocked even if the host's statement predates
  // it — the statement is a snapshot of the store, the write is newer than the snapshot.
  for (const AchievementUnlock &write : g_writes) {
    g_unlock_time_by_id.try_emplace(write.achievement_id, NowAsFileTime());
  }
}

bool IsAchievementUnlocked(const std::uint32_t achievement_id) {
  return GetAchievementUnlockTime(achievement_id) != 0;
}

std::uint64_t GetAchievementUnlockTime(const std::uint32_t achievement_id) {
  const std::lock_guard<std::mutex> lock(g_mutex);
  const auto it = g_unlock_time_by_id.find(achievement_id);
  return it == g_unlock_time_by_id.end() ? 0 : it->second;
}

void SetTitleXdbf(const std::span<const std::uint8_t> bytes) {
  const std::lock_guard<std::mutex> lock(g_mutex);
  g_title_xdbf.assign(bytes.begin(), bytes.end());
}

std::vector<std::uint8_t> GetTitleXdbfSnapshot() {
  const std::lock_guard<std::mutex> lock(g_mutex);
  return g_title_xdbf;
}

void RequestAchievementsUi(const std::uint32_t user_index) {
  (void)user_index;
  g_ui_requests.fetch_add(1, std::memory_order_relaxed);
}

std::uint32_t GetAchievementsUiRequestCount() {
  return g_ui_requests.load(std::memory_order_relaxed);
}

} // namespace refii::achievement
