#pragma once

// Fable 2 .babel localized-text codec (parse, serialize, CSV conversion).
//
// Format reverse-engineered from default.xex Babel_ReadRecordsAndBlocks
// (0x8237E850), Babel_ParseSections (0x8237AF20) and TextManager_GetString
// (0x8237AA98). All integers big-endian; string payloads are UTF-16BE.
//
// Layout:
//   u32 magic 0x5B010000
//   u32 recordCount, records[(u32 key, u32 blockId, u32 offsetInBlock)]
//       sorted ascending by key. key = FNV-1 (basis 0x811C9DC5,
//       prime 0x01000193) of the TEXT_* key name -- the SAME hash a .gdb
//       type-4 string value carries, so gdb values resolve into babel.
//   u32 blockCount, blocks[(u32 blockId, u32 compSize, u32 decompSize,
//       zlib data[compSize])]. decompSize <= 18022 (runtime scratch buffer).
//       blockId is an arbitrary join handle: the loader maps blockId -> block
//       then rewrites each record's blockId field to the block pointer.
//   u32 scratchBlockSize (0x4000 in shipped data; preserved verbatim)
//   u32 n, speechLinks[(u32 textKey, u32 speechId, u16 flag)][n]
//   u32 n, textGroups[(u32 groupKey, u32 memberCount, u32 members[])][n]
//   u32 n, speakerNames[(u32 key, u32 charCount, UTF-16BE chars)][n]
//   String payload within a block: u32 charCount + UTF-16BE chars.
//
// Depends on zlib; kept free of engine/logging dependencies so the VFS
// runtime and the bank extractor can share it.

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace refii::vfs::babel {

inline constexpr uint32_t kFnvBasis = 0x811C9DC5u;
inline constexpr uint32_t kFnvPrime = 0x01000193u;
inline constexpr uint32_t kBabelMagic = 0x5B010000u;

uint32_t Fnv1(std::string_view text);

struct BabelString {
  uint32_t key = 0;
  std::u16string text;
};

struct BabelSpeechLink {
  uint32_t text_key = 0;
  uint32_t speech_id = 0;
  uint16_t flag = 0;
};

struct BabelTextGroup {
  uint32_t key = 0;
  std::vector<uint32_t> members;
};

struct BabelSpeaker {
  uint32_t key = 0;
  std::u16string name;
};

struct BabelDatabase {
  std::vector<BabelString> strings;  // sorted by key after ParseBabel
  std::vector<BabelSpeechLink> speech_links;
  std::vector<BabelTextGroup> text_groups;
  std::vector<BabelSpeaker> speakers;
  uint32_t scratch_block_size = 0x4000;

  // Optional hash -> TEXT_* name table, used only to render readable CSV keys
  // and to accept named keys on the way back in.
  std::unordered_map<uint32_t, std::string> names;

  void AddName(std::string_view text);
  const std::string* FindName(uint32_t hash) const;
};

bool ParseBabel(const uint8_t* data, size_t size, BabelDatabase* out,
                std::string* error);
bool SerializeBabel(const BabelDatabase& database, std::vector<uint8_t>* out,
                    std::string* error);

// CSV rows: key,kind,value
//   kind "text"    -> value is the localized string (UTF-8)
//   kind "speech"  -> value is "<speechKey> <flag>"
//   kind "group"   -> value is space-separated member keys
//   kind "speaker" -> value is the speaker tag (UTF-8)
// Keys resolve to TEXT_* names when known, otherwise "#xxxxxxxx".
bool ParseCsv(std::string_view text, BabelDatabase* out, std::string* error);
void SerializeCsv(const BabelDatabase& database, std::string* out);

bool LoadBabelFile(const std::filesystem::path& path, BabelDatabase* out,
                   std::string* error);
bool ConvertBabelFileToCsvFile(const std::filesystem::path& babel_path,
                               const std::filesystem::path& csv_path,
                               std::string* error);
bool ConvertCsvFileToBabelFile(const std::filesystem::path& csv_path,
                               const std::filesystem::path& babel_path,
                               std::string* error);

}  // namespace refii::vfs::babel
