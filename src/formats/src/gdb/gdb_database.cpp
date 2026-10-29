#include "gdb/gdb_database.h"

#include <algorithm>
#include <cctype>
#include <cinttypes>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <numeric>
#include <unordered_set>

namespace refii::vfs::gdb {
namespace {

// The name registry a fresh guest starts with (sub_82A01488: 0x10000 slots);
// the name-table tail's first word is the capacity the names were laid out in.
constexpr uint32_t kNameRegistryInitialCapacity = 0x00010000u;
constexpr size_t kHeaderSize = 24;
constexpr uint32_t kHashParent = 0x5F6317D5u;  // Fnv1("parent")

uint32_t ReadBe32(const uint8_t* data) {
  return (static_cast<uint32_t>(data[0]) << 24) |
         (static_cast<uint32_t>(data[1]) << 16) |
         (static_cast<uint32_t>(data[2]) << 8) | static_cast<uint32_t>(data[3]);
}

int16_t ReadBe16(const uint8_t* data) {
  return static_cast<int16_t>((static_cast<uint16_t>(data[0]) << 8) |
                              static_cast<uint16_t>(data[1]));
}

void AppendBe32(std::vector<uint8_t>* out, uint32_t value) {
  out->push_back(static_cast<uint8_t>(value >> 24));
  out->push_back(static_cast<uint8_t>(value >> 16));
  out->push_back(static_cast<uint8_t>(value >> 8));
  out->push_back(static_cast<uint8_t>(value));
}

void AppendBe16(std::vector<uint8_t>* out, uint16_t value) {
  out->push_back(static_cast<uint8_t>(value >> 8));
  out->push_back(static_cast<uint8_t>(value));
}

bool Fail(std::string* error, std::string message) {
  if (error != nullptr) {
    *error = std::move(message);
  }
  return false;
}

std::string_view TypeToken(uint8_t type) {
  switch (type) {
    case kTypeBool: return "bool";
    case kTypeEnum: return "enum";
    case kTypeSymbol: return "sym";
    case kTypeFloat: return "float";
    case kTypeString: return "string";
    case kTypeInt: return "int";
    case kTypeRef: return "ref";
    case kTypeLink: return "link";
    case kTypeAny: return "any";
    default: return {};
  }
}

bool TokenToType(std::string_view token, uint8_t* out) {
  for (uint8_t type = 0; type <= kTypeAny; ++type) {
    if (TypeToken(type) == token) {
      *out = type;
      return true;
    }
  }
  if (token.size() >= 2 && token.front() == 't') {
    unsigned long value = 0;
    const std::string digits(token.substr(1));
    char* end = nullptr;
    value = std::strtoul(digits.c_str(), &end, 10);
    if (end != nullptr && *end == '\0' && value <= 0xFF) {
      *out = static_cast<uint8_t>(value);
      return true;
    }
  }
  return false;
}

std::string FormatTypeToken(uint8_t type) {
  const std::string_view token = TypeToken(type);
  if (!token.empty()) {
    return std::string(token);
  }
  char buffer[8];
  std::snprintf(buffer, sizeof(buffer), "t%u", type);
  return buffer;
}

std::string FormatHashLiteral(uint32_t hash) {
  char buffer[16];
  std::snprintf(buffer, sizeof(buffer), "#%08x", hash);
  return buffer;
}

std::string FormatRawHex(uint32_t value) {
  char buffer[16];
  std::snprintf(buffer, sizeof(buffer), "0x%08x", value);
  return buffer;
}

bool IsHashLiteral(std::string_view text, uint32_t* out) {
  if (text.size() != 9 || text.front() != '#') {
    return false;
  }
  uint32_t value = 0;
  for (char ch : text.substr(1)) {
    const char lowered = static_cast<char>(std::tolower(
        static_cast<unsigned char>(ch)));
    uint32_t digit = 0;
    if (lowered >= '0' && lowered <= '9') {
      digit = static_cast<uint32_t>(lowered - '0');
    } else if (lowered >= 'a' && lowered <= 'f') {
      digit = static_cast<uint32_t>(lowered - 'a' + 10);
    } else {
      return false;
    }
    value = (value << 4) | digit;
  }
  if (out != nullptr) {
    *out = value;
  }
  return true;
}

// Shortest %g form that reparses to the same float bits; raw hex for
// non-finite values. Mirrored by tools/gdb2csv.py.
std::string FormatFloat(uint32_t bits) {
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  if (!std::isfinite(value)) {
    return FormatRawHex(bits);
  }
  char buffer[64];
  for (int precision = 1; precision <= 9; ++precision) {
    std::snprintf(buffer, sizeof(buffer), "%.*g", precision,
                  static_cast<double>(value));
    const float reparsed = std::strtof(buffer, nullptr);
    uint32_t reparsed_bits = 0;
    std::memcpy(&reparsed_bits, &reparsed, sizeof(reparsed_bits));
    if (reparsed_bits == bits) {
      break;
    }
  }
  return buffer;
}

void AppendCsvField(std::string* out, std::string_view field) {
  const bool needs_quotes =
      field.find_first_of(",\"\n\r") != std::string_view::npos;
  if (!needs_quotes) {
    out->append(field);
    return;
  }
  out->push_back('"');
  for (char ch : field) {
    if (ch == '"') {
      out->push_back('"');
    }
    out->push_back(ch);
  }
  out->push_back('"');
}

void AppendCsvRow(std::string* out, std::string_view entry,
                  std::string_view property, std::string_view type,
                  std::string_view value) {
  AppendCsvField(out, entry);
  out->push_back(',');
  AppendCsvField(out, property);
  out->push_back(',');
  AppendCsvField(out, type);
  out->push_back(',');
  AppendCsvField(out, value);
  out->push_back('\n');
}

// Splits one CSV record (handling quotes) starting at `position`; returns
// false on malformed quoting. Advances `position` past the record.
bool ParseCsvRecord(std::string_view text, size_t* position,
                    std::vector<std::string>* fields) {
  fields->clear();
  std::string current;
  bool in_quotes = false;
  bool any = false;
  size_t i = *position;
  for (; i < text.size(); ++i) {
    const char ch = text[i];
    any = true;
    if (in_quotes) {
      if (ch == '"') {
        if (i + 1 < text.size() && text[i + 1] == '"') {
          current.push_back('"');
          ++i;
        } else {
          in_quotes = false;
        }
      } else {
        current.push_back(ch);
      }
      continue;
    }
    if (ch == '"' && current.empty()) {
      in_quotes = true;
      continue;
    }
    if (ch == ',') {
      fields->push_back(std::move(current));
      current.clear();
      continue;
    }
    if (ch == '\n' || ch == '\r') {
      while (i < text.size() && (text[i] == '\n' || text[i] == '\r')) {
        ++i;
      }
      break;
    }
    current.push_back(ch);
  }
  *position = i;
  if (in_quotes) {
    return false;
  }
  if (any) {
    fields->push_back(std::move(current));
  }
  return true;
}

struct SchemaLayout {
  // Declared property k -> index into GdbEntry::properties: "parent" first
  // (the guest's enumeration skips declared index 0 of an entry with one),
  // then the entry's own order.
  std::vector<uint32_t> declared;
  // Sorted slot s -> declared k, ordered by (hash, declaration order).
  std::vector<uint32_t> sorted_order;
  // Declared k -> sorted slot: descriptor k's low 24 bits.
  std::vector<uint32_t> slot_of;

