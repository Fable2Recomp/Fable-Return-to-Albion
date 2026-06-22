#include <achievement/achievement_runtime.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_map>

namespace refii::achievement {
namespace {

std::uint64_t MakeUnlockKey(const std::uint32_t user_index,
                            const std::uint32_t achievement_id) {
  return (static_cast<std::uint64_t>(user_index) << 32) | achievement_id;
}

std::mutex g_unlock_mutex;
std::uint64_t g_next_sequence = 1;
std::unordered_map<std::uint64_t, AchievementUnlock> g_unlocks;
std::unordered_map<std::uint32_t, AchievementDefinition>
    g_definitions_by_achievement_id;
std::unordered_map<std::uint32_t, AchievementIconPng> g_icons_by_achievement_id;
std::unordered_map<std::uint32_t, std::uint32_t> g_achievement_id_by_image_id;
std::filesystem::path g_save_path;
bool g_save_loaded = false;

constexpr std::string_view kSaveFileHeader = "REFIIACHV1";
constexpr std::string_view kSavePayloadHeader = "refii-achievements-v1";
constexpr std::array<std::uint64_t, 4> kSaveTagSeeds = {
    0x6a9d2f94b4f2a145ull,
    0xd44f4f4a6d5a3f9bull,
    0x928f7d9e91c3bb23ull,
    0x3f38e2f5bd789573ull,
};
constexpr std::array<std::uint8_t, 32> kSaveKey = {
    0x8a, 0x38, 0xe6, 0x61, 0x47, 0x2c, 0x9f, 0x19, 0x31, 0xb4, 0x67,
    0xd2, 0x5b, 0x08, 0xf0, 0xae, 0xca, 0x73, 0x14, 0x92, 0xef, 0x5d,
    0x20, 0x6b, 0x7c, 0xd9, 0x03, 0xa6, 0x41, 0xbe, 0x88, 0x55,
};

std::filesystem::path AchievementSavePath() {
  if (!g_save_path.empty()) {
    return g_save_path;
  }
  return std::filesystem::path("assets") / "save" / "achievements.refii";
}

std::uint64_t Fnv1a64(std::uint64_t hash,
                      const std::span<const std::uint8_t> bytes) {
  constexpr std::uint64_t kPrime = 1099511628211ull;
  for (const std::uint8_t byte : bytes) {
    hash ^= byte;
    hash *= kPrime;
  }
  return hash;
}

std::array<std::uint8_t, 8> ToLittleEndianBytes(std::uint64_t value) {
  std::array<std::uint8_t, 8> bytes{};
  for (std::size_t i = 0; i < bytes.size(); ++i) {
    bytes[i] = static_cast<std::uint8_t>((value >> (i * 8)) & 0xFF);
  }
  return bytes;
}

std::uint64_t SplitMix64(std::uint64_t &state) {
  state += 0x9e3779b97f4a7c15ull;
  std::uint64_t value = state;
  value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9ull;
  value = (value ^ (value >> 27)) * 0x94d049bb133111ebull;
  return value ^ (value >> 31);
}

std::string HexEncode(const std::span<const std::uint8_t> bytes) {
  std::ostringstream out;
  out << std::hex << std::setfill('0');
  for (const std::uint8_t byte : bytes) {
    out << std::setw(2) << static_cast<unsigned>(byte);
  }
  return out.str();
}

std::string HexEncode64(const std::uint64_t value) {
  std::ostringstream out;
  out << std::hex << std::setfill('0') << std::setw(16) << value;
  return out.str();
}

std::optional<std::uint8_t> HexNibble(const char ch) {
  if (ch >= '0' && ch <= '9') {
    return static_cast<std::uint8_t>(ch - '0');
  }
  if (ch >= 'a' && ch <= 'f') {
    return static_cast<std::uint8_t>(10 + ch - 'a');
  }
  if (ch >= 'A' && ch <= 'F') {
    return static_cast<std::uint8_t>(10 + ch - 'A');
  }
  return std::nullopt;
}

std::optional<std::vector<std::uint8_t>> HexDecode(const std::string_view hex) {
  if ((hex.size() % 2) != 0) {
    return std::nullopt;
  }

  std::vector<std::uint8_t> bytes;
  bytes.reserve(hex.size() / 2);
  for (std::size_t i = 0; i < hex.size(); i += 2) {
    const std::optional<std::uint8_t> high = HexNibble(hex[i]);
    const std::optional<std::uint8_t> low = HexNibble(hex[i + 1]);
    if (!high || !low) {
      return std::nullopt;
    }
    bytes.push_back(static_cast<std::uint8_t>((*high << 4) | *low));
  }
  return bytes;
}

std::uint64_t MakeSaveSalt() {
  const auto now = std::chrono::steady_clock::now().time_since_epoch().count();
  std::uint64_t salt = static_cast<std::uint64_t>(now);
  salt ^= static_cast<std::uint64_t>(g_unlocks.size()) * 0x9e3779b97f4a7c15ull;
  return SplitMix64(salt);
}

std::uint64_t MakeSaveStreamSeed(const std::uint64_t salt) {
  std::uint64_t hash = 14695981039346656037ull;
  hash = Fnv1a64(hash, kSaveKey);
  const std::array<std::uint8_t, 8> salt_bytes = ToLittleEndianBytes(salt);
  hash = Fnv1a64(hash, salt_bytes);
  return hash;
}

std::vector<std::uint8_t>
XorWithSaveStream(const std::span<const std::uint8_t> input,
                  const std::uint64_t salt) {
  std::vector<std::uint8_t> output(input.begin(), input.end());
  std::uint64_t stream_state = MakeSaveStreamSeed(salt);
  std::uint64_t stream_block = 0;
  std::size_t stream_byte_index = sizeof(stream_block);

  for (std::uint8_t &byte : output) {
    if (stream_byte_index >= sizeof(stream_block)) {
      stream_block = SplitMix64(stream_state);
      stream_byte_index = 0;
    }
    byte ^= static_cast<std::uint8_t>(
        (stream_block >> (stream_byte_index * 8)) & 0xFF);
    ++stream_byte_index;
  }

  return output;
}

std::array<std::uint64_t, 4>
MakeSaveTag(const std::uint64_t salt,
            const std::span<const std::uint8_t> encoded) {
  std::array<std::uint64_t, 4> tag{};
  const std::array<std::uint8_t, 8> salt_bytes = ToLittleEndianBytes(salt);
  for (std::size_t i = 0; i < tag.size(); ++i) {
    std::uint64_t hash = kSaveTagSeeds[i];
    hash = Fnv1a64(hash, kSaveKey);
    hash = Fnv1a64(hash, salt_bytes);
    hash = Fnv1a64(hash, encoded);
    tag[i] = hash;
  }
  return tag;
}

std::string SaveTagToHex(const std::array<std::uint64_t, 4> &tag) {
  std::string result;
  result.reserve(tag.size() * 16);
  for (const std::uint64_t value : tag) {
    result += HexEncode64(value);
  }
  return result;
}

void AppendLine(std::vector<std::uint8_t> &bytes, const std::string_view line) {
  bytes.insert(bytes.end(), line.begin(), line.end());
  bytes.push_back('\n');
}

std::vector<std::uint8_t> BuildSavePayloadLocked() {
  std::vector<AchievementUnlock> unlocks;
  unlocks.reserve(g_unlocks.size());
  for (const auto &[key, unlock] : g_unlocks) {
    (void)key;
    unlocks.push_back(unlock);
  }
  std::ranges::sort(unlocks, {}, &AchievementUnlock::sequence);

  std::vector<std::uint8_t> payload;
  AppendLine(payload, kSavePayloadHeader);
  for (const AchievementUnlock &unlock : unlocks) {
    AppendLine(payload, std::to_string(unlock.user_index) + " " +
                            std::to_string(unlock.achievement_id) + " " +
                            std::to_string(unlock.sequence));
  }
  return payload;
}

void ParseSavePayloadLocked(const std::span<const std::uint8_t> payload) {
  const std::string payload_text(payload.begin(), payload.end());
  std::istringstream stream(payload_text);

  std::string line;
  if (!std::getline(stream, line) || line != kSavePayloadHeader) {
    return;
  }

  while (std::getline(stream, line)) {
    if (line.empty()) {
      continue;
    }

    std::istringstream line_stream(line);
    AchievementUnlock unlock;
    if (!(line_stream >> unlock.user_index >> unlock.achievement_id >>
          unlock.sequence)) {
      continue;
    }
    if (!unlock.achievement_id) {
      continue;
    }
    if (!unlock.sequence) {
      unlock.sequence = g_next_sequence++;
    }

    const std::uint64_t key =
        MakeUnlockKey(unlock.user_index, unlock.achievement_id);
    if (g_unlocks.contains(key)) {
      continue;
    }

    g_next_sequence = std::max(g_next_sequence, unlock.sequence + 1);
    g_unlocks.emplace(key, unlock);
  }
}

std::optional<std::string_view>
ValueAfterPrefix(const std::string_view line, const std::string_view prefix) {
  if (!line.starts_with(prefix)) {
    return std::nullopt;
  }
  return line.substr(prefix.size());
}

std::optional<std::uint64_t> ParseHex64(const std::string_view hex) {
  if (hex.size() != 16) {
    return std::nullopt;
  }

  std::uint64_t value = 0;
  for (const char ch : hex) {
    const std::optional<std::uint8_t> nibble = HexNibble(ch);
    if (!nibble) {
      return std::nullopt;
    }
    value = (value << 4) | *nibble;
  }
  return value;
}

void LoadAchievementSaveLocked() {
  if (g_save_loaded) {
    return;
  }
  g_save_loaded = true;

  std::ifstream file(AchievementSavePath(), std::ios::binary);
  if (!file.is_open()) {
    return;
  }

  std::string header;
  std::string salt_line;
  std::string payload_line;
  std::string tag_line;
  if (!std::getline(file, header) || !std::getline(file, salt_line) ||
      !std::getline(file, payload_line) || !std::getline(file, tag_line)) {
    return;
  }
  if (header != kSaveFileHeader) {
    return;
  }

  const std::optional<std::string_view> salt_hex =
      ValueAfterPrefix(salt_line, "salt=");
  const std::optional<std::string_view> payload_hex =
      ValueAfterPrefix(payload_line, "payload=");
  const std::optional<std::string_view> tag_hex =
      ValueAfterPrefix(tag_line, "tag=");
  if (!salt_hex || !payload_hex || !tag_hex) {
    return;
  }

  const std::optional<std::uint64_t> salt = ParseHex64(*salt_hex);
  const std::optional<std::vector<std::uint8_t>> encoded =
      HexDecode(*payload_hex);
  if (!salt || !encoded) {
    return;
  }

  const std::array<std::uint64_t, 4> expected_tag =
      MakeSaveTag(*salt, *encoded);
  if (SaveTagToHex(expected_tag) != *tag_hex) {
    return;
  }

  const std::vector<std::uint8_t> payload = XorWithSaveStream(*encoded, *salt);
  ParseSavePayloadLocked(payload);
}

void FlushAchievementSaveLocked() {
  const std::filesystem::path save_path = AchievementSavePath();
  std::error_code error;
  std::filesystem::create_directories(save_path.parent_path(), error);
  if (error) {
    return;
  }

  const std::uint64_t salt = MakeSaveSalt();
  const std::vector<std::uint8_t> payload = BuildSavePayloadLocked();
  const std::vector<std::uint8_t> encoded = XorWithSaveStream(payload, salt);
  const std::array<std::uint64_t, 4> tag = MakeSaveTag(salt, encoded);

  std::ofstream file(save_path, std::ios::binary | std::ios::trunc);
  if (!file.is_open()) {
    return;
  }

  file << kSaveFileHeader << '\n';
  file << "salt=" << HexEncode64(salt) << '\n';
  file << "payload=" << HexEncode(encoded) << '\n';
  file << "tag=" << SaveTagToHex(tag) << '\n';
}

} // namespace

void RecordAchievementWrites(const std::span<const AchievementWrite> writes) {
  if (writes.empty()) {
    return;
  }

  std::lock_guard lock(g_unlock_mutex);
  LoadAchievementSaveLocked();

  bool changed = false;
  for (const AchievementWrite &write : writes) {
    const std::uint64_t key =
        MakeUnlockKey(write.user_index, write.achievement_id);
    if (g_unlocks.contains(key)) {
      continue;
    }

    g_unlocks.emplace(key, AchievementUnlock{
                               write.user_index,
                               write.achievement_id,
                               g_next_sequence++,
                           });
    changed = true;
  }

  if (changed) {
    FlushAchievementSaveLocked();
  }
}

bool IsAchievementUnlocked(const std::uint32_t user_index,
                           const std::uint32_t achievement_id) {
  std::lock_guard lock(g_unlock_mutex);
  LoadAchievementSaveLocked();
  return g_unlocks.contains(MakeUnlockKey(user_index, achievement_id));
}

std::vector<AchievementUnlock> GetUnlockedAchievementsSnapshot() {
  std::vector<AchievementUnlock> unlocks;

  {
    std::lock_guard lock(g_unlock_mutex);
    LoadAchievementSaveLocked();
    unlocks.reserve(g_unlocks.size());
    for (const auto &[key, unlock] : g_unlocks) {
      (void)key;
      unlocks.push_back(unlock);
    }
  }

  std::ranges::sort(unlocks, {}, &AchievementUnlock::sequence);
  return unlocks;
}

void LoadAchievementSave() {
  std::lock_guard lock(g_unlock_mutex);
  LoadAchievementSaveLocked();
}

void FlushAchievementSave() {
  std::lock_guard lock(g_unlock_mutex);
  LoadAchievementSaveLocked();
  FlushAchievementSaveLocked();
}

void ClearAchievementSave() {
  std::lock_guard lock(g_unlock_mutex);
  g_unlocks.clear();
  g_next_sequence = 1;
  g_save_loaded = true;
  FlushAchievementSaveLocked();
}

void SetAchievementSavePath(std::filesystem::path save_path) {
  save_path = save_path.lexically_normal();

  std::lock_guard lock(g_unlock_mutex);
  if (save_path == g_save_path) {
    return;
  }

  g_save_path = std::move(save_path);
  g_unlocks.clear();
  g_next_sequence = 1;
  g_save_loaded = false;
}

void RegisterAchievementDefinition(const AchievementDefinition &definition) {
  if (!definition.achievement_id) {
    return;
  }

  std::lock_guard lock(g_unlock_mutex);
  g_definitions_by_achievement_id.try_emplace(definition.achievement_id,
                                              definition);
}

AchievementDefinition
GetAchievementDefinitionSnapshot(const std::uint32_t achievement_id) {
  std::lock_guard lock(g_unlock_mutex);
  const auto it = g_definitions_by_achievement_id.find(achievement_id);
  if (it == g_definitions_by_achievement_id.end()) {
    return {};
  }
  return it->second;
}

std::vector<AchievementDefinition> GetAchievementDefinitionsSnapshot() {
  std::vector<AchievementDefinition> definitions;

  {
    std::lock_guard lock(g_unlock_mutex);
    definitions.reserve(g_definitions_by_achievement_id.size());
    for (const auto &[achievement_id, definition] :
         g_definitions_by_achievement_id) {
      (void)achievement_id;
      definitions.push_back(definition);
    }
  }

  std::ranges::sort(definitions, {}, &AchievementDefinition::achievement_id);
  return definitions;
}

void RegisterAchievementIconPng(const std::uint32_t achievement_id,
                                const std::uint32_t image_id,
                                const std::span<const std::uint8_t> png_bytes) {
  if (!achievement_id || !image_id || png_bytes.empty()) {
    return;
  }

  std::lock_guard lock(g_unlock_mutex);
  if (g_icons_by_achievement_id.contains(achievement_id)) {
    return;
  }

  AchievementIconPng icon;
  icon.achievement_id = achievement_id;
  icon.image_id = image_id;
  icon.bytes.assign(png_bytes.begin(), png_bytes.end());

  g_icons_by_achievement_id.emplace(achievement_id, std::move(icon));
  g_achievement_id_by_image_id.emplace(image_id, achievement_id);
}

std::vector<std::uint8_t>
GetAchievementIconPngSnapshot(const std::uint32_t achievement_id) {
  std::lock_guard lock(g_unlock_mutex);
  const auto it = g_icons_by_achievement_id.find(achievement_id);
  if (it == g_icons_by_achievement_id.end()) {
    return {};
  }
  return it->second.bytes;
}

std::vector<std::uint8_t>
GetAchievementIconPngByImageIdSnapshot(const std::uint32_t image_id) {
  std::lock_guard lock(g_unlock_mutex);
  const auto image_it = g_achievement_id_by_image_id.find(image_id);
  if (image_it == g_achievement_id_by_image_id.end()) {
    return {};
  }

  const auto icon_it = g_icons_by_achievement_id.find(image_it->second);
  if (icon_it == g_icons_by_achievement_id.end()) {
    return {};
  }
  return icon_it->second.bytes;
}

} // namespace refii::achievement
