#include "core/native_lua_hooks.h"
#include "core/native_lua_rexglue_compat.h"
#include "lua_script_paths.h"

#include "core/payload_bridge_string.h"
#include "core/rex_payload_bridge.h"

#include <algorithm>
#include <cstring>
#include <cctype>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/runtime.h>

namespace refii::lua {
namespace {
REXCVAR_DEFINE_BOOL(lua_trace_script_loads, false, "Lua",
                    "Log script source paths at the Lua file-load choke point.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

bool StartsWith(std::string_view value, std::string_view prefix) {
  return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

bool LooksLikeReadableGuestString(std::string_view value) {
  if (value.empty()) {
    return false;
  }

  for (const unsigned char ch : value) {
    if (ch == '\t' || ch == '\r' || ch == '\n') {
      continue;
    }
    if (ch < 0x20 || ch > 0x7E) {
      return false;
    }
  }

  return true;
}

int ScoreReadableGuestString(std::string_view value) {
  if (!LooksLikeReadableGuestString(value)) {
    return 0;
  }

  int score = 1;
  if (value.find(".lua") != std::string_view::npos) {
    score += 4;
  }
  if (value.find("scripts") != std::string_view::npos ||
      value.find("game:\\") != std::string_view::npos ||
      value.find("data\\") != std::string_view::npos ||
      value.find('\\') != std::string_view::npos ||
      value.find('/') != std::string_view::npos) {
    score += 2;
  }
  if (value.size() >= 16) {
    score += 1;
  }
  return score;
}

bool LooksLikeLuaScriptKey(std::string_view key) {
  if (key.empty() || key.find(".lua") == std::string_view::npos) {
    return false;
  }

  for (const unsigned char ch : key) {
    if (std::isalnum(ch) || ch == '/' || ch == '\\' || ch == '.' || ch == '_' || ch == '-' ||
        ch == '@' || ch == ':' || ch == ' ') {
      continue;
    }
    return false;
  }

  return true;
}

uint32_t ReadGuestLE32(uint8_t* base, uint32_t guest_address) {
  return uint32_t(PpcLoadU8(base, guest_address)) |
         (uint32_t(PpcLoadU8(base, guest_address + 1)) << 8) |
         (uint32_t(PpcLoadU8(base, guest_address + 2)) << 16) |
         (uint32_t(PpcLoadU8(base, guest_address + 3)) << 24);
}

std::string ReadGuestSizedString(uint8_t* base, uint32_t data_guest, uint32_t length,
                                 size_t max_bytes) {
  if (data_guest == 0 || length == 0 || length > 0x10000) {
    return {};
  }

  std::string value;
  const size_t clamped_length = std::min<size_t>(length, max_bytes);
  value.reserve(clamped_length);
  for (size_t i = 0; i < clamped_length; ++i) {
    value.push_back(static_cast<char>(
        PpcLoadU8(base, data_guest + static_cast<uint32_t>(i))));
  }

  while (!value.empty() && value.back() == '\0') {
    value.pop_back();
  }

  return LooksLikeReadableGuestString(value) ? value : std::string{};
}

std::string ReadGuestStringObject(uint8_t* base, uint32_t object_guest, size_t max_bytes) {
  if (object_guest == 0) {
    return {};
  }

  const uint32_t data_guest = PpcLoadU32(base, object_guest + 0);
  const uint32_t length = PpcLoadU32(base, object_guest + 4);
  return ReadGuestSizedString(base, data_guest, length, max_bytes);
}

std::string ReadGuestLua51TStringObject(uint8_t* base, uint32_t object_guest, size_t max_bytes) {
  if (base == nullptr || object_guest == 0) {
    return {};
  }

  // Guest stack string values point at a real Lua 5.1 TString object:
  // CommonHeader (next, tt, marked), reserved, hash, len, then inline chars.
  constexpr uint32_t kLua51StringTagOffset = 4;
  constexpr uint32_t kLua51StringLengthOffset = 12;
  constexpr uint32_t kLua51StringCharsOffset = 16;
  constexpr uint8_t kLuaTString = 4;

  if (PpcLoadU8(base, object_guest + kLua51StringTagOffset) != kLuaTString) {
    return {};
  }

  const uint32_t length = PpcLoadU32(base, object_guest + kLua51StringLengthOffset);
  return ReadGuestSizedString(base,
                              object_guest + kLua51StringCharsOffset,
                              length,
                              max_bytes);
}

std::string ReadGuestWrappedString(uint8_t* base, uint32_t wrapper_guest, size_t max_bytes) {
  if (wrapper_guest == 0) {
    return {};
  }

  const uint32_t object_guest = PpcLoadU32(base, wrapper_guest + 0);
  return ReadGuestStringObject(base, object_guest, max_bytes);
}

std::string ReadLuaBytecodeSourcePath(uint8_t* base, const NativeScriptRequest& request) {
  if (request.script_buffer_guest == 0 || request.script_size < 16) {
    return {};
  }

  const uint32_t chunk = request.script_buffer_guest;
  if (PpcLoadU8(base, chunk) != 0x1B || PpcLoadU8(base, chunk + 1) != 'L' ||
      PpcLoadU8(base, chunk + 2) != 'u' ||
      PpcLoadU8(base, chunk + 3) != 'a') {
    return {};
  }

  if (PpcLoadU8(base, chunk + 4) != 0x51 ||
      PpcLoadU8(base, chunk + 6) != 0x01 ||
      PpcLoadU8(base, chunk + 8) != 0x04) {
    return {};
  }

  const uint32_t source_size = ReadGuestLE32(base, chunk + 12);
  if (source_size <= 1 || source_size > request.script_size - 16 || source_size > 4096) {
    return {};
  }

  std::string source;
  source.reserve(source_size);
  for (uint32_t i = 0; i < source_size - 1; ++i) {
    source.push_back(static_cast<char>(PpcLoadU8(base, chunk + 16 + i)));
  }
  return NormalizeLuaScriptKey(source);
}

std::string CanonicalizeLuaScriptKeyCandidate(std::string key) {
  key = NormalizeLuaScriptKey(
      refii::lua::CanonicalizeRetailLuaRelativeString(std::move(key)));

  constexpr std::string_view kOverridePrefix = "override/";
  constexpr std::string_view kGameScriptsPrefix = "gamescripts/scripts/";
  constexpr std::string_view kGuiScriptsPrefix = "guiscripts/scripts/";
  constexpr std::string_view kGuiArtPrefix = "guiscripts/art/";

  if (StartsWith(key, kOverridePrefix)) {
    key = key.substr(kOverridePrefix.size());
  }

  if (StartsWith(key, kGameScriptsPrefix)) {
    return "scripts/" + key.substr(kGameScriptsPrefix.size());
  }
  if (StartsWith(key, kGuiScriptsPrefix)) {
    return "scripts/" + key.substr(kGuiScriptsPrefix.size());
  }
  if (StartsWith(key, kGuiArtPrefix)) {
    return "art/" + key.substr(kGuiArtPrefix.size());
  }

  return key;
}

std::string ExtractEmbeddedLuaScriptKey(std::string_view value) {
  if (value.empty()) {
    return {};
  }

  const std::string normalized = NormalizeLuaScriptKey(std::string(value));
  if (normalized.empty()) {
    return {};
  }

  const std::string canonical = CanonicalizeLuaScriptKeyCandidate(normalized);
  if (LooksLikeLuaScriptKey(canonical)) {
    return canonical;
  }

  constexpr std::string_view kCandidatePrefixes[] = {
      "data/scripts/",      "data/scripts_r/",      "scripts/",        "scripts_r/",
      "gamescripts/scripts/", "gamescripts_r/scripts/", "guiscripts/scripts/",
      "guiscripts/art/",    "art/"};

  for (const std::string_view prefix : kCandidatePrefixes) {
    size_t search_from = 0;
    while (true) {
      const size_t start = normalized.find(prefix, search_from);
      if (start == std::string::npos) {
        break;
      }

      const size_t lua_suffix = normalized.find(".lua", start);
      if (lua_suffix != std::string::npos) {
        std::string candidate = CanonicalizeLuaScriptKeyCandidate(
            normalized.substr(start, lua_suffix + 4 - start));
        if (LooksLikeLuaScriptKey(candidate)) {
          return candidate;
        }
      }

      search_from = start + 1;
    }
  }

  return {};
}

std::string ReadValidatedLuaScriptKey(uint8_t* base, const NativeScriptRequest& request) {
  const std::string source = ExtractEmbeddedLuaScriptKey(
      ReadGuestLuaStringLike(base, request.source_guest));
  if (LooksLikeLuaScriptKey(source)) {
    return source;
  }

  const std::string chunk_name = ExtractEmbeddedLuaScriptKey(
      ReadGuestLuaStringLike(base, request.chunk_name_guest));
  if (LooksLikeLuaScriptKey(chunk_name)) {
    return chunk_name;
  }

  const std::string bytecode_source =
      CanonicalizeLuaScriptKeyCandidate(ReadLuaBytecodeSourcePath(base, request));
  if (LooksLikeLuaScriptKey(bytecode_source)) {
    return bytecode_source;
  }

  return {};
}
}  // namespace

std::string ReadGuestCString(uint8_t* base, uint32_t guest_ptr, size_t max_bytes) {
  std::string value;
  if (guest_ptr == 0) {
    return value;
  }

  value.reserve(std::min<size_t>(max_bytes, 128));
  for (size_t i = 0; i < max_bytes; ++i) {
    const auto byte =
        static_cast<char>(PpcLoadU8(base, guest_ptr + static_cast<uint32_t>(i)));
    if (byte == '\0') {
      break;
    }
    value.push_back(byte);
  }
  return value;
}

std::string ReadGuestLuaStringLikeImpl(uint8_t* base, uint32_t guest_ptr, size_t max_bytes) {
  const std::string direct = ReadGuestCString(base, guest_ptr, max_bytes);
  const std::string wrapped = ReadGuestWrappedString(base, guest_ptr, max_bytes);
  const std::string object = ReadGuestStringObject(base, guest_ptr, max_bytes);
  const std::string lua51_object = ReadGuestLua51TStringObject(base, guest_ptr, max_bytes);

  const std::string* best = nullptr;
  int best_score = 0;
  auto consider = [&](const std::string& candidate) {
    const int score = ScoreReadableGuestString(candidate);
    if (score == 0) {
      return;
    }
    if (best == nullptr || score > best_score ||
        (score == best_score && candidate.size() > best->size())) {
      best = &candidate;
      best_score = score;
    }
  };

  consider(direct);
  consider(wrapped);
  consider(object);
  consider(lua51_object);
  if (best != nullptr) {
    return *best;
  }

  return direct;
}

std::string NormalizeLuaScriptKey(std::string key) {
  if (key.empty()) {
    return key;
  }

  std::replace(key.begin(), key.end(), '\\', '/');
  std::transform(key.begin(), key.end(), key.begin(),
                 [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });

  if (!key.empty() && key.front() == '@') {
    key.erase(key.begin());
  }

  constexpr std::string_view kScriptsPrefix = "data/scripts/";
  constexpr std::string_view kScriptsRPrefix = "data/scripts_r/";

  if (const auto pos = key.find(kScriptsPrefix); pos != std::string::npos) {
    return key.substr(pos);
  }
  if (const auto pos = key.find(kScriptsRPrefix); pos != std::string::npos) {
    return key.substr(pos);
  }

  return key;
}

std::string ReadNativeLuaScriptKeyImpl(uint8_t* base, const NativeScriptRequest& request) {
  return ReadValidatedLuaScriptKey(base, request);
}

extern "C" int32_t refii_bridge_lua_native_script_load_tracing_enabled() { return REXCVAR_GET(lua_trace_script_loads); }

// --- The payload/host C ABI ----------------------------------------------------------
//
// Entries for the inline wrappers in core/native_lua_hooks.h. The C++ implementations above
// keep their shape for host callers; only the boundary is flattened. Strings go into the
// CALLER's buffer with the needed length returned, so nothing allocated here is freed on
// the other side. See core/lua_payload_bridge.h.

namespace {
size_t LuaCopyOut(const std::string& value, char* out, size_t capacity) {
  if (out != nullptr && capacity > 0) {
    size_t take = value.size() < capacity ? value.size() : capacity;
    std::memcpy(out, value.data(), take);
  }
  return value.size();
}
}  // namespace

extern "C" size_t refii_bridge_lua_read_guest_lua_string_like(uint8_t* base, uint32_t guest_ptr,
                                                              size_t max_bytes, char* out,
                                                              size_t capacity) {
  return LuaCopyOut(ReadGuestLuaStringLikeImpl(base, guest_ptr, max_bytes), out, capacity);
}

extern "C" size_t refii_bridge_lua_read_native_script_key(uint8_t* base,
                                                          const NativeScriptRequest* request,
                                                          char* out, size_t capacity) {
  if (request == nullptr) {
    return 0;
  }
  return LuaCopyOut(ReadNativeLuaScriptKeyImpl(base, *request), out, capacity);
}
}  // namespace refii::lua
