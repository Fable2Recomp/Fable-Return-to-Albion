#pragma once

// Fable 2 .gdb game-database codec (parse, serialize, CSV conversion).
//
// Format reverse-engineered from default.xex GDBFile_Parse (0x82A016C0),
// GDBEntry_FindPropWalkParents (0x821E5A08) and GDBNames_RegisterTable
// (0x82A01338). All integers are big-endian. Hashes are 32-bit FNV-1
// (basis 0x811C9DC5, prime 0x01000193).
//
// Container layout:
//   header (24B): 'GDB\0', u32 entryCount, u32 recordsSize, u32 schemasSize,
//                 u32 aliasCount, u32 reserved (always 0 in shipped data)
//   payload:      records | schemas |
//                 u32 ids[n] (sorted entry hashes) | s16 deltas[n] | pad(n&1) |
//                 (u32 aliasHash, u32 entryId)[aliasCount]
//   record:       u32 schemaOffset, then one u32 value per property in the
//                 schema's sorted-hash order
//   schema:       u32 (propCount << 8 | flags), propCount sorted prop-name
//                 hashes, propCount descriptors (typeByte << 24 | declOrder).
//                 Duplicate hashes represent array-valued properties.
//   record offset for entry i: 4 * (deltas[i] + (((recordsSize<<8)/n * i) >> 10))
//   optional tail (after payload, consumed by the runtime name registry):
//                 u32 0x00010000, u32 pairsSize, u32 nameCount,
//                 (u32 hash, cstring)[nameCount], u32 pairOffset[nameCount]
//
// This header is shared between the VFS runtime and the bank extractor, so
// it must stay free of engine/logging dependencies.

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace refii::vfs::gdb {

inline constexpr uint32_t kFnvBasis = 0x811C9DC5u;
inline constexpr uint32_t kFnvPrime = 0x01000193u;

// Property type codes as consumed by GDBEntry_GetPropTyped and the
// conversion shim at 0x8229CE90.
inline constexpr uint8_t kTypeBool = 0;
inline constexpr uint8_t kTypeEnum = 1;
inline constexpr uint8_t kTypeSymbol = 2;
inline constexpr uint8_t kTypeFloat = 3;
inline constexpr uint8_t kTypeString = 4;   // FNV-1 hash, resolved via names
inline constexpr uint8_t kTypeInt = 5;
inline constexpr uint8_t kTypeRef = 6;      // entry reference ("parent" links)
inline constexpr uint8_t kTypeLink = 7;     // interchangeable with kTypeRef
inline constexpr uint8_t kTypeAny = 8;

uint32_t Fnv1(std::string_view text);

struct GdbProperty {
  uint32_t name_hash = 0;
  uint8_t type = 0;
  uint32_t value = 0;
};

struct GdbEntry {
  uint32_t id = 0;
  uint32_t schema_flags = 0;
  // Declaration order (descriptor low 24 bits), not sorted-hash order.
  std::vector<GdbProperty> properties;
};

struct GdbDatabase {
  // Sorted by id after ParseGdb; SerializeGdb sorts a copy if needed.
  std::vector<GdbEntry> entries;
  // (aliasHash, targetEntryId) pairs.
  std::vector<std::pair<uint32_t, uint32_t>> aliases;
  // Hash -> string name table in file/first-use order.
  std::vector<std::pair<uint32_t, std::string>> names;
  std::unordered_map<uint32_t, size_t> name_index;

  // Registers text under its FNV-1 hash; returns false on a hash collision
  // with a different string.
  bool AddName(std::string_view text);
  const std::string* FindName(uint32_t hash) const;
};

bool ParseGdb(const uint8_t* data, size_t size, GdbDatabase* out,
              std::string* error);
bool SerializeGdb(const GdbDatabase& database, std::vector<uint8_t>* out,
                  std::string* error);

// CSV long format, one property per row: entry,property,type,value
//   - hashes resolve to names where known, otherwise "#xxxxxxxx"
//   - "@flags" rows carry non-zero schema flag bytes
//   - "@alias" rows carry the alias table
//   - "@name" rows preserve name-table strings not referenced by any row
bool ParseCsv(std::string_view text, GdbDatabase* out, std::string* error);
void SerializeCsv(const GdbDatabase& database, std::string* out);

bool LoadGdbFile(const std::filesystem::path& path, GdbDatabase* out,
                 std::string* error);
bool ConvertGdbFileToCsvFile(const std::filesystem::path& gdb_path,
                             const std::filesystem::path& csv_path,
                             std::string* error);
bool ConvertCsvFileToGdbFile(const std::filesystem::path& csv_path,
                             const std::filesystem::path& gdb_path,
                             std::string* error);

}  // namespace refii::vfs::gdb
