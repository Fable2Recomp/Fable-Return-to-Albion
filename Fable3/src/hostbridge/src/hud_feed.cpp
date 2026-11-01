// refii - Fable 3's HUD, as the guest states it: its typed element registries (texts, meters) and
// its screen fade.
//
// THE REGISTRIES ARE FABLE 2'S, UNDER A NEW ROOT. GUI = `*0x83556490`, the HUD H = `*(GUI+0x20)`,
// and H+0x24 holds the registry set: the HUD.bgf load sub_825C18C0 hands it to the builder
// (`lwz r3,0x24(r31)` / `bl sub_82675288` at 0x825C1914). The builder keeps fourteen registry
// pointers in a vector at holder+0x18 (begin) / +0x1C (end), [0]..[12] in Fable 2's order, each
// vtable's name slot returning its .bgf attribute: [1] MeterName, [4] PulserName, [6] TextName.
// A registry is a map at +4 whose head is at +8; a node keys the element name's FNV-1 at +12,
// its record at +16, `_Isnil` at +21; a record's element vector is +4..+8. The element writers
// are Fable 2's byte for byte: text sub_82733F30 (LHString at element+0x10, element+12 = 1 —
// 0x82733F70/0x82733F78), meter sub_8291F988 (current at element+0x14).
//
// The visible subtitle is one of these texts: the subtitler (`*(H+0x38)`) writes each page through
// sub_82920278 into the text registry under FNV-1("Subtitles") (`dword_8355906C`), as Fable 2's does.
//
// THE TOPBOX writes its strings the same way: its publisher sub_8217D880 (run from the manager's
// tick sub_82274950 at 0x82274A4C) composes `TopTitle` and `TopMain` as paragraphs and writes each
// into the text registry (sub_82920BC8 -> sub_82920278 -> sub_82733F30), and the button labels
// `RightTopButton` / `LeftTopButton` straight into it (0x8217DD30 / 0x8217DE14). Its visibility
// goes to the toggle and slide registries [9] and [10], which hold no readable state; the manager
// states it instead — see RefiiHostGetFable3TopBox.

#include <cstdint>
#include <cstring>

#include "host_abi.h"
#include "host_core.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestHalfAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kRegistryMeter = 1u;
constexpr uint32_t kRegistryText = 6u;
constexpr uint32_t kMaxNames = 512u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

/// The first element registered under each name in registry `index`, as parallel arrays. Returns
/// the number of names, which may exceed `capacity`. An explicit stack bounded at a red-black
/// tree's depth: another thread mutates the map, so a torn pointer truncates the answer.
uint32_t WalkRegistry(const uint8_t* base, uint32_t index, uint32_t* out_hashes, uint32_t* out_elements,
                      uint32_t capacity) {
    const uint32_t hud = Word(base, Word(base, kGuiManager) + 0x20u);
    const uint32_t holder = Word(base, hud + 0x24u);
    const uint32_t begin = Word(base, holder + 0x18u);
    const uint32_t end = Word(base, holder + 0x1Cu);
    if (!Plausible(begin) || end <= begin || (end - begin) > 0x400u || ((end - begin) / 4u) <= index) {
        return 0u;
    }

    const uint32_t registry = Word(base, begin + (index * 4u));
    const uint32_t head = Word(base, registry + 8u);
    const uint32_t root = Word(base, head + 4u);
    if (!Plausible(root)) {
        return 0u;
    }

    constexpr uint32_t kMaxDepth = 64u;
    uint32_t stack[kMaxDepth];
    uint32_t depth = 0u;
    uint32_t found = 0u;
    stack[depth++] = root;
    while (depth > 0u && found < kMaxNames) {
        const uint32_t node = stack[--depth];
        if (!Plausible(node) || GuestByteAt(base, node + 21u) != 0u) {
            continue;
        }

        const uint32_t record = Word(base, node + 16u);
        const uint32_t elements = Word(base, record + 4u);
        if (Plausible(elements) && Word(base, record + 8u) > elements) {
            const uint32_t element = Word(base, elements);
            if (Plausible(element)) {
                if (out_hashes != nullptr && out_elements != nullptr && found < capacity) {
                    out_hashes[found] = GuestWordAt(base, node + 12u);
                    out_elements[found] = element;
                }
                ++found;
            }
        }

        if (depth + 2u <= kMaxDepth) {
            stack[depth++] = Word(base, node + 0u);
            stack[depth++] = Word(base, node + 8u);
        }
    }

    return found;
}