  const GdbProperty& AtSlot(const GdbEntry& entry, uint32_t slot) const {
    return entry.properties[declared[sorted_order[slot]]];
  }
};

SchemaLayout BuildSchemaLayout(const GdbEntry& entry) {
  SchemaLayout layout;
  const uint32_t count = static_cast<uint32_t>(entry.properties.size());
  layout.declared.reserve(count);
  for (uint32_t i = 0; i < count; ++i) {
    if (entry.properties[i].name_hash == kHashParent) {
      layout.declared.push_back(i);
      break;
    }
  }
  for (uint32_t i = 0; i < count; ++i) {
    if (layout.declared.empty() || layout.declared.front() != i) {
      layout.declared.push_back(i);
    }
  }
  layout.sorted_order.resize(count);
  std::iota(layout.sorted_order.begin(), layout.sorted_order.end(), 0u);
  std::stable_sort(layout.sorted_order.begin(), layout.sorted_order.end(),
                   [&entry, &layout](uint32_t left, uint32_t right) {
                     return entry.properties[layout.declared[left]].name_hash <
                            entry.properties[layout.declared[right]].name_hash;
                   });
  layout.slot_of.resize(count);
  for (uint32_t slot = 0; slot < count; ++slot) {
    layout.slot_of[layout.sorted_order[slot]] = slot;
  }
  return layout;
}

// The guest's name registry (sub_82A01488 / sub_82A01288 / sub_829FF8A0):
// open addressing, slot = hash & (capacity - 1), linear probe, doubled when
// the count reaches 3/4 of the capacity (old slots re-inserted in slot order).
// Returns the capacity and the pair indices in slot order, which is what the
// name-table tail stores and GDBNames_RegisterTable walks linearly.
void NameRegistryLayout(const std::vector<uint32_t>& hashes,
                        uint32_t* capacity_out, std::vector<uint32_t>* order) {
  uint32_t capacity = kNameRegistryInitialCapacity;
  std::vector<int64_t> slots(capacity, -1);
  const auto find = [&](uint32_t hash) {
    uint32_t s = hash & (capacity - 1);
    while (slots[s] >= 0) {
      if (hashes[static_cast<size_t>(slots[s])] == hash) {
        return true;
      }
      s = (s + 1) & (capacity - 1);
    }
    return false;
  };
  const auto insert = [&](std::vector<int64_t>& table, uint32_t cap,
                          uint32_t index) {
    const uint32_t hash = hashes[index];
    uint32_t s = hash & (cap - 1);
    while (table[s] >= 0 && hashes[static_cast<size_t>(table[s])] != hash) {
      s = (s + 1) & (cap - 1);
    }
    table[s] = index;
  };
  uint32_t count = 0;
  for (uint32_t i = 0; i < hashes.size(); ++i) {
    if (find(hashes[i])) {
      continue;
    }
    ++count;
    if (count == ((3u * capacity) >> 2)) {
      const uint32_t grown = capacity << 1;
      std::vector<int64_t> next(grown, -1);
      for (uint32_t s = 0; s < capacity; ++s) {
        if (slots[s] >= 0) {
          insert(next, grown, static_cast<uint32_t>(slots[s]));
        }
      }
      slots.swap(next);
      capacity = grown;
    }
    insert(slots, capacity, i);
  }
  order->clear();
  order->reserve(count);
  for (uint32_t s = 0; s < capacity; ++s) {
    if (slots[s] >= 0) {
      order->push_back(static_cast<uint32_t>(slots[s]));
    }
  }
  *capacity_out = capacity;
}

}  // namespace

static bool ParseGdbImpl(const uint8_t* data, size_t size, GdbDatabase* out,
                         std::string* error);
static bool SerializeGdbImpl(const GdbDatabase& database,
                             std::vector<uint8_t>* out, std::string* error);
static bool ParseCsvImpl(std::string_view text, GdbDatabase* out,
                         std::string* error);

uint32_t Fnv1(std::string_view text) {
  uint32_t hash = kFnvBasis;
  for (char ch : text) {
    hash *= kFnvPrime;
    hash ^= static_cast<uint8_t>(ch);
  }
  return hash;
}

bool GdbDatabase::AddName(std::string_view text) {
  const uint32_t hash = Fnv1(text);
  const auto existing = name_index.find(hash);
  if (existing != name_index.end()) {
    return names[existing->second].second == text;
  }
  name_index.emplace(hash, names.size());
  names.emplace_back(hash, std::string(text));
  return true;
}

const std::string* GdbDatabase::FindName(uint32_t hash) const {
  const auto match = name_index.find(hash);
  if (match == name_index.end()) {
    return nullptr;
  }
  return &names[match->second].second;
}

bool ParseGdb(const uint8_t* data, size_t size, GdbDatabase* out,
              std::string* error) {
  // These parsers run inside the game process on untrusted bytes; no
  // exception (bad_alloc included) may escape.
  try {
    return ParseGdbImpl(data, size, out, error);
  } catch (const std::exception& exception) {
    return Fail(error, std::string("gdb ParseGdb exception: ") + exception.what());
  } catch (...) {
    return Fail(error, "gdb ParseGdb: unknown exception");
  }
}

static bool ParseGdbImpl(const uint8_t* data, size_t size, GdbDatabase* out,
              std::string* error) {
  *out = GdbDatabase{};
  if (size < kHeaderSize || std::memcmp(data, "GDB\0", 4) != 0) {
    return Fail(error, "not a GDB file (bad magic or truncated header)");
  }
  const uint64_t entry_count = ReadBe32(data + 4);
  const uint64_t records_size = ReadBe32(data + 8);
  const uint64_t schemas_size = ReadBe32(data + 12);
  const uint64_t alias_count = ReadBe32(data + 16);
  const uint64_t reserved = ReadBe32(data + 20);
  if (reserved != 0) {
    return Fail(error, "unsupported GDB: reserved table count is non-zero");
  }
  const uint64_t payload_size =
      records_size + schemas_size +
      2 * (3 * entry_count + (entry_count & 1) + 4 * alias_count);
  if (kHeaderSize + payload_size > size) {
    return Fail(error, "truncated GDB payload");
  }
  const uint8_t* records = data + kHeaderSize;
  const uint8_t* schemas = records + records_size;
  const uint8_t* ids = schemas + schemas_size;
  const uint8_t* deltas = ids + 4 * entry_count;
  const uint8_t* aliases = data + kHeaderSize + payload_size - 8 * alias_count;
  const uint64_t stride =
      entry_count != 0 ? ((records_size << 8) / entry_count) : 0;

  out->entries.resize(entry_count);
  for (uint64_t i = 0; i < entry_count; ++i) {
    GdbEntry& entry = out->entries[i];
    entry.id = ReadBe32(ids + 4 * i);
    const int64_t record_offset =
        4 * (ReadBe16(deltas + 2 * i) +
             static_cast<int64_t>((stride * i) >> 10));
    if (record_offset < 0 ||
        static_cast<uint64_t>(record_offset) + 4 > records_size) {
      return Fail(error, "record offset out of range for entry " +
                             FormatHashLiteral(entry.id));
    }
    const uint32_t schema_offset = ReadBe32(records + record_offset);
    if (static_cast<uint64_t>(schema_offset) + 4 > schemas_size) {
      return Fail(error, "schema offset out of range for entry " +
                             FormatHashLiteral(entry.id));
    }
    const uint8_t* schema = schemas + schema_offset;
    const uint32_t schema_word = ReadBe32(schema);
    const uint32_t property_count = schema_word >> 8;
    entry.schema_flags = schema_word & 0xFFu;
    if (static_cast<uint64_t>(schema_offset) + 4 + 8 * property_count >
            schemas_size ||
        static_cast<uint64_t>(record_offset) + 4 + 4 * property_count >
            records_size) {
      return Fail(error, "schema or record overruns block for entry " +
                             FormatHashLiteral(entry.id));
    }
    // Declared property k sits at sorted slot D[k] & 0xFFFFFF (sub_82A008F8 /
    // sub_82A007C0); the type of slot s is D[s] >> 24 (0x821E5AA0).
    entry.properties.resize(property_count);
    std::vector<bool> slot_taken(property_count, false);
    for (uint32_t k = 0; k < property_count; ++k) {
      const uint32_t descriptor =
          ReadBe32(schema + 4 + 4 * property_count + 4 * k);
      const uint32_t slot = descriptor & 0xFFFFFFu;
      if (slot >= property_count || slot_taken[slot]) {
        return Fail(error, "invalid sorted-slot index in schema of entry " +
                               FormatHashLiteral(entry.id));
      }
      slot_taken[slot] = true;
      const uint32_t slot_descriptor =
          ReadBe32(schema + 4 + 4 * property_count + 4 * slot);
      GdbProperty& property = entry.properties[k];
      property.name_hash = ReadBe32(schema + 4 + 4 * slot);
      property.type = static_cast<uint8_t>(slot_descriptor >> 24);
      property.value = ReadBe32(records + record_offset + 4 + 4 * slot);
    }
  }

  out->aliases.reserve(alias_count);
  for (uint64_t i = 0; i < alias_count; ++i) {
    out->aliases.emplace_back(ReadBe32(aliases + 8 * i),
                              ReadBe32(aliases + 8 * i + 4));
  }

  // Optional name table tail.
  const uint8_t* tail = data + kHeaderSize + payload_size;
  const size_t tail_size = size - kHeaderSize - payload_size;
  // The tail's first word is the registry capacity the names were laid out
  // in: a power of two (the guest masks with capacity - 1).
  const uint32_t capacity = tail_size >= 12 ? ReadBe32(tail) : 0u;
  if (tail_size >= 12 && capacity != 0 && (capacity & (capacity - 1)) == 0) {
    const uint64_t pairs_size = ReadBe32(tail + 4);
    const uint64_t name_count = ReadBe32(tail + 8);
    if (12 + pairs_size + 4 * name_count > tail_size) {
      return Fail(error, "truncated GDB name table");
    }
    const uint8_t* pairs = tail + 12;
    uint64_t position = 0;
    for (uint64_t i = 0; i < name_count; ++i) {
      if (position + 5 > pairs_size) {
        return Fail(error, "GDB name table overruns its block");
      }
      const uint32_t hash = ReadBe32(pairs + position);
      const uint8_t* text = pairs + position + 4;
      const uint8_t* text_end = static_cast<const uint8_t*>(
          std::memchr(text, 0, pairs_size - position - 4));
      if (text_end == nullptr) {
        return Fail(error, "unterminated string in GDB name table");
      }
      std::string_view name(reinterpret_cast<const char*>(text),
                            static_cast<size_t>(text_end - text));
      if (Fnv1(name) != hash) {
        return Fail(error, "GDB name table hash mismatch for '" +
                               std::string(name) + "'");
      }
      out->AddName(name);
      position = static_cast<uint64_t>(text_end - pairs) + 1;
    }
  }
  return true;
}

bool SerializeGdb(const GdbDatabase& database, std::vector<uint8_t>* out,
                  std::string* error) {
  // These parsers run inside the game process on untrusted bytes; no
  // exception (bad_alloc included) may escape.
  try {
    return SerializeGdbImpl(database, out, error);
  } catch (const std::exception& exception) {
    return Fail(error, std::string("gdb SerializeGdb exception: ") + exception.what());
  } catch (...) {
    return Fail(error, "gdb SerializeGdb: unknown exception");
  }
}

static bool SerializeGdbImpl(const GdbDatabase& database, std::vector<uint8_t>* out,
                  std::string* error) {
  out->clear();
  std::vector<uint32_t> order(database.entries.size());
  std::iota(order.begin(), order.end(), 0u);
  std::stable_sort(order.begin(), order.end(),
                   [&database](uint32_t left, uint32_t right) {
                     return database.entries[left].id <
                            database.entries[right].id;
                   });
  for (size_t i = 1; i < order.size(); ++i) {
    if (database.entries[order[i - 1]].id == database.entries[order[i]].id) {
      return Fail(error, "duplicate entry id " +
                             FormatHashLiteral(database.entries[order[i]].id));
    }
  }

  std::vector<uint8_t> records;
  std::vector<uint8_t> schemas;
  std::vector<uint64_t> record_offsets(order.size(), 0);
  std::map<std::string, uint32_t> schema_offsets;
  for (size_t i = 0; i < order.size(); ++i) {
    const GdbEntry& entry = database.entries[order[i]];
    if (entry.properties.size() >= (1u << 24) || entry.schema_flags > 0xFF) {
      return Fail(error, "entry " + FormatHashLiteral(entry.id) +
                             " has too many properties or invalid flags");
    }
    const SchemaLayout layout = BuildSchemaLayout(entry);
    const uint32_t count = static_cast<uint32_t>(entry.properties.size());
    std::string key;
    key.push_back(static_cast<char>(entry.schema_flags));
    for (uint32_t slot = 0; slot < count; ++slot) {
      const GdbProperty& property = layout.AtSlot(entry, slot);
      key.append(reinterpret_cast<const char*>(&property.name_hash),
                 sizeof(property.name_hash));
      key.push_back(static_cast<char>(property.type));
      key.append(reinterpret_cast<const char*>(&layout.slot_of[slot]),
                 sizeof(uint32_t));
    }
    auto [match, inserted] =
        schema_offsets.try_emplace(std::move(key),
                                   static_cast<uint32_t>(schemas.size()));
    if (inserted) {
      AppendBe32(&schemas, (count << 8) | entry.schema_flags);
      for (uint32_t slot = 0; slot < count; ++slot) {
        AppendBe32(&schemas, layout.AtSlot(entry, slot).name_hash);
      }
      // Descriptor k: (type of sorted slot k) << 24 | sorted slot of declared k.
      for (uint32_t k = 0; k < count; ++k) {
        AppendBe32(&schemas,
                   (static_cast<uint32_t>(layout.AtSlot(entry, k).type) << 24) |
                       layout.slot_of[k]);
      }
    }
    record_offsets[i] = records.size();
    AppendBe32(&records, match->second);
    for (uint32_t slot = 0; slot < count; ++slot) {
      AppendBe32(&records, layout.AtSlot(entry, slot).value);
    }
  }

  const uint64_t entry_count = order.size();
  const uint64_t records_size = records.size();
  const uint64_t stride =
      entry_count != 0 ? ((records_size << 8) / entry_count) : 0;
  std::vector<int16_t> deltas(entry_count, 0);
  for (uint64_t i = 0; i < entry_count; ++i) {
    const int64_t delta = static_cast<int64_t>(record_offsets[i] / 4) -
                          static_cast<int64_t>((stride * i) >> 10);
    if (delta < INT16_MIN || delta > INT16_MAX) {
      return Fail(error,
                  "record layout exceeds the s16 interpolation delta range "
                  "(entry " +
                      FormatHashLiteral(database.entries[order[i]].id) + ")");
    }
    deltas[i] = static_cast<int16_t>(delta);
  }

  std::vector<std::pair<uint32_t, uint32_t>> aliases = database.aliases;
  std::sort(aliases.begin(), aliases.end());

  out->reserve(kHeaderSize + records.size() + schemas.size() +
               8 * entry_count + 8 * aliases.size() + 64);
  out->insert(out->end(), {'G', 'D', 'B', '\0'});
  AppendBe32(out, static_cast<uint32_t>(entry_count));
  AppendBe32(out, static_cast<uint32_t>(records_size));
  AppendBe32(out, static_cast<uint32_t>(schemas.size()));
  AppendBe32(out, static_cast<uint32_t>(aliases.size()));
  AppendBe32(out, 0);
  out->insert(out->end(), records.begin(), records.end());
  out->insert(out->end(), schemas.begin(), schemas.end());
  for (uint64_t i = 0; i < entry_count; ++i) {
    AppendBe32(out, database.entries[order[i]].id);
  }
  for (int16_t delta : deltas) {
    AppendBe16(out, static_cast<uint16_t>(delta));
  }
  if (entry_count & 1) {
    AppendBe16(out, 0);
  }
  for (const auto& [alias, target] : aliases) {
    AppendBe32(out, alias);
    AppendBe32(out, target);
  }

  // Name table tail: the pairs, then the registry's capacity and its slot
  // order (NameRegistryLayout) as the offsets array.
  std::vector<uint8_t> pairs;
  std::vector<uint32_t> pair_offsets;
  std::vector<uint32_t> pair_hashes;
  pair_offsets.reserve(database.names.size());
  pair_hashes.reserve(database.names.size());
  for (const auto& [hash, name] : database.names) {
    pair_offsets.push_back(static_cast<uint32_t>(pairs.size()));
    pair_hashes.push_back(hash);
    AppendBe32(&pairs, hash);
    pairs.insert(pairs.end(), name.begin(), name.end());
    pairs.push_back(0);
  }
  uint32_t capacity = kNameRegistryInitialCapacity;
  std::vector<uint32_t> slot_order;
  NameRegistryLayout(pair_hashes, &capacity, &slot_order);
  AppendBe32(out, capacity);
  AppendBe32(out, static_cast<uint32_t>(pairs.size()));
  AppendBe32(out, static_cast<uint32_t>(pair_offsets.size()));
  out->insert(out->end(), pairs.begin(), pairs.end());
  for (uint32_t pair : slot_order) {
    AppendBe32(out, pair_offsets[pair]);
  }
  return true;
}

void SerializeCsv(const GdbDatabase& database, std::string* out) {
  out->clear();
  out->append("entry,property,type,value\n");
  std::unordered_set<uint32_t> referenced;

  const auto key_text = [&](uint32_t hash) {
    const std::string* name = database.FindName(hash);
    if (name != nullptr && !IsHashLiteral(*name, nullptr)) {
      referenced.insert(hash);
      return *name;
    }
    return FormatHashLiteral(hash);
  };

  for (const GdbEntry& entry : database.entries) {
    const std::string entry_key = FormatHashLiteral(entry.id);
    if (entry.schema_flags != 0 || entry.properties.empty()) {
      // The flags row also materializes entries that have no properties.
      AppendCsvRow(out, entry_key, "@flags", "int",
                   std::to_string(entry.schema_flags));
    }
    for (const GdbProperty& property : entry.properties) {
      std::string value;
      switch (property.type) {
        case kTypeBool:
        case kTypeEnum:
        case kTypeInt:
          value = std::to_string(property.value);
          break;
        case kTypeFloat:
          value = FormatFloat(property.value);
          break;
        case kTypeString:
        case kTypeRef:
        case kTypeLink:
          value = key_text(property.value);
          break;
        default:
          value = FormatRawHex(property.value);
          break;
      }
      AppendCsvRow(out, entry_key, key_text(property.name_hash),
                   FormatTypeToken(property.type), value);
    }
  }
  for (const auto& [alias, target] : database.aliases) {
    AppendCsvRow(out, key_text(alias), "@alias", "ref", key_text(target));
  }
  for (const auto& [hash, name] : database.names) {
    if (!referenced.contains(hash)) {
      AppendCsvRow(out, "", "@name", "string", name);
    }
  }
}

bool ParseCsv(std::string_view text, GdbDatabase* out, std::string* error) {
  // These parsers run inside the game process on untrusted bytes; no
  // exception (bad_alloc included) may escape.
  try {
    return ParseCsvImpl(text, out, error);
  } catch (const std::exception& exception) {
    return Fail(error, std::string("gdb ParseCsv exception: ") + exception.what());
  } catch (...) {
    return Fail(error, "gdb ParseCsv: unknown exception");
  }
}

static bool ParseCsvImpl(std::string_view text, GdbDatabase* out, std::string* error) {
  *out = GdbDatabase{};
  // Tolerate the UTF-8 BOM that Windows CSV editors prepend.
  if (text.starts_with("\xEF\xBB\xBF")) {
    text.remove_prefix(3);
  }
  size_t position = 0;
  size_t line = 0;
  std::vector<std::string> fields;
  std::unordered_set<uint32_t> seen_entries;
  GdbEntry* current = nullptr;
  std::string current_key;

  const auto fail_line = [&](const std::string& message) {
    return Fail(error, "CSV line " + std::to_string(line) + ": " + message);
  };
  const auto parse_key = [out](const std::string& field, uint32_t* hash) {
    if (IsHashLiteral(field, hash)) {
      return true;
    }
    if (field.empty()) {
      return false;
    }
    *hash = Fnv1(field);
    return out->AddName(field);
  };

  while (position < text.size()) {
    ++line;
    if (!ParseCsvRecord(text, &position, &fields)) {
      return fail_line("unterminated quoted field");
    }
    if (fields.empty() || (fields.size() == 1 && fields[0].empty())) {
      continue;
    }
    if (line == 1 && fields.size() >= 2 && fields[0] == "entry" &&
        fields[1] == "property") {
      continue;
    }
    if (fields.size() != 4) {
      return fail_line("expected 4 columns, got " +
                       std::to_string(fields.size()));
    }
    const std::string& entry_field = fields[0];
    const std::string& property_field = fields[1];
    const std::string& type_field = fields[2];
    const std::string& value_field = fields[3];

    if (property_field == "@name") {
      if (!out->AddName(value_field)) {
        return fail_line("name hash collision for '" + value_field + "'");
      }
      continue;
    }
    if (property_field == "@alias") {
      uint32_t alias = 0;
      uint32_t target = 0;
      if (!parse_key(entry_field, &alias) || !parse_key(value_field, &target)) {
        return fail_line("invalid alias row");
      }
      out->aliases.emplace_back(alias, target);
      continue;
    }

    uint32_t entry_id = 0;
    if (!parse_key(entry_field, &entry_id)) {
      return fail_line("invalid entry key '" + entry_field + "'");
    }
    if (current == nullptr || entry_field != current_key) {
      if (!seen_entries.insert(entry_id).second) {
        return fail_line("entry '" + entry_field +
                         "' appears in more than one row block");
      }
      out->entries.emplace_back();
      current = &out->entries.back();
      current->id = entry_id;
      current_key = entry_field;
    }

    if (property_field == "@flags") {
      const unsigned long flags = std::strtoul(value_field.c_str(), nullptr, 0);
      if (flags > 0xFF) {
        return fail_line("@flags out of range");
      }
      current->schema_flags = static_cast<uint32_t>(flags);
      continue;
    }

    GdbProperty property;
    if (!parse_key(property_field, &property.name_hash)) {
      return fail_line("invalid property key '" + property_field + "'");
    }
    if (!TokenToType(type_field, &property.type)) {
      return fail_line("unknown type token '" + type_field + "'");
    }
    switch (property.type) {
      case kTypeBool:
      case kTypeEnum:
      case kTypeInt: {
        char* end = nullptr;
        property.value =
            static_cast<uint32_t>(std::strtoul(value_field.c_str(), &end, 0));
        if (end == value_field.c_str() || *end != '\0') {
          if (value_field == "true") {
            property.value = 1;
          } else if (value_field == "false") {
            property.value = 0;
          } else {
            return fail_line("invalid integer value '" + value_field + "'");
          }
        }
        break;
      }
      case kTypeFloat: {
        if (value_field.starts_with("0x")) {
          property.value = static_cast<uint32_t>(
              std::strtoul(value_field.c_str(), nullptr, 16));
        } else {
          char* end = nullptr;
          const float value = std::strtof(value_field.c_str(), &end);
          if (end == value_field.c_str() || *end != '\0') {
            return fail_line("invalid float value '" + value_field + "'");
          }
          std::memcpy(&property.value, &value, sizeof(property.value));
        }
        break;
      }
      case kTypeString:
      case kTypeRef:
      case kTypeLink: {
        if (value_field.empty()) {
          property.value = Fnv1("");
          out->AddName("");
        } else if (!parse_key(value_field, &property.value)) {
          return fail_line("invalid symbol value '" + value_field + "'");
        }
        break;
      }
      default: {
        char* end = nullptr;
        property.value =
            static_cast<uint32_t>(std::strtoul(value_field.c_str(), &end, 0));
        if (end == value_field.c_str() || *end != '\0') {
          return fail_line("invalid raw value '" + value_field + "'");
        }
        break;
      }
    }
    current->properties.push_back(property);
  }
  return true;
}

bool LoadGdbFile(const std::filesystem::path& path, GdbDatabase* out,
                 std::string* error) {
  std::ifstream stream(path, std::ios::binary);
  if (!stream) {
    return Fail(error, "cannot open " + path.generic_string());
  }
  std::vector<uint8_t> data((std::istreambuf_iterator<char>(stream)),
                            std::istreambuf_iterator<char>());
  return ParseGdb(data.data(), data.size(), out, error);
}

bool ConvertGdbFileToCsvFile(const std::filesystem::path& gdb_path,
                             const std::filesystem::path& csv_path,
                             std::string* error) {
  GdbDatabase database;
  if (!LoadGdbFile(gdb_path, &database, error)) {
    return false;
  }
  std::string csv;
  SerializeCsv(database, &csv);
  std::ofstream stream(csv_path, std::ios::binary | std::ios::trunc);
  if (!stream.write(csv.data(), static_cast<std::streamsize>(csv.size()))) {
    return Fail(error, "cannot write " + csv_path.generic_string());
  }
  return true;
}

bool ConvertCsvFileToGdbFile(const std::filesystem::path& csv_path,
                             const std::filesystem::path& gdb_path,
                             std::string* error) {
  std::ifstream stream(csv_path, std::ios::binary);
  if (!stream) {
    return Fail(error, "cannot open " + csv_path.generic_string());
  }
  const std::string text((std::istreambuf_iterator<char>(stream)),
                         std::istreambuf_iterator<char>());
  GdbDatabase database;
  if (!ParseCsv(text, &database, error)) {
    return false;
  }
  std::vector<uint8_t> data;
  if (!SerializeGdb(database, &data, error)) {
    return false;
  }
  std::ofstream output(gdb_path, std::ios::binary | std::ios::trunc);
  if (!output.write(reinterpret_cast<const char*>(data.data()),
                    static_cast<std::streamsize>(data.size()))) {
    return Fail(error, "cannot write " + gdb_path.generic_string());
  }
  return true;
}

}  // namespace refii::vfs::gdb
