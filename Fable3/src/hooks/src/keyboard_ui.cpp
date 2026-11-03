// refii - Fable 3's system keyboard: the guest asks the platform for text, and the host is the
// platform. Its one caller is the interaction mode's rename (`GUI.RenameEntityWithInteractionMode`,
// the dog's and a weapon's name in the Sanctuary, GuildRoomsSingle.lua).
//
// THE CALL. sub_82961FA0(mode, entity, pad, hero) (reached from the native 0x8251F110 at
// 0x8251F2B0) builds the title `TEXT_QUERY_INTERFACE_RENAME_TITLE` (mode+80, 0x829621C4) and the
// description `TEXT_QUERY_INTERFACE_RENAME_DESC` (mode+84, 0x829621EC), empties the 17-unit
// buffer at mode+44 (sub_82E34950 at 0x829621A4), zeroes the XOVERLAPPED at mode+88..+112
// (0x8296215C) and calls the thunk j_XamShowKeyboardUI (0x830FF1F8, `b XamShowKeyboardUI`) at
// 0x82962250 with r3 the player's user index `*(player+40)`, r4 0x20000004, r5 the default text
// (the empty buffer), r6 the title, r7 the description, r8 the buffer, r9 17, r10 the
// XOVERLAPPED. A result of 997 marks the request pending (mode+25 = 1, 0x82962260).
//
// THE ANSWER THE GUEST WAITS FOR. The mode's poll sub_82962E28 runs while mode+25 is set: it waits
// for the XOVERLAPPED's InternalLow to leave 997 (0x82962EA8), then XGetOverlappedResult
// (sub_82DABF20(ov, 0, 1) at 0x82962F10) answers InternalLow. Non-zero, or a buffer that names
// nothing new, posts the message "ClosedSystemUI" (0x82962FE8..0x82963028); zero reads the buffer
// as the new name (sub_8229B650 from mode+44) and hands it to sub_82962720, which renames the dog
// or the weapon when the text is non-empty and differs from its current name. hEvent and the
// completion routine are zero (the mode zeroed them), so the completion is the stores alone.
//
// THE HOST'S SIDE. The hook leaves the request pending and states it as
// `@Keyboard.Open(user, title, description, default, max units)`; Godot's Fable3Keyboard asks
// the player and posts the answer as a guest tick action (`keyboard:<text>` or
// `keyboard-cancel`), which the tick pump hands to AnswerKeyboardUi on the guest's own thread.

#include "keyboard_ui.h"

#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"

