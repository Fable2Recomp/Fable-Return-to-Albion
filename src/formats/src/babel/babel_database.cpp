#include "babel/babel_database.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <numeric>

#include <zlib.h>

namespace refii::vfs::babel {
namespace {

// Serialized blocks are packed under this cap; the runtime scratch buffer is
// 18022 bytes and shipped blocks reach ~17926, so this stays safely below
// both while comfortably fitting the largest single string (~5702 bytes).
constexpr size_t kBlockPackLimit = 17000;
constexpr size_t kMaxDecompressedBlock = 18022;

uint32_t ReadBe32(const uint8_t* data) {
  return (static_cast<uint32_t>(data[0]) << 24) |
         (static_cast<uint32_t>(data[1]) << 16) |
         (static_cast<uint32_t>(data[2]) << 8) | static_cast<uint32_t>(data[3]);
}

uint16_t ReadBe16(const uint8_t* data) {
  return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8) |
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

std::string FormatHashLiteral(uint32_t hash) {
  char buffer[16];
  std::snprintf(buffer, sizeof(buffer), "#%08x", hash);
  return buffer;
}

bool IsHashLiteral(std::string_view text, uint32_t* out) {
  if (text.size() != 9 || text.front() != '#') {
    return false;
  }
  uint32_t value = 0;
  for (char ch : text.substr(1)) {
    uint32_t digit = 0;
    if (ch >= '0' && ch <= '9') {
      digit = static_cast<uint32_t>(ch - '0');
    } else if (ch >= 'a' && ch <= 'f') {
      digit = static_cast<uint32_t>(ch - 'a' + 10);
    } else if (ch >= 'A' && ch <= 'F') {
      digit = static_cast<uint32_t>(ch - 'A' + 10);
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

void AppendUtf8(std::string* out, uint32_t code_point) {
  if (code_point < 0x80) {
    out->push_back(static_cast<char>(code_point));
  } else if (code_point < 0x800) {
    out->push_back(static_cast<char>(0xC0 | (code_point >> 6)));
    out->push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
  } else if (code_point < 0x10000) {
    out->push_back(static_cast<char>(0xE0 | (code_point >> 12)));
    out->push_back(static_cast<char>(0x80 | ((code_point >> 6) & 0x3F)));
    out->push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
  } else {
    out->push_back(static_cast<char>(0xF0 | (code_point >> 18)));
    out->push_back(static_cast<char>(0x80 | ((code_point >> 12) & 0x3F)));
    out->push_back(static_cast<char>(0x80 | ((code_point >> 6) & 0x3F)));
    out->push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
  }
}

std::string Utf16ToUtf8(const std::u16string& text) {
  std::string out;
  out.reserve(text.size());
  for (size_t i = 0; i < text.size(); ++i) {
    uint32_t unit = text[i];
    if (unit >= 0xD800 && unit <= 0xDBFF && i + 1 < text.size()) {
      const uint32_t low = text[i + 1];
      if (low >= 0xDC00 && low <= 0xDFFF) {
        unit = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
        ++i;
      }
    }
    AppendUtf8(&out, unit);
  }
  return out;
}

std::u16string Utf8ToUtf16(std::string_view text) {
  std::u16string out;
  out.reserve(text.size());
  size_t i = 0;
  while (i < text.size()) {
    const auto lead = static_cast<uint8_t>(text[i]);
    uint32_t code_point = 0;
    size_t extra = 0;
    if (lead < 0x80) {
      code_point = lead;
    } else if ((lead & 0xE0) == 0xC0) {
      code_point = lead & 0x1F;
      extra = 1;
    } else if ((lead & 0xF0) == 0xE0) {
      code_point = lead & 0x0F;
      extra = 2;
    } else if ((lead & 0xF8) == 0xF0) {
      code_point = lead & 0x07;
      extra = 3;
    } else {
      code_point = 0xFFFD;
    }
    ++i;
    for (size_t k = 0; k < extra && i < text.size(); ++k, ++i) {
      code_point = (code_point << 6) | (static_cast<uint8_t>(text[i]) & 0x3F);
    }
    if (code_point >= 0x10000) {
      code_point -= 0x10000;
      out.push_back(static_cast<char16_t>(0xD800 + (code_point >> 10)));
      out.push_back(static_cast<char16_t>(0xDC00 + (code_point & 0x3FF)));
    } else {
      out.push_back(static_cast<char16_t>(code_point));
    }
  }
  return out;
}

bool ZlibInflate(const uint8_t* data, size_t size, size_t expected,
                 std::vector<uint8_t>* out, std::string* error) {
  // Babel blocks carry a zlib header but omit the adler32 trailer, so a
  // one-shot uncompress() reports a truncated stream. Drive inflate directly
  // and accept the output once the known decompressed size is produced.
  out->assign(expected, 0);
  z_stream stream{};
  if (inflateInit(&stream) != Z_OK) {
    return Fail(error, "zlib inflateInit failed");
  }
  stream.next_in = const_cast<Bytef*>(data);
  stream.avail_in = static_cast<uInt>(size);
  stream.next_out = out->data();
  stream.avail_out = static_cast<uInt>(expected);
  const int result = inflate(&stream, Z_FINISH);
  inflateEnd(&stream);
  if (stream.total_out != expected ||
      (result != Z_STREAM_END && result != Z_OK && result != Z_BUF_ERROR)) {
    return Fail(error, "zlib inflate failed");
  }
  return true;
}

bool ZlibDeflate(const uint8_t* data, size_t size, std::vector<uint8_t>* out,
                 std::string* error) {
  uLongf bound = compressBound(static_cast<uLong>(size));
  out->assign(bound, 0);
  const int result = compress2(out->data(), &bound, data,
                               static_cast<uLong>(size), Z_BEST_COMPRESSION);
  if (result != Z_OK) {
    return Fail(error, "zlib deflate failed");
  }
  out->resize(bound);
  return true;
}

// --- minimal CSV helpers (shared conventions with the gdb codec) ---

void AppendCsvField(std::string* out, std::string_view field) {
  if (field.find_first_of(",\"\n\r") == std::string_view::npos) {
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

void AppendCsvRow(std::string* out, std::string_view key, std::string_view kind,
                  std::string_view value) {
  AppendCsvField(out, key);
  out->push_back(',');
  AppendCsvField(out, kind);
  out->push_back(',');
  AppendCsvField(out, value);
  out->push_back('\n');
}

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

}  // namespace

static bool ParseBabelImpl(const uint8_t* data, size_t size,
                           BabelDatabase* out, std::string* error);
static bool SerializeBabelImpl(const BabelDatabase& database,
                               std::vector<uint8_t>* out,
                               std::string* error);
static bool ParseCsvImpl(std::string_view text, BabelDatabase* out,
                         std::string* error);

uint32_t Fnv1(std::string_view text) {
  uint32_t hash = kFnvBasis;
  for (char ch : text) {
    hash *= kFnvPrime;
    hash ^= static_cast<uint8_t>(ch);
  }
  return hash;
}

void BabelDatabase::AddName(std::string_view text) {
  names.try_emplace(Fnv1(text), std::string(text));
}

const std::string* BabelDatabase::FindName(uint32_t hash) const {
  const auto match = names.find(hash);
  return match == names.end() ? nullptr : &match->second;
}

bool ParseBabel(const uint8_t* data, size_t size, BabelDatabase* out,
                std::string* error) {
  // These parsers run inside the game process on untrusted bytes; no
  // exception (bad_alloc included) may escape.
  try {
    return ParseBabelImpl(data, size, out, error);
  } catch (const std::exception& exception) {
    return Fail(error, std::string("babel ParseBabel exception: ") + exception.what());
  } catch (...) {
    return Fail(error, "babel ParseBabel: unknown exception");
  }
}

static bool ParseBabelImpl(const uint8_t* data, size_t size, BabelDatabase* out,
                std::string* error) {
  *out = BabelDatabase{};
  size_t pos = 0;
  const auto need = [&](size_t count) { return pos + count <= size; };

  if (!need(8) || ReadBe32(data) != kBabelMagic) {
    return Fail(error, "not a babel file (bad magic or truncated)");
  }
  pos = 4;
  const uint32_t record_count = ReadBe32(data + pos);
  pos += 4;
  struct RawRecord {
    uint32_t key;
    uint32_t block_id;
    uint32_t offset;
  };
  // Bounds-check counts BEFORE sizing containers: a corrupt count must fail
  // cleanly, never turn into a multi-gigabyte allocation inside the game.
  if (!need(static_cast<size_t>(record_count) * 12)) {
    return Fail(error, "truncated babel record table");
  }
  std::vector<RawRecord> records(record_count);
  for (uint32_t i = 0; i < record_count; ++i) {
    records[i] = {ReadBe32(data + pos), ReadBe32(data + pos + 4),
                  ReadBe32(data + pos + 8)};
    pos += 12;
  }

  if (!need(4)) {
    return Fail(error, "truncated babel block count");
  }
  const uint32_t block_count = ReadBe32(data + pos);
  pos += 4;
  std::unordered_map<uint32_t, std::vector<uint8_t>> blocks;
  blocks.reserve(block_count);
  for (uint32_t i = 0; i < block_count; ++i) {
    if (!need(12)) {
      return Fail(error, "truncated babel block header");
    }
    const uint32_t block_id = ReadBe32(data + pos);
    const uint32_t comp_size = ReadBe32(data + pos + 4);
    const uint32_t decomp_size = ReadBe32(data + pos + 8);
    pos += 12;
    if (!need(comp_size)) {
      return Fail(error, "truncated babel block payload");
    }
    if (decomp_size > kMaxDecompressedBlock) {
      // The runtime scratch buffer is 18022 bytes; anything larger is corrupt
      // (and would otherwise let a bad file demand an arbitrary allocation).
      return Fail(error, "babel block exceeds the 18022-byte scratch limit");
    }
    std::vector<uint8_t> decompressed;
    if (!ZlibInflate(data + pos, comp_size, decomp_size, &decompressed,
                     error)) {
      return false;
    }
    blocks.emplace(block_id, std::move(decompressed));
    pos += comp_size;
  }

  if (!need(4)) {
    return Fail(error, "truncated babel scratch size");
  }
  out->scratch_block_size = ReadBe32(data + pos);
  pos += 4;

  // Records -> decoded strings. All offset math in 64-bit: a crafted 32-bit
  // offset near UINT32_MAX must not wrap past the bounds checks.
  out->strings.reserve(record_count);
  for (const RawRecord& record : records) {
    const auto block = blocks.find(record.block_id);
    const uint64_t offset = record.offset;
    if (block == blocks.end() || offset + 4 > block->second.size()) {
      return Fail(error, "babel record references missing block/offset");
    }
    const uint32_t char_count = ReadBe32(block->second.data() + offset);
    const uint64_t bytes = static_cast<uint64_t>(char_count) * 2;
    if (offset + 4 + bytes > block->second.size()) {
      return Fail(error, "babel string overruns its block");
    }
    std::u16string text(char_count, u'\0');
    for (uint32_t c = 0; c < char_count; ++c) {
      text[c] = static_cast<char16_t>(
          ReadBe16(block->second.data() + offset + 4 + 2 * c));
    }
    out->strings.push_back({record.key, std::move(text)});
  }

  const auto read_u32 = [&](uint32_t* value) {
    if (!need(4)) {
      return false;
    }
    *value = ReadBe32(data + pos);
    pos += 4;
    return true;
  };

  uint32_t count = 0;
  if (!read_u32(&count)) {
    return Fail(error, "truncated babel speech-link count");
  }
  if (!need(static_cast<size_t>(count) * 10)) {
    return Fail(error, "truncated babel speech links");
  }
  out->speech_links.reserve(count);
  for (uint32_t i = 0; i < count; ++i) {
    if (!need(10)) {
      return Fail(error, "truncated babel speech links");
    }
    out->speech_links.push_back(
        {ReadBe32(data + pos), ReadBe32(data + pos + 4), ReadBe16(data + pos + 8)});
    pos += 10;
  }

  if (!read_u32(&count)) {
    return Fail(error, "truncated babel text-group count");
  }
  // Variable-size entries need at least 8 bytes each; cap the reservation so
  // a corrupt count cannot demand an oversized allocation up front.
  out->text_groups.reserve(std::min<size_t>(count, (size - pos) / 8));
  for (uint32_t i = 0; i < count; ++i) {
    if (!need(8)) {
      return Fail(error, "truncated babel text group");
    }
    BabelTextGroup group;
    group.key = ReadBe32(data + pos);
    const uint32_t members = ReadBe32(data + pos + 4);
    pos += 8;
    if (!need(static_cast<size_t>(members) * 4)) {
      return Fail(error, "truncated babel text-group members");
    }
    group.members.resize(members);
    for (uint32_t m = 0; m < members; ++m) {
      group.members[m] = ReadBe32(data + pos);
      pos += 4;
    }
    out->text_groups.push_back(std::move(group));
  }

  if (!read_u32(&count)) {
    return Fail(error, "truncated babel speaker count");
  }
  out->speakers.reserve(std::min<size_t>(count, (size - pos) / 8));
  for (uint32_t i = 0; i < count; ++i) {
    if (!need(8)) {
      return Fail(error, "truncated babel speaker");
    }
    BabelSpeaker speaker;
    speaker.key = ReadBe32(data + pos);
    const uint32_t char_count = ReadBe32(data + pos + 4);
    pos += 8;
    const size_t bytes = static_cast<size_t>(char_count) * 2;
    if (!need(bytes)) {
      return Fail(error, "truncated babel speaker name");
    }
    speaker.name.resize(char_count);
    for (uint32_t c = 0; c < char_count; ++c) {
      speaker.name[c] = static_cast<char16_t>(ReadBe16(data + pos + 2 * c));
    }
    pos += bytes;
    out->speakers.push_back(std::move(speaker));
  }

  if (pos != size) {
    return Fail(error, "trailing bytes after babel speaker table");
  }
  return true;
}

bool SerializeBabel(const BabelDatabase& database, std::vector<uint8_t>* out,
                    std::string* error) {
  // These parsers run inside the game process on untrusted bytes; no
  // exception (bad_alloc included) may escape.
  try {
    return SerializeBabelImpl(database, out, error);
  } catch (const std::exception& exception) {
    return Fail(error, std::string("babel SerializeBabel exception: ") + exception.what());
  } catch (...) {
    return Fail(error, "babel SerializeBabel: unknown exception");
  }
}

static bool SerializeBabelImpl(const BabelDatabase& database, std::vector<uint8_t>* out,
                    std::string* error) {
  out->clear();

  std::vector<uint32_t> order(database.strings.size());
  std::iota(order.begin(), order.end(), 0u);
  std::stable_sort(order.begin(), order.end(),
                   [&database](uint32_t left, uint32_t right) {
                     return database.strings[left].key <
                            database.strings[right].key;
                   });

  struct RecordOut {
    uint32_t key;
    uint32_t block_id;
    uint32_t offset;
  };
  std::vector<RecordOut> records;
  records.reserve(order.size());
  std::vector<std::vector<uint8_t>> block_payloads;  // decompressed
  std::vector<uint8_t> current;
  uint32_t current_block_id = 0;

  const auto flush_block = [&]() {
    if (!current.empty()) {
      block_payloads.push_back(std::move(current));
      current.clear();
      ++current_block_id;
    }
  };

  for (uint32_t index : order) {
    const BabelString& entry = database.strings[index];
    const size_t encoded = 4 + entry.text.size() * 2;
    if (encoded > kMaxDecompressedBlock) {
      return Fail(error, "babel string exceeds the 18022-byte block limit");
    }
    if (!current.empty() && current.size() + encoded > kBlockPackLimit) {
      flush_block();
    }
    records.push_back({entry.key, current_block_id,
                       static_cast<uint32_t>(current.size())});
    AppendBe32(&current, static_cast<uint32_t>(entry.text.size()));
    for (char16_t unit : entry.text) {
      AppendBe16(&current, static_cast<uint16_t>(unit));
    }
  }
  flush_block();

  out->insert(out->end(), {0x5B, 0x01, 0x00, 0x00});
  AppendBe32(out, static_cast<uint32_t>(records.size()));
  for (const RecordOut& record : records) {
    AppendBe32(out, record.key);
    AppendBe32(out, record.block_id);
    AppendBe32(out, record.offset);
  }
  AppendBe32(out, static_cast<uint32_t>(block_payloads.size()));
  for (uint32_t block_id = 0; block_id < block_payloads.size(); ++block_id) {
    std::vector<uint8_t> compressed;
    if (!ZlibDeflate(block_payloads[block_id].data(),
                     block_payloads[block_id].size(), &compressed, error)) {
      return false;
    }
    AppendBe32(out, block_id);
    AppendBe32(out, static_cast<uint32_t>(compressed.size()));
    AppendBe32(out, static_cast<uint32_t>(block_payloads[block_id].size()));
    out->insert(out->end(), compressed.begin(), compressed.end());
  }
  AppendBe32(out, database.scratch_block_size);

  AppendBe32(out, static_cast<uint32_t>(database.speech_links.size()));
  for (const BabelSpeechLink& link : database.speech_links) {
    AppendBe32(out, link.text_key);
    AppendBe32(out, link.speech_id);
    AppendBe16(out, link.flag);
  }

  AppendBe32(out, static_cast<uint32_t>(database.text_groups.size()));
  for (const BabelTextGroup& group : database.text_groups) {
    AppendBe32(out, group.key);
    AppendBe32(out, static_cast<uint32_t>(group.members.size()));
    for (uint32_t member : group.members) {
      AppendBe32(out, member);
    }
  }

  AppendBe32(out, static_cast<uint32_t>(database.speakers.size()));
  for (const BabelSpeaker& speaker : database.speakers) {
    AppendBe32(out, speaker.key);
    AppendBe32(out, static_cast<uint32_t>(speaker.name.size()));
    for (char16_t unit : speaker.name) {
      AppendBe16(out, static_cast<uint16_t>(unit));
    }
  }
  return true;
}

void SerializeCsv(const BabelDatabase& database, std::string* out) {
  out->clear();
  out->append("key,kind,value\n");
  const auto key_text = [&](uint32_t hash) {
    const std::string* name = database.FindName(hash);
    return name != nullptr ? *name : FormatHashLiteral(hash);
  };
  const auto editable_text = [](const std::u16string& native_text) {
    std::u16string_view text(native_text);
    if (!text.empty() && text.back() == u'\0') {
      text.remove_suffix(1);
    }
    return Utf16ToUtf8(std::u16string(text));
  };

  for (const BabelString& entry : database.strings) {
    AppendCsvRow(out, key_text(entry.key), "text", editable_text(entry.text));
  }
  for (const BabelSpeechLink& link : database.speech_links) {
    std::string value = FormatHashLiteral(link.speech_id);
    value.push_back(' ');
    value.append(std::to_string(link.flag));
    AppendCsvRow(out, key_text(link.text_key), "speech", value);
  }
  for (const BabelTextGroup& group : database.text_groups) {
    std::string value;
    for (uint32_t member : group.members) {
      if (!value.empty()) {
        value.push_back(' ');
      }
      value.append(key_text(member));
    }
    AppendCsvRow(out, key_text(group.key), "group", value);
  }
  for (const BabelSpeaker& speaker : database.speakers) {
    AppendCsvRow(out, key_text(speaker.key), "speaker",
                 editable_text(speaker.name));
  }
}

bool ParseCsv(std::string_view text, BabelDatabase* out, std::string* error) {
  // These parsers run inside the game process on untrusted bytes; no
  // exception (bad_alloc included) may escape.
  try {
    return ParseCsvImpl(text, out, error);
  } catch (const std::exception& exception) {
    return Fail(error, std::string("babel ParseCsv exception: ") + exception.what());
  } catch (...) {
    return Fail(error, "babel ParseCsv: unknown exception");
  }
}

static bool ParseCsvImpl(std::string_view text, BabelDatabase* out, std::string* error) {
  *out = BabelDatabase{};
  // Tolerate the UTF-8 BOM that Windows CSV editors prepend.
  if (text.starts_with("\xEF\xBB\xBF")) {
    text.remove_prefix(3);
  }
  size_t position = 0;
  size_t line = 0;
  std::vector<std::string> fields;

  const auto parse_key = [out](const std::string& field) -> uint32_t {
    uint32_t hash = 0;
    if (IsHashLiteral(field, &hash)) {
      return hash;
    }
    out->AddName(field);
    return Fnv1(field);
  };

  while (position < text.size()) {
    ++line;
    if (!ParseCsvRecord(text, &position, &fields)) {
      return Fail(error, "babel CSV line " + std::to_string(line) +
                             ": unterminated quoted field");
    }
    if (fields.empty() || (fields.size() == 1 && fields[0].empty())) {
      continue;
    }
    if (line == 1 && fields.size() >= 2 && fields[0] == "key" &&
        fields[1] == "kind") {
      continue;
    }
    if (fields.size() != 3) {
      return Fail(error, "babel CSV line " + std::to_string(line) +
                             ": expected 3 columns");
    }
    const std::string& kind = fields[1];
    const std::string& value = fields[2];
    if (kind == "text") {
      std::u16string native_text = Utf8ToUtf16(value);
      native_text.push_back(u'\0');
      out->strings.push_back({parse_key(fields[0]), std::move(native_text)});
    } else if (kind == "speaker") {
      std::u16string native_name = Utf8ToUtf16(value);
      native_name.push_back(u'\0');
      out->speakers.push_back({parse_key(fields[0]), std::move(native_name)});
    } else if (kind == "speech") {
      const size_t space = value.find(' ');
      if (space == std::string::npos) {
        return Fail(error, "babel CSV line " + std::to_string(line) +
                               ": speech row needs '<key> <flag>'");
      }
      BabelSpeechLink link;
      link.text_key = parse_key(fields[0]);
      uint32_t speech = 0;
      IsHashLiteral(value.substr(0, space), &speech);
      link.speech_id = speech;
      link.flag = static_cast<uint16_t>(
          std::strtoul(value.c_str() + space + 1, nullptr, 0));
      out->speech_links.push_back(link);
    } else if (kind == "group") {
      BabelTextGroup group;
      group.key = parse_key(fields[0]);
      size_t start = 0;
      while (start < value.size()) {
        size_t space = value.find(' ', start);
        if (space == std::string::npos) {
          space = value.size();
        }
        if (space > start) {
          group.members.push_back(parse_key(value.substr(start, space - start)));
        }
        start = space + 1;
      }
      out->text_groups.push_back(std::move(group));
    } else {
      return Fail(error, "babel CSV line " + std::to_string(line) +
                             ": unknown kind '" + kind + "'");
    }
  }
  return true;
}

bool LoadBabelFile(const std::filesystem::path& path, BabelDatabase* out,
                   std::string* error) {
  std::ifstream stream(path, std::ios::binary);
  if (!stream) {
    return Fail(error, "cannot open " + path.generic_string());
  }
  std::vector<uint8_t> data((std::istreambuf_iterator<char>(stream)),
                            std::istreambuf_iterator<char>());
  return ParseBabel(data.data(), data.size(), out, error);
}

bool ConvertBabelFileToCsvFile(const std::filesystem::path& babel_path,
                               const std::filesystem::path& csv_path,
                               std::string* error) {
  BabelDatabase database;
  if (!LoadBabelFile(babel_path, &database, error)) {
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

bool ConvertCsvFileToBabelFile(const std::filesystem::path& csv_path,
                               const std::filesystem::path& babel_path,
                               std::string* error) {
  std::ifstream stream(csv_path, std::ios::binary);
  if (!stream) {
    return Fail(error, "cannot open " + csv_path.generic_string());
  }
  const std::string text((std::istreambuf_iterator<char>(stream)),
                         std::istreambuf_iterator<char>());
  BabelDatabase database;
  if (!ParseCsv(text, &database, error)) {
    return false;
  }
  std::vector<uint8_t> data;
  if (!SerializeBabel(database, &data, error)) {
    return false;
  }
  std::ofstream output(babel_path, std::ios::binary | std::ios::trunc);
  if (!output.write(reinterpret_cast<const char*>(data.data()),
                    static_cast<std::streamsize>(data.size()))) {
    return Fail(error, "cannot write " + babel_path.generic_string());
  }
  return true;
}

}  // namespace refii::vfs::babel