/// The LHString whose handle sits at `at` as NUL-terminated UTF-8 (Rep: +0 big-endian UTF-16, +4
/// length in code units). Returns the bytes written including the terminator, one entry per name
/// even when the text is absent.
uint32_t WriteWideString(const uint8_t* base, uint32_t at, char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0u) {
        return 0u;
    }

    uint32_t written = 0u;
    const uint32_t rep = Word(base, at);
    const uint32_t chars = Word(base, rep);
    const uint32_t length = Plausible(rep) ? GuestWordAt(base, rep + 4u) : 0u;
    if (Plausible(chars) && length > 0u && length < 512u) {
        for (uint32_t c = 0; c < length; ++c) {
            const uint16_t unit = GuestHalfAt(base, chars + (c * 2u));
            if (unit == 0u || (unit >= 0xD800u && unit <= 0xDFFFu)) {
                break;
            }

            char encoded[3];
            uint32_t bytes = 0u;
            if (unit < 0x80u) {
                encoded[bytes++] = static_cast<char>(unit);
            } else if (unit < 0x800u) {
                encoded[bytes++] = static_cast<char>(0xC0u | (unit >> 6));
                encoded[bytes++] = static_cast<char>(0x80u | (unit & 0x3Fu));
            } else {
                encoded[bytes++] = static_cast<char>(0xE0u | (unit >> 12));
                encoded[bytes++] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
                encoded[bytes++] = static_cast<char>(0x80u | (unit & 0x3Fu));
            }

            if (written + bytes + 1u >= capacity) {
                break;
            }
            for (uint32_t b = 0; b < bytes; ++b) {
                out[written++] = encoded[b];
            }
        }
    }

    out[written++] = '\0';
    return written;
}

}  // namespace

extern "C" {

/// Every HUD text by its element name's FNV-1, the finished string the guest wrote (registry [6]).
/// Strings go to `text` NUL-separated in name order; returns the number of names.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudTexts(uint32_t* out_hashes, uint32_t capacity, char* text,
                                                   uint32_t text_capacity) {
    if (text != nullptr && text_capacity > 0u) {
        text[0] = '\0';
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
    const uint32_t found = WalkRegistry(base, kRegistryText, out_hashes, elements, want);
    const uint32_t stored = found < want ? found : want;
    uint32_t written = 0u;
    for (uint32_t i = 0; i < stored && text != nullptr; ++i) {
        written += WriteWideString(base, elements[i] + 0x10u, text + written,
                                   text_capacity > written ? text_capacity - written : 0u);
    }

    return found;
}

/// Every HUD meter by name, its current value 0..100 (registry [1], element+0x14 — sub_8291F988
/// steps it toward the target at +0x1C at the rate it writes to +0x18, 0x8291FA68).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudMeters(uint32_t* out_hashes, float* out_values, uint32_t capacity) {
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
    const uint32_t found = WalkRegistry(base, kRegistryMeter, out_hashes, elements, want);
    const uint32_t stored = found < want ? found : want;
    for (uint32_t i = 0; i < stored && out_values != nullptr; ++i) {
        const uint32_t raw = GuestWordAt(base, elements[i] + 0x14u);
        std::memcpy(&out_values[i], &raw, sizeof(float));
    }

    return found;
}

/// The screen fade: fader = `*(GUI+0x3C)`, Fable 2's layout — +0 progress t, +8 the colour it
/// started from and +12 the target, both B,G,R,A bytes (sub_825D5700 stores them, sub_82182CB8
/// lerps `src + (dst - src) * t`, the draw sub_82181B30 reads them as BGRA), +0x21 the lock
/// (LockScreenFade 0x8251BC20). Returns the fader's address, 0 when there is none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetScreenFade(float* out_rgba, float* out_progress, uint32_t* out_locked) {
    if (out_rgba != nullptr) {
        out_rgba[0] = out_rgba[1] = out_rgba[2] = out_rgba[3] = 0.0f;
    }
    if (out_progress != nullptr) *out_progress = 0.0f;
    if (out_locked != nullptr) *out_locked = 0u;

    const uint8_t* base = GuestBase();
    const uint32_t fade = base == nullptr ? 0u : Word(base, Word(base, kGuiManager) + 0x3Cu);
    if (!Plausible(fade)) {
        return 0u;
    }

    const uint32_t bits = GuestWordAt(base, fade);
    float t = 0.0f;
    std::memcpy(&t, &bits, sizeof(t));
    t = !(t > 0.0f) ? 0.0f : (t > 1.0f ? 1.0f : t);

    uint8_t lerped[4] = {0, 0, 0, 0};
    for (uint32_t i = 0; i < 4u; ++i) {
        const float from = static_cast<float>(GuestByteAt(base, fade + 8u + i));
        const float to = static_cast<float>(GuestByteAt(base, fade + 12u + i));
        lerped[i] = static_cast<uint8_t>(from + (t * (to - from)));
    }

    if (out_rgba != nullptr) {
        out_rgba[0] = static_cast<float>(lerped[2]) * (1.0f / 255.0f);
        out_rgba[1] = static_cast<float>(lerped[1]) * (1.0f / 255.0f);
        out_rgba[2] = static_cast<float>(lerped[0]) * (1.0f / 255.0f);
        out_rgba[3] = static_cast<float>(lerped[3]) * (1.0f / 255.0f);
    }
    if (out_progress != nullptr) *out_progress = t;
    if (out_locked != nullptr) *out_locked = GuestByteAt(base, fade + 0x21u);
    return fade;
}