namespace {

using namespace refii::fable3;

constexpr uint32_t kErrorSuccess = 0u;
constexpr uint32_t kErrorInvalidParameter = 87u;
constexpr uint32_t kErrorIoPending = 997u;
constexpr uint32_t kErrorCancelled = 1223u;

/// XOVERLAPPED: +0 InternalLow (the result), +4 InternalHigh, +12 hEvent, +16 completion
/// routine, +24 dwExtendedError.
constexpr uint32_t kOverlappedResult = 0u;
constexpr uint32_t kOverlappedHigh = 4u;
constexpr uint32_t kOverlappedEvent = 12u;
constexpr uint32_t kOverlappedRoutine = 16u;
constexpr uint32_t kOverlappedExtended = 24u;

constexpr uint32_t kMaxUnits = 512u;

struct Pending {
  uint32_t buffer = 0u;
  uint32_t units = 0u;
  uint32_t overlapped = 0u;
};

std::mutex g_mutex;
Pending g_pending;

void StoreGuestU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

void StoreGuestU16(uint8_t* base, uint32_t address, uint16_t value) {
  const uint16_t swapped = __builtin_bswap16(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

/// A NUL-terminated UTF-16BE string at `address`, as UTF-8.
std::string ReadWide(const uint8_t* base, uint32_t address) {
  std::string text;
  if (!Plausible(address)) {
    return text;
  }
  for (uint32_t i = 0; i < kMaxUnits; ++i) {
    uint32_t unit = LoadGuestU16(base, address + (i * 2u));
    if (unit == 0u) {
      break;
    }
    if (unit >= 0xD800u && unit < 0xDC00u) {
      const uint32_t low = LoadGuestU16(base, address + ((i + 1u) * 2u));
      if (low >= 0xDC00u && low < 0xE000u) {
        unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
        ++i;
      }
    }
    AppendUtf8(text, unit);
  }
  return text;
}

/// The next code point of UTF-8 `text` from `at`, advancing it; U+FFFD for a malformed byte.
uint32_t NextCodePoint(const char* text, size_t& at) {
  const auto byte = [&](size_t i) { return static_cast<uint8_t>(text[i]); };
  const uint8_t lead = byte(at);
  uint32_t code = 0xFFFDu;
  size_t length = 1u;
  if (lead < 0x80u) {
    code = lead;
  } else if ((lead & 0xE0u) == 0xC0u) {
    length = 2u;
  } else if ((lead & 0xF0u) == 0xE0u) {
    length = 3u;
  } else if ((lead & 0xF8u) == 0xF0u) {
    length = 4u;
  }
  if (length > 1u) {
    code = lead & (0xFFu >> (length + 1u));
    for (size_t i = 1; i < length; ++i) {
      const uint8_t next = byte(at + i);
      if ((next & 0xC0u) != 0x80u) {
        at += i;
        return 0xFFFDu;
      }
      code = (code << 6) | (next & 0x3Fu);
    }
  }
  at += length;
  return code;
}

}  // namespace

/// `XamShowKeyboardUI(user, flags, default, title, description, buffer, units, overlapped)`,
/// through its one guest thunk: left pending for the host to answer.
REX_EXTERN(rex_Xam_ShowKeyboardUI) {
  const uint32_t user = ctx.r3.u32;
  const uint32_t flags = ctx.r4.u32;
  const uint32_t default_text = ctx.r5.u32;
  const uint32_t title = ctx.r6.u32;
  const uint32_t description = ctx.r7.u32;
  const uint32_t buffer = ctx.r8.u32;
  const uint32_t units = ctx.r9.u32;
  const uint32_t overlapped = ctx.r10.u32;

  if (base == nullptr || !Plausible(buffer) || !Plausible(overlapped) || units == 0u || units > kMaxUnits) {
    ctx.r3.u64 = kErrorInvalidParameter;
    return;
  }
  if (LoadGuestU32(base, overlapped + kOverlappedEvent) != 0u ||
      LoadGuestU32(base, overlapped + kOverlappedRoutine) != 0u) {
    REXLOG_WARN("Refii keyboard: the XOVERLAPPED at {:08X} carries an event or a routine; the answer "
                "completes it by its result word only",
                overlapped);
  }

  const std::string stated_default = ReadWide(base, default_text);
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_pending = Pending{buffer, units, overlapped};
  }
  StoreGuestU32(base, overlapped + kOverlappedHigh, 0u);
  StoreGuestU32(base, overlapped + kOverlappedExtended, kErrorIoPending);
  StoreGuestU32(base, overlapped + kOverlappedResult, kErrorIoPending);

  Statement("@Keyboard.Open")
      .Add(Number(static_cast<float>(user)))
      .Add(Text(ReadWide(base, title)))
      .Add(Text(ReadWide(base, description)))
      .Add(Text(stated_default))
      .Add(Number(static_cast<float>(units)))
      .Post();
  REXLOG_INFO("Refii keyboard: open for user {} (flags {:08X}, {} unit(s), overlapped {:08X})", user, flags,
              units, overlapped);
  ctx.r3.u64 = kErrorIoPending;
}

namespace refii::fable3 {

bool AnswerKeyboardUi(uint8_t* base, const char* text) {
  Pending pending;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    pending = g_pending;
    g_pending = Pending{};
  }
  if (base == nullptr || pending.overlapped == 0u) {
    REXLOG_WARN("Refii keyboard: an answer with no keyboard open; dropped");
    return false;
  }

  uint32_t result = kErrorCancelled;
  uint32_t written = 0u;
  if (text != nullptr) {
    // The buffer holds `units` code units with the terminator.
    size_t at = 0u;
    while (text[at] != '\0' && written + 1u < pending.units) {
      uint32_t code = NextCodePoint(text, at);
      if (code >= 0x10000u) {
        if (written + 2u >= pending.units) {
          break;
        }
        code -= 0x10000u;
        StoreGuestU16(base, pending.buffer + (written * 2u), static_cast<uint16_t>(0xD800u + (code >> 10)));
        ++written;
        code = 0xDC00u + (code & 0x3FFu);
      }
      StoreGuestU16(base, pending.buffer + (written * 2u), static_cast<uint16_t>(code));
      ++written;
    }
    StoreGuestU16(base, pending.buffer + (written * 2u), 0u);
    result = kErrorSuccess;
  }

  // The result word last: the guest's poll reads only it (0x82962EA8).
  StoreGuestU32(base, pending.overlapped + kOverlappedExtended, result);
  __atomic_thread_fence(__ATOMIC_RELEASE);
  StoreGuestU32(base, pending.overlapped + kOverlappedResult, result);
  REXLOG_INFO("Refii keyboard: answered {} ({} unit(s))", text != nullptr ? "with text" : "cancelled", written);
  return true;
}

}  // namespace refii::fable3
