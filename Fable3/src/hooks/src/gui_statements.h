// refii - what Fable 3's UI hooks share: reading the guest's two string classes and posting a
// statement as a pseudo-call on the UI cut's queue (core/gui_native_calls.h), so Godot's
// Fable3GuiNatives routes `@Box.Open`, `@Element.Text` and the rest like any `GUI.*` call.
//
// CCharString: a handle word holding a rep pointer; rep+0 = `char*` (sub_82359150 reads a name
// as `*(char**)handle[0]`, 0x823591E4). LHString (the game's WString): a handle word holding a rep
// pointer; rep+0 = UTF-16BE characters, rep+4 = length in code units (the compare sub_8229B520
// walks `*v2` by `v2[1]`), the same shape hud_feed.cpp reads out of the HUD registries.

#pragma once

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include "core/gui_native_calls.h"

namespace refii::fable3 {

inline uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

inline uint16_t LoadGuestU16(const uint8_t* base, uint32_t address) {
  uint16_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap16(value);
}

inline float LoadGuestF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadGuestU32(base, address);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// A guest pointer, loosely: enough to refuse obvious rubbish while reading structures another
/// thread may be mutating.
inline bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

/// The CCharString whose handle word sits at `at`, bounded.
inline std::string ReadCharString(const uint8_t* base, uint32_t at, uint32_t max_chars = 160u) {
  std::string text;
  if (!Plausible(at)) {
    return text;
  }
  const uint32_t rep = LoadGuestU32(base, at);
  if (!Plausible(rep)) {
    return text;
  }
  const uint32_t chars = LoadGuestU32(base, rep);
  if (!Plausible(chars)) {
    return text;
  }
  for (uint32_t i = 0; i < max_chars; ++i) {
    const char ch = static_cast<char>(base[chars + i]);
    if (ch == '\0') {
      break;
    }
    text.push_back(ch);
  }
  return text;
}

inline void AppendUtf8(std::string& out, uint32_t code) {
  if (code < 0x80u) {
    out.push_back(static_cast<char>(code));
  } else if (code < 0x800u) {
    out.push_back(static_cast<char>(0xC0u | (code >> 6)));
    out.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  } else if (code < 0x10000u) {
    out.push_back(static_cast<char>(0xE0u | (code >> 12)));
    out.push_back(static_cast<char>(0x80u | ((code >> 6) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  } else {
    out.push_back(static_cast<char>(0xF0u | (code >> 18)));
    out.push_back(static_cast<char>(0x80u | ((code >> 12) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | ((code >> 6) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  }
}

/// The LHString whose handle word sits at `at`, as UTF-8.
inline std::string ReadLhString(const uint8_t* base, uint32_t at, uint32_t max_units = 1024u) {
  std::string text;
  if (!Plausible(at)) {
    return text;
  }
  const uint32_t rep = LoadGuestU32(base, at);
  if (!Plausible(rep)) {
    return text;
  }
  const uint32_t chars = LoadGuestU32(base, rep);
  const uint32_t length = LoadGuestU32(base, rep + 4u);
  if (!Plausible(chars) || length == 0u || length > max_units) {
    return text;
  }
  for (uint32_t i = 0; i < length; ++i) {
    uint32_t unit = LoadGuestU16(base, chars + (i * 2u));
    if (unit >= 0xD800u && unit < 0xDC00u && i + 1u < length) {
      const uint32_t low = LoadGuestU16(base, chars + ((i + 1u) * 2u));
      if (low >= 0xDC00u && low < 0xE000u) {
        unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
        ++i;
      }
    }
    AppendUtf8(text, unit);
  }
  return text;
}

inline std::string Hex(uint32_t value) {
  char buffer[16];
  std::snprintf(buffer, sizeof(buffer), "%08X", value);
  return buffer;
}

inline refii::vfs::GuiNativeArgument Text(const std::string& text) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
  argument.text = text;
  return argument;
}

inline refii::vfs::GuiNativeArgument Number(float value) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kNumber;
  argument.number = value;
  return argument;
}

inline refii::vfs::GuiNativeArgument Boolean(bool value) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kBoolean;
  argument.boolean = value;
  return argument;
}

/// A statement under a `@Family.Event` name with its arguments, in order.
class Statement {
 public:
  explicit Statement(const char* name) { call_.name = name; }
  Statement& Add(refii::vfs::GuiNativeArgument argument) {
    call_.arguments.push_back(std::move(argument));
    return *this;
  }
  void Post() { refii::vfs::PostGuiNativeCall(std::move(call_)); }

 private:
  refii::vfs::GuiNativeCall call_;
};

}  // namespace refii::fable3