/// The TopBox as its manager M = `*(H+0x4C)` holds it (SetTopBoxHidden 0x82519C38: `lwz r10,0x4C(r10)`
/// off H). Eight words:
///   [0] up: M+2 && !M+0 && !M+1. The publisher sets M+2 when it shows the front record
///       (`stb` at 0x8217D99C, slide 2 at 0x8217DEC8); sub_824B6F60 clears it in the tick the front
///       record changes, as it plays the hide slide (0x824B701C); M+0 / M+1 are the hidden flags
///       (SetTopBoxHidden `stb r3,0(r10)` at 0x82519C6C, then the hide slide).
///   [1] the front record's title key, rec+4 (the publisher's `sub_8230DA98(textDB, rec+4)` at
///       0x8217DA20 decides the title; when false it never rewrites `TopTitle`).
///   [2] its body key: the provider `*(rec+8)`'s +4. Every provider class (vtables 0x82091E58,
///       0x82091E60, 0x82091E68, 0x82091E70; getters sub_824B42D8 / 4418 / 45E8 / 46E0) starts
///       from `sub_8230DCA8(textDB, provider+4)`, which yields no string for an absent key, so
///       the body is empty exactly when the book has no string for it. No title and no body is
///       the button-only box, which rewrites neither `TopTitle` nor `TopMain` (0x8217DA64).
///   [3] A shown (rec+28), [4] A is a hold (rec+29), [5] Y shown (rec+31), [6] Y is a hold
///       (rec+32): the publisher's button count 1 + A + Y (0x8217DC44..0x8217DC60) and its
///       meter toggles (0x8217DD3C..0x8217DD50, 0x8217DE40).
///   [7] M, 0 when there is none.
/// The front record is sub_82194270's: the last record of the first non-empty channel, channels
/// a vector at M+0x4C..M+0x50, each a vector of record pointers at +4..+8.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFable3TopBox(uint32_t* out_words, uint32_t capacity) {
    constexpr uint32_t kWords = 8u;
    if (out_words == nullptr || capacity < kWords) {
        return 0u;
    }
    for (uint32_t i = 0; i < kWords; ++i) {
        out_words[i] = 0u;
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t hud = Word(base, Word(base, kGuiManager) + 0x20u);
    const uint32_t manager = Word(base, hud + 0x4Cu);
    if (!Plausible(manager)) {
        return 0u;
    }

    uint32_t record = 0u;
    const uint32_t channels_end = Word(base, manager + 0x50u);
    uint32_t channel = Word(base, manager + 0x4Cu);
    for (uint32_t guard = 0; Plausible(channel) && channel != channels_end && guard < 64u; ++guard, channel += 4u) {
        const uint32_t queue = Word(base, channel);
        const uint32_t begin = Word(base, queue + 4u);
        const uint32_t end = Word(base, queue + 8u);
        if (Plausible(begin) && end > begin && ((end - begin) & ~3u) != 0u) {
            record = Word(base, end - 4u);
            if (record != 0u) {
                break;
            }
        }
    }

    const bool hidden = GuestByteAt(base, manager + 0u) != 0u || GuestByteAt(base, manager + 1u) != 0u;
    const bool published = GuestByteAt(base, manager + 2u) != 0u;
    out_words[7] = manager;
    if (!Plausible(record)) {
        return kWords;
    }

    out_words[0] = (published && !hidden) ? 1u : 0u;
    out_words[1] = GuestWordAt(base, record + 4u);
    const uint32_t provider = Word(base, record + 8u);
    out_words[2] = Plausible(provider) ? GuestWordAt(base, provider + 4u) : 0u;
    out_words[3] = GuestByteAt(base, record + 28u);
    out_words[4] = GuestByteAt(base, record + 29u);
    out_words[5] = GuestByteAt(base, record + 31u);
    out_words[6] = GuestByteAt(base, record + 32u);
    return kWords;
}

}  // extern "C"
